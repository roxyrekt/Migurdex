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

    for (i, &sc) in s_chars.iter().enumerate() {
        v1[0] = i + 1;

        for (j, &tc) in t_chars.iter().enumerate() {
            let cost = if sc == tc { 0 } else { 1 };
            v1[j + 1] = (v1[j] + 1).min(v0[j + 1] + 1).min(v0[j] + cost);
        }

        v0.copy_from_slice(&v1);
    }

    let dist = v0[m];
    let max_len = n.max(m) as f64;

    1.0 - (dist as f64 / max_len)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rust_fuzzy_similarity(
    str1_ptr: *const c_char,
    str2_ptr: *const c_char,
) -> f64 {
    if str1_ptr.is_null() || str2_ptr.is_null() {
        return 0.0;
    }

    let str1 = unsafe { CStr::from_ptr(str1_ptr).to_str().unwrap_or_default() };
    let str2 = unsafe { CStr::from_ptr(str2_ptr).to_str().unwrap_or_default() };

    calculate_similarity(str1, str2)
}

#[cfg(test)]
mod tests {
    use super::calculate_similarity;

    #[test]
    fn identical_strings_score_one() {
        assert_eq!(calculate_similarity("Naruto", "Naruto"), 1.0);
    }

    #[test]
    fn case_and_whitespace_are_normalized() {
        assert_eq!(calculate_similarity("  Naruto ", "naruto"), 1.0);
    }

    #[test]
    fn empty_input_scores_zero() {
        assert_eq!(calculate_similarity("", "Naruto"), 0.0);
        assert_eq!(calculate_similarity("Naruto", ""), 0.0);
    }

    #[test]
    fn single_edit_is_penalized_but_close() {
        let score = calculate_similarity("abcde", "abxde");
        assert!(score > 0.7 && score < 1.0, "score={score}");
    }

    #[test]
    fn completely_different_strings_score_low() {
        assert!(calculate_similarity("aaaa", "zzzz") < 0.5);
    }

    #[test]
    fn unicode_is_compared_by_char_not_byte() {
        assert_eq!(calculate_similarity("seker", "seker"), 1.0);
        assert!(calculate_similarity("seker", "sxkxr") < 1.0);
    }

    #[test]
    fn score_is_symmetric() {
        let a = calculate_similarity("Shingeki no Kyojin", "Attack on Titan");
        let b = calculate_similarity("Attack on Titan", "Shingeki no Kyojin");
        assert!((a - b).abs() < f64::EPSILON);
    }

    #[test]
    fn prefix_scores_higher_than_unrelated() {
        let prefix = calculate_similarity("Naruto Shippuuden", "Naruto");
        let unrelated = calculate_similarity("Naruto Shippuuden", "Bleach");
        assert!(prefix > unrelated, "prefix={prefix} unrelated={unrelated}");
    }
}
