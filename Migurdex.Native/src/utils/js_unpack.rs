use std::ffi::CStr;
use std::os::raw::c_char;
use regex::Regex;
use super::alloc::to_raw_bytes;
use crate::ffi::types::NativeBuffer;

pub fn unpack_packed(html: &str) -> Option<String> {
    let re = Regex::new(
        r#"eval\s*\(\s*function\s*\(\s*p\s*,\s*a\s*,\s*c\s*,\s*k\s*,\s*e\s*,\s*d\s*\).+?\}\s*\(\s*['"](.*?)['"]\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*['"](.*?)['"]\s*\.split\(['"]\|['"]\)"#
    ).ok()?;

    let caps = re.captures(html)?;
    let packed = caps.get(1)?.as_str();
    let radix: u32 = caps.get(2)?.as_str().parse().ok()?;
    let words_str = caps.get(4)?.as_str();
    let words: Vec<&str> = words_str.split('|').collect();

    Some(unpack(packed, radix, &words))
}

pub fn unpack(packed: &str, radix: u32, words: &[&str]) -> String {
    let re = match Regex::new(r"\b[0-9a-zA-Z]+\b") {
        Ok(r) => r,
        Err(_) => return packed.to_string(),
    };

    re.replace_all(packed, |caps: &regex::Captures| {
        let value = caps.get(0).unwrap().as_str();
        let index = unbase(value, radix);
        if index < words.len() && !words[index].is_empty() {
            words[index].to_string()
        } else {
            value.to_string()
        }
    }).into_owned()
}

fn unbase(value: &str, radix: u32) -> usize {
    if radix <= 10 {
        return value.parse::<usize>().unwrap_or(0);
    }

    const ALPHABET: &[u8] = b"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    let mut res: usize = 0;
    let mut power: usize = 1;

    for &b in value.as_bytes().iter().rev() {
        let digit = match ALPHABET.iter().position(|&x| x == b) {
            Some(idx) if (idx as u32) < radix => idx,
            _ => return 0,
        };

        res = res.saturating_add(digit.saturating_mul(power));
        power = power.saturating_mul(radix as usize);
    }

    res
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_js_unpack(html_ptr: *const c_char) -> NativeBuffer {
    if html_ptr.is_null() {
        return NativeBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
    }

    let html_str = unsafe { CStr::from_ptr(html_ptr).to_str().unwrap_or_default() };
    let unpacked = unpack_packed(html_str).unwrap_or_default();

    let (ptr, len) = to_raw_bytes(unpacked.into_bytes());
    NativeBuffer { ptr, len }
}
