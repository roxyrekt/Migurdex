use futures::future::join_all;
use serde::{Deserialize, Serialize};
use std::ffi::CStr;
use std::os::raw::c_char;
use std::time::Duration;
use wreq_util::Profile;

use crate::ffi::types::NativeBuffer;
use crate::http::client::{get_client, get_no_redirect_client};
use crate::http::emulation::{apply_headers, extract_emulation_from_json, parse_emulation};
use crate::http::rate_limit::{get_host_semaphore, RUNTIME};
use crate::utils::alloc::to_raw_bytes;

#[derive(Deserialize)]
struct BatchRequest {
    url: String,
    method: Option<String>,
    body: Option<String>,
    headers: Option<std::collections::HashMap<String, String>>,
    no_follow: Option<bool>,
}

#[derive(Serialize)]
struct BatchResponse {
    url: String,
    status: u16,
    body: String,
    error: Option<String>,
    location: Option<String>,
}

pub extern "C" fn fetch_url_post(
    url_ptr: *const c_char,
    body_ptr: *const c_char,
    headers_ptr: *const c_char,
) -> NativeBuffer {
    if url_ptr.is_null() || body_ptr.is_null() || headers_ptr.is_null() {
        return NativeBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
    }

    let url_str = unsafe { CStr::from_ptr(url_ptr).to_str().unwrap_or_default() };
    let body_str = unsafe { CStr::from_ptr(body_ptr).to_str().unwrap_or_default() };
    let headers_str = unsafe { CStr::from_ptr(headers_ptr).to_str().unwrap_or_default() };

    let emulation = extract_emulation_from_json(headers_str);
    let client = get_client(emulation);

    let sem = get_host_semaphore(url_str);
    let result = RUNTIME.block_on(async {
        let _permit = sem.acquire().await.unwrap();
        let delay = (url_str.len() % 50) as u64;
        tokio::time::sleep(Duration::from_millis(delay)).await;

        let mut req = client.post(url_str).body(body_str.to_string());
        req = apply_headers(req, headers_str);

        match req.send().await {
            Ok(resp) => resp.text().await.unwrap_or_default(),
            Err(e) => {
                println!("[Rust] POST Error: {:?}", e);
                String::new()
            }
        }
    });

    let (ptr, len) = to_raw_bytes(result.into_bytes());
    NativeBuffer { ptr, len }
}

pub extern "C" fn fetch_url_no_follow(
    url_ptr: *const c_char,
    headers_ptr: *const c_char,
) -> NativeBuffer {
    if url_ptr.is_null() || headers_ptr.is_null() {
        return NativeBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
    }

    let url_str = unsafe { CStr::from_ptr(url_ptr).to_str().unwrap_or_default() };
    let headers_str = unsafe { CStr::from_ptr(headers_ptr).to_str().unwrap_or_default() };

    let emulation = extract_emulation_from_json(headers_str);
    let client = get_no_redirect_client(emulation);

    let sem = get_host_semaphore(url_str);
    let result = RUNTIME.block_on(async {
        let _permit = sem.acquire().await.unwrap();
        let delay = (url_str.len() % 50) as u64;
        tokio::time::sleep(Duration::from_millis(delay)).await;

        let mut req = client.get(url_str);
        req = apply_headers(req, headers_str);

        match req.send().await {
            Ok(resp) => {
                let status = resp.status().as_u16();
                if (301..=308).contains(&status) && status != 304 && status != 305 || status == 307 || status == 308 {
                    if let Some(loc) = resp.headers().get("location") {
                        let loc_str = loc.to_str().unwrap_or_default();
                        println!("[Rust] NoFollow redirect {} -> {}", url_str, loc_str);
                        return loc_str.to_string();
                    }
                }
                println!("[Rust] NoFollow: no redirect (status {})", status);
                String::new()
            }
            Err(e) => {
                println!("[Rust] NoFollow Error: {:?}", e);
                String::new()
            }
        }
    });

    let (ptr, len) = to_raw_bytes(result.into_bytes());
    NativeBuffer { ptr, len }
}

