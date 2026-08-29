use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

const FRANKFURTER_URL: &str = "https://api.frankfurter.app/latest?from=USD&to=";
const CACHE_TTL_SECS: u64 = 24 * 3600;
const FETCH_TIMEOUT: Duration = Duration::from_secs(10);
/// Defensive bounds on any fetched FX rate. Outside [0.0001, 1_000_000] the rate is either
/// a parser bug or a tampered response; we refuse it so the UI never multiplies a NaN or
/// wild value into displayed costs.
const MIN_VALID_FX_RATE: f64 = 0.0001;
const MAX_VALID_FX_RATE: f64 = 1_000_000.0;

/// Currency metadata the frontend renders against. `rate` is USD -> target; the UI
/// multiplies each raw USD number by `rate` and prefixes `symbol` for display.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CurrencyApplied {
    pub code: String,
    pub symbol: String,
    pub rate: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Entry {
    rate: f64,
    saved_at: u64,
}

pub struct FxCache {
    entries: Mutex<HashMap<String, Entry>>,
}

impl FxCache {
    pub fn new() -> Self {
        let entries = load_from_disk().unwrap_or_default();
        FxCache {
            entries: Mutex::new(entries),
        }
    }

    /// Returns a cached-or-fresh rate. Tries cache first, then Frankfurter if stale. Any
    /// response that fails the sanity bounds is dropped and the cached (possibly stale)
    /// value is returned instead.
    pub async fn rate_for(&self, code: &str) -> Option<f64> {
        if code == "USD" {
            return Some(1.0);
        }

        {
            let guard = self.entries.lock().ok()?;
            if let Some(entry) = guard.get(code) {
                if now_secs().saturating_sub(entry.saved_at) < CACHE_TTL_SECS {
                    return Some(entry.rate);
                }
            }
        }

        match fetch_rate(code).await {
            Some(fresh) if is_valid(fresh) => {
                if let Ok(mut guard) = self.entries.lock() {
                    guard.insert(
                        code.to_string(),
                        Entry {
                            rate: fresh,
                            saved_at: now_secs(),
                        },
                    );
                    let _ = save_to_disk(&guard);
                }
                Some(fresh)
            }
            _ => {
                // Fetch failed or out-of-band; serve stale cached value if any.
                let guard = self.entries.lock().ok()?;
                guard.get(code).map(|e| e.rate)
            }
        }
    }
}

fn is_valid(rate: f64) -> bool {
    rate.is_finite() && (MIN_VALID_FX_RATE..=MAX_VALID_FX_RATE).contains(&rate)
}

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

fn cache_path() -> PathBuf {
    dirs::cache_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("codeburn-menubar")
        .join("fx-rates.json")
}

fn load_from_disk() -> Option<HashMap<String, Entry>> {
    let bytes = fs::read(cache_path()).ok()?;
    let parsed: HashMap<String, Entry> = serde_json::from_slice(&bytes).ok()?;
    Some(parsed.into_iter().filter(|(_, e)| is_valid(e.rate)).collect())
}

fn save_to_disk(entries: &HashMap<String, Entry>) -> Option<()> {
    let path = cache_path();
    let parent = path.parent()?;
    fs::create_dir_all(parent).ok()?;
    let serialized = serde_json::to_vec(entries).ok()?;
    let tmp = path.with_extension("tmp");
    fs::write(&tmp, serialized).ok()?;
    fs::rename(&tmp, path).ok()?;
    Some(())
}

async fn fetch_rate(code: &str) -> Option<f64> {
    let client = reqwest::Client::builder()
        .timeout(FETCH_TIMEOUT)
        .https_only(true)
        .build()
        .ok()?;
    let url = format!("{}{}", FRANKFURTER_URL, code);
    let response = client.get(&url).send().await.ok()?;
    if !response.status().is_success() {
        return None;
    }
    let body: serde_json::Value = response.json().await.ok()?;
    body.get("rates")?.get(code)?.as_f64()
}

/// Prefers a handwritten glyph over whatever Intl returns for a given code, since some
/// locales produce "US$" / "CA$" which reads as noise. Mirrors the Swift symbol override
/// table so both apps display identical strings for the same code.
pub fn symbol_for(code: &str) -> String {
    match code {
        "USD" | "CAD" | "AUD" | "NZD" | "HKD" | "SGD" | "MXN" => "$".into(),
        "EUR" => "\u{20AC}".into(),
        "GBP" => "\u{00A3}".into(),
        "JPY" | "CNY" => "\u{00A5}".into(),
        "KRW" => "\u{20A9}".into(),
        "INR" => "\u{20B9}".into(),
        "BRL" => "R$".into(),
        "CHF" => "CHF".into(),
        "SEK" | "DKK" => "kr".into(),
        "ZAR" => "R".into(),
        _ => code.into(),
    }
}
