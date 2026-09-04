use std::ffi::CStr;
use std::os::raw::c_char;

pub fn calculate_similarity(source: &str, target: &str) -> f64 {
    if source.is_empty() || target.is_empty() {
        return 0.0;
    }

    let source_clean = source.trim().to_lowercase();
    let target_clean = target.trim().to_lowercase();

    if source_clean == target_clean {
        return 1.0;
    }

    let s_chars: Vec<char> = source_clean.chars().collect();
    let t_chars: Vec<char> = target_clean.chars().collect();

    let n = s_chars.len();
    let m = t_chars.len();

    if n == 0 || m == 0 {
        return 0.0;
    }

    let mut v0: Vec<usize> = (0..=m).collect();
    let mut v1: Vec<usize> = vec![0; m + 1];

    for i in 0..n {
        v1[0] = i + 1;

        for j in 0..m {
            let cost = if s_chars[i] == t_chars[j] { 0 } else { 1 };
            v1[j + 1] = (v1[j] + 1).min(v0[j + 1] + 1).min(v0[j] + cost);
        }

        v0.copy_from_slice(&v1);
    }

    let dist = v0[m];
    let max_len = n.max(m) as f64;

    1.0 - (dist as f64 / max_len)
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_fuzzy_similarity(str1_ptr: *const c_char, str2_ptr: *const c_char) -> f64 {
    if str1_ptr.is_null() || str2_ptr.is_null() {
        return 0.0;
    }

    let str1 = unsafe { CStr::from_ptr(str1_ptr).to_str().unwrap_or_default() };
    let str2 = unsafe { CStr::from_ptr(str2_ptr).to_str().unwrap_or_default() };

    calculate_similarity(str1, str2)
}
