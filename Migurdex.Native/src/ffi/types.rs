use std::os::raw::c_char;

#[repr(C)]
pub struct FfiHeader {
    pub key_ptr: *const u8,
    pub key_len: usize,
    pub val_ptr: *const u8,
    pub val_len: usize,
}

#[repr(C)]
pub struct FfiResponse {
    pub task_id: i64,
    pub status: u16,
    pub headers: *const FfiHeader,
    pub headers_len: usize,
    pub body_ptr: *mut u8,
    pub body_len: usize,
    pub error_ptr: *mut u8,
    pub error_len: usize,
}

#[repr(C)]
pub struct FfiRequestOptions {
    pub no_follow: bool,
    pub skip_cert_verify: bool,
    pub emulation_ptr: *const u8,
    pub emulation_len: usize,
}

pub type FfiCallback = unsafe extern "C" fn(response: FfiResponse);

#[repr(C)]
pub struct NativeBuffer {
    pub ptr: *mut u8,
    pub len: usize,
}

#[repr(C)]
pub struct RustApi {
    pub fetch_url: extern "C" fn(*const c_char, *const c_char) -> NativeBuffer,
    pub fetch_url_post: extern "C" fn(*const c_char, *const c_char, *const c_char) -> NativeBuffer,
    pub rust_free: extern "C" fn(*mut u8, usize),
    pub rust_alloc: extern "C" fn(usize) -> *mut u8,
    pub fetch_url_no_follow: extern "C" fn(*const c_char, *const c_char) -> NativeBuffer,
    pub fetch_batch: extern "C" fn(*const c_char) -> NativeBuffer,
    pub rust_send_async: unsafe extern "C" fn(
        i64,
        *const u8,
        usize,
        *const u8,
        usize,
        *const FfiHeader,
        usize,
        *const u8,
        usize,
        *const FfiRequestOptions,
        FfiCallback,
    ),
    pub rust_free_headers: unsafe extern "C" fn(*mut FfiHeader, usize),
    pub rust_js_unpack: extern "C" fn(*const c_char) -> NativeBuffer,
    pub rust_fuzzy_similarity: extern "C" fn(*const c_char, *const c_char) -> f64,
}
