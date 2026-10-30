#![allow(clippy::missing_safety_doc)]

pub mod ffi;
pub mod http;
pub mod utils;

use ffi::types::RustApi;
use utils::alloc::{rust_alloc, rust_free, rust_free_headers};
use utils::fuzzy::rust_fuzzy_similarity;
use utils::js_unpack::rust_js_unpack;

#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

#[unsafe(no_mangle)]
pub extern "C" fn get_rust_api() -> *const RustApi {
    static API: RustApi = RustApi {
        rust_free,
        rust_alloc,
        rust_send_async: ffi::async_ffi::rust_send_async,
        rust_free_headers,
        rust_js_unpack,
        rust_fuzzy_similarity,
    };
    &API
}
