//! Claude subscription usage (the "Plan" insight). Mirrors the macOS SubscriptionClient:
//! read Claude Code's OAuth credentials, call the usage endpoint, adopt a token Claude Code
//! has already rotated on 401 (never spending the shared refresh token), and keep a rolling
//! snapshot file so a freshly reset window can still show last cycle's final.

use std::fs;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;

const CREDENTIALS_RELATIVE_PATH: &str = ".claude/.credentials.json";
const USAGE_URL: &str = "https://api.anthropic.com/api/oauth/usage";
const BETA_HEADER: &str = "oauth-2025-04-20";
const USER_AGENT: &str = "claude-code/2.1.0";
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);
const MAX_CREDENTIAL_BYTES: u64 = 64 * 1024;
const SNAPSHOT_FILENAME: &str = "subscription-snapshots.json";
const SNAPSHOT_RETENTION: Duration = Duration::from_secs(30 * 24 * 3600);
const WINDOW_KEYS: [(&str, &str); 4] = [
    ("five_hour", "5-hour window"),
    ("seven_day", "7-day total"),
    ("seven_day_opus", "7-day Opus"),
    ("seven_day_sonnet", "7-day Sonnet"),
];

#[derive(Debug, Clone, Serialize)]
pub struct PlanWindow {
    pub key: String,
    pub label: String,
    /// 0..100
    pub percent: f64,
    /// RFC 3339 timestamp of the next reset, when the API supplied one.
    pub resets_at: Option<String>,
    /// Final percent reached in the immediately prior cycle, from the snapshot store.
    pub previous_final: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum PlanUsage {
    Ok {
        tier: String,
        raw_tier: Option<String>,
        windows: Vec<PlanWindow>,
        fetched_at: String,
    },
    NoCredentials,
    Failed {
        message: String,
    },
}

pub struct PlanClient {
    snapshot_lock: Mutex<()>,
}

impl PlanClient {
    pub fn new() -> Self {
        PlanClient {
            snapshot_lock: Mutex::new(()),
        }
    }

