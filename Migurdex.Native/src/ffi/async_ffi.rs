use std::slice;
use wreq_util::Profile;

use crate::ffi::types::{FfiCallback, FfiHeader, FfiRequestOptions, FfiResponse};
use crate::http::client::{get_client_with_options, ClientKey};
use crate::http::emulation::parse_emulation;
use crate::http::rate_limit::RUNTIME;
use crate::utils::alloc::{to_raw_bytes, to_raw_layout};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rust_send_async(
    task_id: i64,
    url_ptr: *const u8,
    url_len: usize,
    method_ptr: *const u8,
    method_len: usize,
    headers: *const FfiHeader,
    headers_len: usize,
    body_ptr: *const u8,
    body_len: usize,
    options: *const FfiRequestOptions,
    callback: FfiCallback,
) {
    let url = if !url_ptr.is_null() && url_len > 0 {
        let slice = unsafe { slice::from_raw_parts(url_ptr, url_len) };
        std::str::from_utf8(slice).unwrap_or_default().to_string()
    } else {
        String::new()
    };

    let method = if !method_ptr.is_null() && method_len > 0 {
        let slice = unsafe { slice::from_raw_parts(method_ptr, method_len) };
        std::str::from_utf8(slice).unwrap_or_default().to_string()
    } else {
        String::from("GET")
    };

    let body_data = if !body_ptr.is_null() && body_len > 0 {
        let slice = unsafe { slice::from_raw_parts(body_ptr, body_len) };
        Some(slice.to_vec())
    } else {
        None
    };

    let mut req_headers = Vec::new();
    let mut emulation = Profile::Chrome147;
    let mut no_follow = false;
    let mut skip_cert_verify = false;

    if !options.is_null() {
        let opt = unsafe { &*options };
        no_follow = opt.no_follow;
        skip_cert_verify = opt.skip_cert_verify;

        if !opt.emulation_ptr.is_null() && opt.emulation_len > 0 {
            let emu_slice = unsafe { slice::from_raw_parts(opt.emulation_ptr, opt.emulation_len) };
            if let Ok(emu_str) = std::str::from_utf8(emu_slice) {
                if !emu_str.is_empty() {
                    emulation = parse_emulation(emu_str);
                }
            }
        }
    }

    if !headers.is_null() && headers_len > 0 {
        let headers_slice = unsafe { slice::from_raw_parts(headers, headers_len) };
        for h in headers_slice {
            let key = if !h.key_ptr.is_null() && h.key_len > 0 {
                let key_slice = unsafe { slice::from_raw_parts(h.key_ptr, h.key_len) };
                std::str::from_utf8(key_slice).unwrap_or_default()
            } else {
                ""
            };
            let val = if !h.val_ptr.is_null() && h.val_len > 0 {
                let val_slice = unsafe { slice::from_raw_parts(h.val_ptr, h.val_len) };
                std::str::from_utf8(val_slice).unwrap_or_default()
            } else {
                ""
            };

            if key.eq_ignore_ascii_case("X-Rust-Emulate") {
                emulation = parse_emulation(val);
            } else if key.eq_ignore_ascii_case("X-Skip-Cert-Verify") {
                if val.eq_ignore_ascii_case("true") || val == "1" {
                    skip_cert_verify = true;
                }
            } else if !key.is_empty() {
                req_headers.push((key.to_string(), val.to_string()));
            }
        }
    }

    RUNTIME.spawn(async move {
        let client = get_client_with_options(ClientKey {
            emulation,
            skip_cert_verify,
            no_redirect: no_follow,
        });

        let mut builder = match method.as_str() {
            "POST" => client.post(&url).body(body_data.unwrap_or_default()),
            "PUT" => client.put(&url).body(body_data.unwrap_or_default()),
            _ => client.get(&url),
        };

        for (k, v) in req_headers {
            builder = builder.header(k, v);
        }

        match builder.send().await {
            Ok(resp) => {
                let status = resp.status().as_u16();

                let headers_data: Vec<(String, String)> = resp
                    .headers()
                    .iter()
                    .filter_map(|(k, v)| {
                        v.to_str()
                            .ok()
                            .map(|vs| (k.as_str().to_string(), vs.to_string()))
                    })
                    .collect();

                match resp.bytes().await {
                    Ok(bytes) => {
                        let (b_ptr, b_len) = to_raw_bytes(bytes.to_vec());

                        let mut ffi_headers = Vec::new();
                        for (k, v) in &headers_data {
                            ffi_headers.push(FfiHeader {
                                key_ptr: k.as_ptr(),
                                key_len: k.len(),
                                val_ptr: v.as_ptr(),
                                val_len: v.len(),
                            });
                        }

                        let (h_ptr, h_len) = to_raw_layout(ffi_headers);

                        let ffi_resp = FfiResponse {
                            task_id,
                            status,
                            headers: h_ptr,
                            headers_len: h_len,
                            body_ptr: b_ptr,
                            body_len: b_len,
                            error_ptr: std::ptr::null_mut(),
                            error_len: 0,
                        };
                        unsafe { callback(ffi_resp) };
                    }
                    Err(e) => unsafe { send_error(task_id, format!("Body read error: {:?}", e), callback) },
                }
            }
            Err(e) => unsafe { send_error(task_id, format!("Network error: {:?}", e), callback) },
        }
    });
}

unsafe fn send_error(task_id: i64, err: String, callback: FfiCallback) {
    let (ptr, len) = to_raw_bytes(err.into_bytes());
    unsafe {
        callback(FfiResponse {
            task_id,
            status: 0,
            headers: std::ptr::null(),
            headers_len: 0,
            body_ptr: std::ptr::null_mut(),
            body_len: 0,
            error_ptr: ptr,
            error_len: len,
        });
    }
}