pub extern "C" fn fetch_url(url_ptr: *const c_char, headers_ptr: *const c_char) -> NativeBuffer {
    if url_ptr.is_null() || headers_ptr.is_null() {
        return NativeBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
    }

    let url_str = unsafe { CStr::from_ptr(url_ptr).to_str().unwrap_or_default() };
    let headers_str = unsafe { CStr::from_ptr(headers_ptr).to_str().unwrap_or_default() };

    let emulation = extract_emulation_from_json(headers_str);
    let client = get_client(emulation);

    let sem = get_host_semaphore(url_str);
    let result = RUNTIME.block_on(async {
        let _permit = sem.acquire().await.unwrap();
        let delay = (url_str.len() % 50) as u64;
        tokio::time::sleep(Duration::from_millis(delay)).await;

        let mut req = client.get(url_str);
        req = apply_headers(req, headers_str);

        match req.send().await {
            Ok(resp) => {
                let final_url = resp.uri().to_string();
                let text = resp.text().await.unwrap_or_default();
                if final_url != url_str {
                    println!("[Rust] Redirected to: {}", final_url);
                }
                text
            }
            Err(e) => {
                println!("[Rust] GET Error: {:?}", e);
                String::new()
            }
        }
    });

    let (ptr, len) = to_raw_bytes(result.into_bytes());
    NativeBuffer { ptr, len }
}

pub extern "C" fn fetch_batch(requests_json_ptr: *const c_char) -> NativeBuffer {
    if requests_json_ptr.is_null() {
        return NativeBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
    }

    let json_str = unsafe {
        CStr::from_ptr(requests_json_ptr)
            .to_str()
            .unwrap_or_default()
    };

    let requests: Vec<BatchRequest> = match serde_json::from_str(json_str) {
        Ok(reqs) => reqs,
        Err(e) => {
            let err = format!("[{{ \"error\": \"Invalid JSON input: {}\" }}]", e);
            let (ptr, len) = to_raw_bytes(err.into_bytes());
            return NativeBuffer { ptr, len };
        }
    };

    let result = RUNTIME.block_on(async {
        let mut futures = Vec::new();

        for req in requests {
            let mut emulation = Profile::Chrome147;
            if let Some(ref headers) = req.headers {
                if let Some(val) = headers
                    .get("X-Rust-Emulate")
                    .or_else(|| headers.get("x-rust-emulate"))
                {
                    emulation = parse_emulation(val);
                }
            }

            let sem = get_host_semaphore(&req.url);
            let url_for_delay = req.url.clone();
            let fut = async move {
                let _permit = sem.acquire().await.unwrap();
                let delay = (url_for_delay.len() % 50) as u64;
                tokio::time::sleep(Duration::from_millis(delay)).await;

                let client = if req.no_follow.unwrap_or(false) {
                    get_no_redirect_client(emulation)
                } else {
                    get_client(emulation)
                };

                let mut builder = match req.method.as_deref() {
                    Some("POST") => client.post(&req.url).body(req.body.unwrap_or_default()),
                    _ => client.get(&req.url),
                };

                if let Some(headers) = req.headers {
                    for (k, v) in headers {
                        if !k.eq_ignore_ascii_case("X-Rust-Emulate") {
                            builder = builder.header(k, v);
                        }
                    }
                }

                match builder.send().await {
                    Ok(resp) => {
                        let status = resp.status().as_u16();
                        let location = resp
                            .headers()
                            .get("location")
                            .and_then(|v| v.to_str().ok())
                            .map(|s| s.to_string());
                        let body = resp.text().await.unwrap_or_default();

                        BatchResponse {
                            url: req.url,
                            status,
                            body,
                            error: None,
                            location,
                        }
                    }
                    Err(e) => BatchResponse {
                        url: req.url,
                        status: 0,
                        body: String::new(),
                        error: Some(e.to_string()),
                        location: None,
                    },
                }
            };
            futures.push(fut);
        }

        let responses = join_all(futures).await;
        serde_json::to_string(&responses).unwrap_or_else(|_| "[]".to_string())
    });

    let (ptr, len) = to_raw_bytes(result.into_bytes());
    NativeBuffer { ptr, len }
}
