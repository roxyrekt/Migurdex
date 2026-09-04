use wreq_util::Profile;

pub fn parse_emulation(emulate_str: &str) -> Profile {
    let clean_str = emulate_str.to_lowercase().replace(['-', '.'], "_");
    let json_str = format!("\"{}\"", clean_str);

    match serde_json::from_str::<Profile>(&json_str) {
        Ok(profile) => profile,
        Err(_) => {
            println!(
                "[Rust] Warning: Unknown emulation '{}', falling back to Chrome147",
                emulate_str
            );
            Profile::Chrome147
        }
    }
}

pub fn extract_emulation_from_json(headers_json: &str) -> Profile {
    if let Ok(map) = serde_json::from_str::<std::collections::HashMap<String, String>>(headers_json)
    {
        if let Some(val) = map
            .get("X-Rust-Emulate")
            .or_else(|| map.get("x-rust-emulate"))
        {
            return parse_emulation(val);
        }
    }
    Profile::Chrome147
}

pub fn apply_headers(builder: wreq::RequestBuilder, headers_json: &str) -> wreq::RequestBuilder {
    let mut builder = builder;
    if let Ok(map) = serde_json::from_str::<std::collections::HashMap<String, String>>(headers_json)
    {
        for (k, v) in map {
            if !k.eq_ignore_ascii_case("X-Rust-Emulate") {
                builder = builder.header(k, v);
            }
        }
    }
    builder
}