    pub async fn fetch(&self) -> Result<PlanUsage> {
        let creds = match load_credentials() {
            Ok(Some(c)) => c,
            Ok(None) => return Ok(PlanUsage::NoCredentials),
            Err(err) => {
                return Ok(PlanUsage::Failed {
                    message: err.to_string(),
                })
            }
        };

        let response = match fetch_usage(&creds.access_token).await {
            Ok(r) => r,
            // Parity with mac/Sources/CodeBurnMenubar/Data/ClaudeCredentialStore.swift
            // (`refreshAfter401`): Claude's refresh token is single-use and rotates, so
            // spending it here would invalidate the token Claude Code itself is holding and
            // break the user's `claude` login. Instead re-read Claude's own store for a
            // token it has already rotated; if there is nothing fresher yet, report a
            // transient failure and let the next refresh pick it up.
            Err(FetchError::Unauthorized) => {
                let rotated = load_credentials()
                    .ok()
                    .flatten()
                    .map(|c| c.access_token)
                    .filter(|t| *t != creds.access_token);
                let Some(token) = rotated else {
                    return Ok(PlanUsage::Failed {
                        message: "Claude is refreshing its session. This clears itself once Claude Code renews the token; run `claude login` if it persists.".into(),
                    });
                };
                match fetch_usage(&token).await {
                    Ok(r) => r,
                    Err(err) => {
                        return Ok(PlanUsage::Failed {
                            message: err.to_string(),
                        })
                    }
                }
            }
            Err(err) => {
                return Ok(PlanUsage::Failed {
                    message: err.to_string(),
                })
            }
        };

        let now = SystemTime::now();
        let mut windows = Vec::new();
        for (key, label) in WINDOW_KEYS {
            let Some(window) = response.window(key) else { continue };
            let Some(percent) = window.utilization else { continue };
            let resets_at = window.resets_at.clone().filter(|s| !s.is_empty());
            let previous_final = {
                let _guard = self.snapshot_lock.lock().await;
                if let Some(reset) = resets_at.as_deref() {
                    record_snapshot(key, percent, reset, now);
                    previous_window_final(key, reset)
                } else {
                    None
                }
            };
            windows.push(PlanWindow {
                key: key.to_string(),
                label: label.to_string(),
                percent: percent.clamp(0.0, 100.0),
                resets_at,
                previous_final,
            });
        }

        Ok(PlanUsage::Ok {
            tier: tier_display(creds.rate_limit_tier.as_deref()),
            raw_tier: creds.rate_limit_tier,
            windows,
            fetched_at: to_rfc3339(now),
        })
    }
}

// ---- credentials -----------------------------------------------------------------------

struct StoredCredentials {
    access_token: String,
    rate_limit_tier: Option<String>,
}

#[derive(Deserialize)]
struct CredentialsRoot {
    #[serde(rename = "claudeAiOauth")]
    claude_ai_oauth: Option<OAuthBlock>,
}

#[derive(Deserialize)]
struct OAuthBlock {
    #[serde(rename = "accessToken")]
    access_token: Option<String>,
    #[serde(rename = "rateLimitTier")]
    rate_limit_tier: Option<String>,
}

fn credentials_path() -> Option<PathBuf> {
    dirs::home_dir().map(|h| h.join(CREDENTIALS_RELATIVE_PATH))
}

/// Ok(None) when the file does not exist (user never logged in); Err for malformed data.
fn load_credentials() -> Result<Option<StoredCredentials>> {
    let Some(path) = credentials_path() else {
        return Ok(None);
    };
    let meta = match fs::symlink_metadata(&path) {
        Ok(m) => m,
        Err(_) => return Ok(None),
    };
    if meta.file_type().is_symlink() {
        bail!("credentials file is a symlink; refusing to read it");
    }
    if meta.len() > MAX_CREDENTIAL_BYTES {
        bail!("credentials file is unexpectedly large");
    }
    let bytes = fs::read(&path).with_context(|| "failed to read Claude credentials")?;
    let root: CredentialsRoot =
        serde_json::from_slice(&bytes).with_context(|| "Claude credentials are malformed")?;
    let Some(oauth) = root.claude_ai_oauth else {
        return Ok(None);
    };
    let token = oauth.access_token.unwrap_or_default().trim().to_string();
    if token.is_empty() {
        return Ok(None);
    }
    Ok(Some(StoredCredentials {
        access_token: token,
        rate_limit_tier: oauth.rate_limit_tier,
    }))
}

fn tier_display(raw: Option<&str>) -> String {
    let Some(raw) = raw.map(|r| r.to_lowercase()) else {
        return "Subscription".into();
    };
    if raw.contains("max_20x") || raw.contains("max20x") || raw.contains("max-20x") {
        return "Max 20x".into();
    }
    if raw.contains("max_5x") || raw.contains("max5x") || raw.contains("max-5x") {
        return "Max 5x".into();
    }
    if raw.contains("max") {
        return "Max 5x".into();
    }
    if raw.contains("pro") {
        return "Pro".into();
    }
    if raw.contains("team") {
        return "Team".into();
    }
    if raw.contains("enterprise") {
        return "Enterprise".into();
    }
    "Subscription".into()
}

// ---- HTTP ------------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
struct UsageResponse {
    five_hour: Option<Window>,
    seven_day: Option<Window>,
    seven_day_opus: Option<Window>,
    seven_day_sonnet: Option<Window>,
}

impl UsageResponse {
    fn window(&self, key: &str) -> Option<&Window> {
        match key {
            "five_hour" => self.five_hour.as_ref(),
            "seven_day" => self.seven_day.as_ref(),
            "seven_day_opus" => self.seven_day_opus.as_ref(),
            "seven_day_sonnet" => self.seven_day_sonnet.as_ref(),
            _ => None,
        }
    }
}

#[derive(Debug, Deserialize)]
struct Window {
    utilization: Option<f64>,
    resets_at: Option<String>,
}

#[derive(Debug, thiserror::Error)]
enum FetchError {
    #[error("Claude session is no longer authorized")]
    Unauthorized,
    #[error("Usage fetch failed ({0}){1}")]
    Http(u16, String),
    #[error("{0}")]
    Other(String),
}

fn client() -> Result<reqwest::Client, FetchError> {
    reqwest::Client::builder()
        .timeout(REQUEST_TIMEOUT)
        .https_only(true)
        .build()
        .map_err(|e| FetchError::Other(e.to_string()))
}

async fn fetch_usage(token: &str) -> Result<UsageResponse, FetchError> {
    let response = client()?
        .get(USAGE_URL)
        .bearer_auth(token)
        .header("Accept", "application/json")
        .header("anthropic-beta", BETA_HEADER)
        .header("User-Agent", USER_AGENT)
        .send()
        .await
        .map_err(|e| FetchError::Other(e.to_string()))?;
    let status = response.status();
    if status.as_u16() == 401 {
        return Err(FetchError::Unauthorized);
    }
    if !status.is_success() {
        let body = response.text().await.unwrap_or_default();
        let detail = if body.is_empty() { String::new() } else { format!(": {}", truncate(&body, 200)) };
        return Err(FetchError::Http(status.as_u16(), detail));
    }
    response
        .json::<UsageResponse>()
        .await
        .map_err(|e| FetchError::Other(format!("Decode failed: {e}")))
}

fn truncate(text: &str, max: usize) -> String {
    let mut out: String = text.chars().take(max).collect();
    if text.chars().count() > max {
        out.push_str("...");
    }
    out
}

// ---- snapshots -------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Snapshot {
    #[serde(rename = "windowKey")]
    window_key: String,
    percent: f64,
    #[serde(rename = "resetsAt")]
    resets_at: String,
    #[serde(rename = "capturedAt")]
    captured_at: String,
    #[serde(rename = "effectiveTokens")]
    effective_tokens: Option<f64>,
}

