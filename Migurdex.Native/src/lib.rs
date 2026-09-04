pub mod ffi;
pub mod http;
pub mod utils;

use ffi::sync::{fetch_batch, fetch_url, fetch_url_no_follow, fetch_url_post};
use ffi::types::RustApi;
use utils::alloc::{rust_alloc, rust_free, rust_free_headers};
use utils::fuzzy::rust_fuzzy_similarity;
use utils::js_unpack::rust_js_unpack;

#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

#[unsafe(no_mangle)]
pub extern "C" fn get_rust_api() -> *const RustApi {
    static API: RustApi = RustApi {
        fetch_url,
        fetch_url_post,
        rust_free,
        rust_alloc,
        fetch_url_no_follow,
        fetch_batch,
        rust_send_async: ffi::async_ffi::rust_send_async,
        rust_free_headers,
        rust_js_unpack,
        rust_fuzzy_similarity,
    };
    &API
}
