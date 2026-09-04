use std::alloc::{alloc, dealloc, Layout};
use super::super::ffi::types::FfiHeader;

#[unsafe(no_mangle)]
pub extern "C" fn rust_alloc(size: usize) -> *mut u8 {
    if size == 0 {
        return std::ptr::null_mut();
    }
    let layout = match Layout::from_size_align(size, 8) {
        Ok(l) => l,
        Err(_) => return std::ptr::null_mut(),
    };
    unsafe { alloc(layout) }
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_free(ptr: *mut u8, size: usize) {
    if ptr.is_null() || size == 0 {
        return;
    }
    if let Ok(layout) = Layout::from_size_align(size, 8) {
        unsafe { dealloc(ptr, layout) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rust_free_buffer(ptr: *mut u8, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }
    if let Ok(layout) = Layout::from_size_align(len, 8) {
        unsafe { dealloc(ptr, layout) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rust_free_headers(ptr: *mut FfiHeader, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }
    let _ = unsafe { Vec::from_raw_parts(ptr, len, len) };
}

pub fn to_raw_bytes(vec: Vec<u8>) -> (*mut u8, usize) {
    let len = vec.len();
    if len == 0 {
        return (std::ptr::null_mut(), 0);
    }
    let layout = match Layout::from_size_align(len, 8) {
        Ok(l) => l,
        Err(_) => return (std::ptr::null_mut(), 0),
    };
    unsafe {
        let ptr = alloc(layout);
        if ptr.is_null() {
            return (std::ptr::null_mut(), 0);
        }
        std::ptr::copy_nonoverlapping(vec.as_ptr(), ptr, len);
        (ptr, len)
    }
}

pub fn to_raw_layout<T>(mut vec: Vec<T>) -> (*mut T, usize) {
    let len = vec.len();
    if len == 0 {
        return (std::ptr::null_mut(), 0);
    }
    let ptr = vec.as_mut_ptr();
    std::mem::forget(vec);
    (ptr, len)
}
