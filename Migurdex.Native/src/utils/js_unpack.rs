use super::alloc::to_raw_bytes;
use crate::ffi::types::NativeBuffer;
use regex::Regex;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::sync::LazyLock;

static PACKED_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"eval\s*\(\s*function\s*\(\s*p\s*,\s*a\s*,\s*c\s*,\s*k\s*,\s*e\s*,\s*d\s*\).+?\}\s*\(\s*['"](.*?)['"]\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*['"](.*?)['"]\s*\.split\(['"]\|['"]\)"#,
    ).expect("packed regex must compile")
});

static WORD_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\b[0-9a-zA-Z]+\b").expect("word regex must compile"));

pub fn unpack_packed(html: &str) -> Option<String> {
    let caps = PACKED_RE.captures(html)?;
    let packed = caps.get(1)?.as_str();
    let radix: u32 = caps.get(2)?.as_str().parse().ok()?;
    let words_str = caps.get(4)?.as_str();
    let words: Vec<&str> = words_str.split('|').collect();

    Some(unpack(packed, radix, &words))
}

pub fn unpack(packed: &str, radix: u32, words: &[&str]) -> String {
    WORD_RE
        .replace_all(packed, |caps: &regex::Captures| {
            let value = caps.get(0).unwrap().as_str();
            let index = unbase(value, radix);
            if index < words.len() && !words[index].is_empty() {
                words[index].to_string()
            } else {
                value.to_string()
            }
        })
        .into_owned()
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
pub unsafe extern "C" fn rust_js_unpack(html_ptr: *const c_char) -> NativeBuffer {
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

#[cfg(test)]
mod tests {
    use super::{unbase, unpack, unpack_packed};

    #[test]
    fn unbase_decimal() {
        assert_eq!(unbase("42", 10), 42);
    }

    #[test]
    fn unbase_base_62() {
        assert_eq!(unbase("10", 62), 62);
        assert_eq!(unbase("z", 62), 35);
        assert_eq!(unbase("Z", 62), 61);
    }

    #[test]
    fn unbase_out_of_range_digit_returns_zero() {
        assert_eq!(unbase("!", 62), 0);
    }

    #[test]
    fn unpack_replaces_known_words() {
        let words = ["alpha", "beta", "gamma"];
        assert_eq!(unpack("0 1 2", 10, &words), "alpha beta gamma");
    }

    #[test]
    fn unpack_keeps_unknown_or_out_of_range_tokens() {
        let words = ["alpha"];
        assert_eq!(unpack("0 9", 10, &words), "alpha 9");
    }

    #[test]
    fn unpack_packed_returns_none_for_plain_html() {
        assert!(unpack_packed("<html><body>hi</body></html>").is_none());
    }

    #[test]
    fn unpack_packed_decodes_classic_packer_payload() {
        let html =
            r#"eval(function(p,a,c,k,e,d){return p}( '0 1',10,2,'alpha|beta'.split('|'),0,{}))"#;
        assert_eq!(unpack_packed(html).as_deref(), Some("alpha beta"));
    }
}