/// None when there is no cache dir and no home dir: writing the snapshot store into the
/// process's current directory would scatter a file wherever the tray happened to be
/// launched from, so we simply skip snapshots instead.
fn snapshots_path() -> Option<PathBuf> {
    std::env::var_os("CODEBURN_CACHE_DIR")
        .map(PathBuf::from)
        .or_else(|| dirs::home_dir().map(|h| h.join(".cache").join("codeburn")))
        .map(|dir| dir.join(SNAPSHOT_FILENAME))
}

fn load_snapshots() -> Vec<Snapshot> {
    snapshots_path()
        .and_then(|p| fs::read(p).ok())
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())
        .unwrap_or_default()
}

/// Mirrors `mac/Sources/CodeBurnMenubar/Security/SafeFile.swift`: write to a temp file with
/// owner-only permissions and rename over the target, and refuse a target that has been
/// replaced by a symlink pointing somewhere else.
fn save_snapshots(all: &[Snapshot]) {
    let Some(path) = snapshots_path() else { return };
    if let Ok(meta) = fs::symlink_metadata(&path) {
        if meta.file_type().is_symlink() {
            return;
        }
    }
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let Ok(bytes) = serde_json::to_vec_pretty(all) else { return };
    let tmp = path.with_extension("tmp");
    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let Ok(mut file) = options.open(&tmp) else { return };
    use std::io::Write;
    if file.write_all(&bytes).is_ok() && file.flush().is_ok() {
        drop(file);
        let _ = fs::rename(&tmp, &path);
    } else {
        drop(file);
        let _ = fs::remove_file(&tmp);
    }
}

