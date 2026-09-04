use dashmap::DashMap;
use std::sync::{Arc, LazyLock};
use tokio::runtime::Runtime;
use tokio::sync::Semaphore;
use url::Url;

static SEMAPHORES: LazyLock<DashMap<String, Arc<Semaphore>>> = LazyLock::new(DashMap::new);

pub static RUNTIME: LazyLock<Runtime> =
    LazyLock::new(|| Runtime::new().expect("Failed to create Tokio runtime"));

pub fn get_host_semaphore(url_str: &str) -> Arc<Semaphore> {
    let host = Url::parse(url_str)
        .map(|u| u.host_str().unwrap_or("unknown").to_string())
        .unwrap_or_else(|_| "unknown".to_string());

    SEMAPHORES
        .entry(host)
        .or_insert_with(|| Arc::new(Semaphore::new(10)))
        .value()
        .clone()
}
