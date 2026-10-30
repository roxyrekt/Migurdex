use super::super::ffi::types::FfiHeader;
use std::alloc::{Layout, alloc, dealloc};

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
pub unsafe extern "C" fn rust_free(ptr: *mut u8, size: usize) {
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
    let headers = unsafe { std::slice::from_raw_parts(ptr, len) };
    for h in headers {
        if !h.key_ptr.is_null()
            && h.key_len > 0
            && let Ok(layout) = Layout::from_size_align(h.key_len, 8)
        {
            unsafe { dealloc(h.key_ptr as *mut u8, layout) };
        }
        if !h.val_ptr.is_null()
            && h.val_len > 0
            && let Ok(layout) = Layout::from_size_align(h.val_len, 8)
        {
            unsafe { dealloc(h.val_ptr as *mut u8, layout) };
        }
    }
    let _ = unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)) };
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

pub unsafe fn free_raw_bytes(ptr: *mut u8, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }
    if let Ok(layout) = Layout::from_size_align(len, 8) {
        unsafe { dealloc(ptr, layout) };
    }
}

pub fn to_raw_layout<T>(vec: Vec<T>) -> (*mut T, usize) {
    let slice = vec.into_boxed_slice();
    let len = slice.len();
    if len == 0 {
        return (std::ptr::null_mut(), 0);
    }
    (Box::into_raw(slice) as *mut T, len)
}