fn record_snapshot(window_key: &str, percent: f64, resets_at: &str, now: SystemTime) {
    let mut all = load_snapshots();
    match all
        .iter_mut()
        .find(|s| s.window_key == window_key && s.resets_at == resets_at)
    {
        Some(existing) => {
            if percent > existing.percent {
                existing.percent = percent;
                existing.captured_at = to_rfc3339(now);
            }
        }
        None => all.push(Snapshot {
            window_key: window_key.to_string(),
            percent,
            resets_at: resets_at.to_string(),
            captured_at: to_rfc3339(now),
            effective_tokens: None,
        }),
    }
    let cutoff = now.checked_sub(SNAPSHOT_RETENTION).unwrap_or(UNIX_EPOCH);
    all.retain(|s| parse_rfc3339(&s.captured_at).map(|t| t >= cutoff).unwrap_or(true));
    save_snapshots(&all);
}

fn previous_window_final(window_key: &str, current_resets_at: &str) -> Option<f64> {
    let current = parse_rfc3339(current_resets_at)?;
    let all = load_snapshots();
    let priors: Vec<(SystemTime, f64)> = all
        .iter()
        .filter(|s| s.window_key == window_key)
        .filter_map(|s| parse_rfc3339(&s.resets_at).map(|t| (t, s.percent)))
        .filter(|(t, _)| *t < current)
        .collect();
    let latest = priors.iter().map(|(t, _)| *t).max()?;
    priors
        .iter()
        .filter(|(t, _)| *t == latest)
        .map(|(_, p)| *p)
        .fold(None, |acc: Option<f64>, p| Some(acc.map_or(p, |a| a.max(p))))
}

// ---- time helpers (RFC 3339 without pulling in chrono) --------------------------------

fn to_rfc3339(t: SystemTime) -> String {
    let secs = t.duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0) as i64;
    let (y, m, d, hh, mm, ss) = civil_from_unix(secs);
    format!("{y:04}-{m:02}-{d:02}T{hh:02}:{mm:02}:{ss:02}Z")
}

/// Accepts `2026-08-18T10:00:00Z`, with optional fractional seconds and `+hh:mm` offsets.
fn parse_rfc3339(text: &str) -> Option<SystemTime> {
    let bytes = text.as_bytes();
    if bytes.len() < 19 {
        return None;
    }
    let num = |a: usize, b: usize| text.get(a..b)?.parse::<i64>().ok();
    let (y, mo, d) = (num(0, 4)?, num(5, 7)?, num(8, 10)?);
    let (h, mi, s) = (num(11, 13)?, num(14, 16)?, num(17, 19)?);
    let mut rest = &text[19..];
    if rest.starts_with('.') {
        let end = rest[1..]
            .find(|c: char| !c.is_ascii_digit())
            .map(|i| i + 1)
            .unwrap_or(rest.len());
        rest = &rest[end..];
    }
    let offset_secs = match rest {
        "" | "Z" | "z" => 0,
        _ => {
            let sign = if rest.starts_with('-') { -1 } else { 1 };
            let oh = rest.get(1..3)?.parse::<i64>().ok()?;
            let om = rest.get(4..6)?.parse::<i64>().ok()?;
            sign * (oh * 3600 + om * 60)
        }
    };
    let unix = unix_from_civil(y, mo, d) + h * 3600 + mi * 60 + s - offset_secs;
    if unix < 0 {
        return None;
    }
    Some(UNIX_EPOCH + Duration::from_secs(unix as u64))
}

fn unix_from_civil(y: i64, m: i64, d: i64) -> i64 {
    // Howard Hinnant's days_from_civil.
    let y = if m <= 2 { y - 1 } else { y };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = (m + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    (era * 146_097 + doe - 719_468) * 86_400
}

fn civil_from_unix(secs: i64) -> (i64, i64, i64, i64, i64, i64) {
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    (y, m, d, rem / 3600, (rem % 3600) / 60, rem % 60)
}
