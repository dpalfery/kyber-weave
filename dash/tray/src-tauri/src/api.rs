//! The tray's whole conversation with the `kyberdash web` server, over loopback only.
//!
//! The tray is a display layer: it reads state and relays the operator's actions
//! through `/api/kyber/*` and does nothing else. This module is that one client.
//!
//! Requirement 6.10 allows the tray no network connection except to loopback,
//! and 7.7 fixes the poll rate at 15 s while the popover is open and 60 s while
//! it is closed. 7.4 is the one that shapes this module: when the server is
//! unreachable the last document is still shown, labelled with its age and the
//! error, and never presented as current.
//!
//! The tray holds no analysis logic (7.5), so the report is carried as the JSON
//! the engine produced. Re-modelling it here is how the two would drift.

use std::io::Read;
use std::time::{Duration, SystemTime};

use anyhow::{bail, Result};
use serde::Serialize;
use serde_json::Value;

/// Poll rates of Requirement 7.7. "At most every" — these are floors on the
/// gap, not deadlines.
pub const POLL_WHILE_OPEN: Duration = Duration::from_secs(15);
pub const POLL_WHILE_CLOSED: Duration = Duration::from_secs(60);

/// The report endpoint, relative to the server's base URL.
pub const REPORT_PATH: &str = "/api/kyber/report";
/// Read-only job state the server owns (refresh, clean, import, receiver).
pub const JOBS_PATH: &str = "/api/kyber/jobs";
/// Shared, server-owned settings: `GET` to read, `PUT` a patch to change.
pub const SETTINGS_PATH: &str = "/api/kyber/settings";
pub const REFRESH_PATH: &str = "/api/kyber/refresh";
pub const CLEAN_PATH: &str = "/api/kyber/clean";
pub const IMPORT_HISTORY_PATH: &str = "/api/kyber/import-history";

/// Every endpoint the tray may contact. A closed list, so a bug that builds a
/// URL from webview input cannot reach an arbitrary route on the server.
const API_PATHS: [&str; 6] = [
    REPORT_PATH,
    JOBS_PATH,
    SETTINGS_PATH,
    REFRESH_PATH,
    CLEAN_PATH,
    IMPORT_HISTORY_PATH,
];

/// How long a single request may take before it is a failure. Short, because a
/// hung request would otherwise stall the poll loop past its own interval.
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(10);

pub fn poll_interval(popover_open: bool) -> Duration {
    if popover_open {
        POLL_WHILE_OPEN
    } else {
        POLL_WHILE_CLOSED
    }
}

/// Builds the report URL, refusing anything that is not loopback.
///
/// This is the choke point for Requirement 6.10. The base URL arrives from
/// another process's stdout, so it is checked here rather than trusted: only
/// `http://127.0.0.1`, optionally with a port, and nothing that merely starts
/// with those characters.
pub fn report_url(base: &str) -> Result<String> {
    Ok(format!("{}{REPORT_PATH}", loopback_origin(base)?))
}

/// Parses the one origin the tray is allowed to contact or open.
///
/// This intentionally does not use a prefix test.  A URL such as
/// `http://127.0.0.1:1@evil.test` starts with the expected text while its
/// authority is actually `evil.test`; treating it as loopback would turn a
/// process-provided listening line into an SSRF/opening primitive.
pub fn loopback_origin(base: &str) -> Result<String> {
    let trimmed = base.trim();
    let Some(authority_and_tail) = trimmed.strip_prefix("http://") else {
        bail!("refusing a non-loopback server URL: {base}");
    };
    let authority_end = authority_and_tail
        .find(['/', '?', '#'])
        .unwrap_or(authority_and_tail.len());
    let authority = &authority_and_tail[..authority_end];
    let tail = &authority_and_tail[authority_end..];

    if authority.is_empty() || authority.contains('@') || authority.contains(['\\', '%']) {
        bail!("refusing a non-loopback server URL: {base}");
    }

    let (host, port) = match authority.split_once(':') {
        Some((host, port)) if !port.is_empty() && port.chars().all(|c| c.is_ascii_digit()) => {
            let parsed = port
                .parse::<u16>()
                .ok()
                .filter(|port| *port != 0)
                .ok_or_else(|| anyhow::anyhow!("invalid loopback port"))?;
            (host, Some(parsed))
        }
        Some(_) => bail!("refusing a non-loopback server URL: {base}"),
        None => (authority, None),
    };

    if host != "127.0.0.1" {
        bail!("refusing a non-loopback server URL: {base}");
    }

    // The server reports an origin.  A single or repeated trailing slash is
    // harmless, but a path/query/fragment is not part of the reported origin.
    if !tail.is_empty() && !tail.chars().all(|character| character == '/') {
        bail!("refusing a non-loopback server URL: {base}");
    }
    Ok(match port {
        Some(port) => format!("http://127.0.0.1:{port}"),
        None => "http://127.0.0.1".to_string(),
    })
}

/// Builds the report request for the tray's persisted scope controls.
///
/// The dashboard owns filtering and report derivation; the tray only carries
/// the selected scope into the existing endpoint.  `all` is the UI's sentinel
/// for an omitted harness filter.
pub fn report_url_for_scope(base: &str, harness: &str, window_days: u32) -> Result<String> {
    if window_days == 0 {
        bail!("report window must be positive");
    }
    let mut url = format!("{}{REPORT_PATH}?days={window_days}", loopback_origin(base)?);
    if harness != "all" {
        url.push_str("&harness=");
        url.push_str(&encode_query_component(harness));
    }
    Ok(url)
}

/// The URL of one fixed endpoint on the loopback server.
pub fn endpoint_url(base: &str, path: &str) -> Result<String> {
    if !API_PATHS.contains(&path) {
        bail!("refusing an unknown server endpoint: {path}");
    }
    Ok(format!("{}{path}", loopback_origin(base)?))
}

/// Re-validates a complete request URL immediately before opening a socket.
/// Only the report takes a query (the scope controls), so every other endpoint
/// must be the bare path; stripping the path first would otherwise reject the
/// very scope the tray just applied.
pub fn api_request_url(url: &str) -> Result<String> {
    let Some(start) = url.find("/api/kyber/") else {
        bail!("refusing a non-API URL: {url}");
    };
    let (origin, rest) = url.split_at(start);
    let (path, suffix) = match rest.find('?') {
        Some(index) => rest.split_at(index),
        None => (rest, ""),
    };
    if !API_PATHS.contains(&path) {
        bail!("refusing an unknown server endpoint: {url}");
    }
    if !suffix.is_empty() && (path != REPORT_PATH || suffix.contains(['#', '/'])) {
        bail!("refusing a malformed API URL: {url}");
    }
    Ok(format!("{}{path}{suffix}", loopback_origin(origin)?))
}

fn encode_query_component(value: &str) -> String {
    let mut encoded = String::new();
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(byte as char);
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    encoded
}

/// The last report, and what happened on the most recent attempt.
///
/// Mirrors the `report`, `reportFetchedAt` and `error` fields of the design's
/// `ViewState`: a failure sets the error and leaves the document and its
/// timestamp alone, which is exactly what 7.4 asks for.
#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportCache {
    pub report: Option<Value>,
    pub report_fetched_at: Option<String>,
    pub error: Option<String>,
}

impl ReportCache {
    /// A fresh document clears the error; nothing else does.
    pub fn record_success(&mut self, report: Value, at: SystemTime) {
        self.report = Some(report);
        self.report_fetched_at = Some(format_rfc3339(at));
        self.error = None;
    }

    /// Requirement 7.4: keep the document, keep its age, add the error.
    pub fn record_error(&mut self, error: impl Into<String>) {
        self.error = Some(error.into());
    }

    /// True when there is something to show that is not current. The popover
    /// uses this to decide whether the stale banner is warranted; having no
    /// report at all is the empty state instead, which reads differently.
    pub fn is_stale(&self) -> bool {
        self.error.is_some() && self.report.is_some()
    }
}

/// ISO-8601 / RFC 3339 in UTC, which is what the design's `ViewState` carries
/// and what the engine already emits for `generatedAt`.
fn format_rfc3339(at: SystemTime) -> String {
    let datetime: chrono::DateTime<chrono::Utc> = at.into();
    datetime.to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

/// The HTTP verbs the tray uses; nothing else is expressible.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Method {
    Get,
    Post,
    Put,
}

/// A server answer that was not a transport failure. The status is kept so the
/// caller can tell a busy job (409) from a broken one.
#[derive(Clone, Debug)]
pub struct ApiResponse {
    pub status: u16,
    /// `Null` when the body was empty or not JSON.
    pub body: Value,
}

impl ApiResponse {
    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }

    /// The route is not on this server at all, as opposed to being broken.
    /// This is the signal a capability probe reads: a server predating the
    /// jobs/settings/refresh/import routes answers 404 rather than failing.
    pub fn is_not_found(&self) -> bool {
        self.status == 404
    }

    /// The server's `{ "error": "..." }` text, when it sent one.
    pub fn error_message(&self) -> Option<&str> {
        self.body.get("error").and_then(Value::as_str)
    }
}

/// One loopback request. A trait so the runtime is testable without a server,
/// and so the loopback check is enforced in one place regardless of transport.
pub trait LoopbackClient {
    /// `GET` a document; any non-success status is an error.
    fn get(&mut self, url: &str) -> Result<Value>;

    /// A `GET` whose status is reported rather than turned into an error.
    ///
    /// The capability probe needs to tell "this server does not have that
    /// route" (404) from "that route is broken right now" (500) and from "the
    /// socket failed" (`Err`), because only the first means the server is too
    /// old to drive. The default keeps existing test doubles working: they
    /// answer documents, not statuses.
    fn get_with_status(&mut self, url: &str) -> Result<ApiResponse> {
        self.get(url).map(|body| ApiResponse { status: 200, body })
    }

    /// Sends `body` (JSON) and returns the answer whatever its status.
    fn send(&mut self, method: Method, url: &str, body: &Value) -> Result<ApiResponse>;
}

/// The real client: loopback, no redirects, no proxy, bounded.
///
/// Redirects are refused because following one is how a loopback-only client
/// ends up talking to somewhere else. The proxy is disabled for the same
/// reason — an inherited `http_proxy` would send loopback traffic off the box.
pub struct LoopbackFetcher {
    client: reqwest::blocking::Client,
}

/// Action answers are small status documents; anything larger is not one.
const MAX_ACTION_BODY_BYTES: u64 = 64 * 1024;

impl LoopbackFetcher {
    pub fn new() -> Result<Self> {
        let client = reqwest::blocking::Client::builder()
            .timeout(REQUEST_TIMEOUT)
            .redirect(reqwest::redirect::Policy::none())
            .no_proxy()
            .build()?;
        Ok(LoopbackFetcher { client })
    }
}

impl LoopbackClient for LoopbackFetcher {
    fn get(&mut self, url: &str) -> Result<Value> {
        // Checked again here, not just by the caller: this is the only place
        // that opens a socket, so it is the only place the guarantee holds.
        let checked = api_request_url(url)?;
        let response = self.client.get(&checked).send()?;
        if !response.status().is_success() {
            bail!("server returned {}", response.status());
        }
        Ok(response.json()?)
    }

    fn get_with_status(&mut self, url: &str) -> Result<ApiResponse> {
        let checked = api_request_url(url)?;
        let response = self.client.get(&checked).send()?;
        let status = response.status().as_u16();
        let body = response.json().unwrap_or(Value::Null);
        Ok(ApiResponse { status, body })
    }

    fn send(&mut self, method: Method, url: &str, body: &Value) -> Result<ApiResponse> {
        let checked = api_request_url(url)?;
        let request = match method {
            Method::Get => self.client.get(&checked),
            Method::Post => self.client.post(&checked).json(body),
            Method::Put => self.client.put(&checked).json(body),
        };
        let response = request.send()?;
        let status = response.status().as_u16();
        let mut text = String::new();
        response
            .take(MAX_ACTION_BODY_BYTES)
            .read_to_string(&mut text)?;
        Ok(ApiResponse {
            status,
            body: serde_json::from_str(&text).unwrap_or(Value::Null),
        })
    }
}

/// Fetches once and folds the outcome into the cache.
pub fn poll_once<F: LoopbackClient>(
    fetcher: &mut F,
    base_url: &str,
    cache: &mut ReportCache,
    now: SystemTime,
) {
    let url = match report_url(base_url) {
        Ok(url) => url,
        Err(err) => {
            cache.record_error(err.to_string());
            return;
        }
    };
    match fetcher.get(&url) {
        Ok(report) => cache.record_success(report, now),
        Err(err) => cache.record_error(err.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    struct FakeFetcher {
        responses: Vec<Result<Value>>,
        urls: Vec<String>,
    }

    impl FakeFetcher {
        fn ok(values: Vec<Value>) -> Self {
            FakeFetcher {
                responses: values.into_iter().map(Ok).collect(),
                urls: Vec::new(),
            }
        }
    }

    impl LoopbackClient for FakeFetcher {
        fn send(&mut self, _method: Method, _url: &str, _body: &Value) -> Result<ApiResponse> {
            bail!("the report tests never send actions")
        }

        fn get(&mut self, url: &str) -> Result<Value> {
            self.urls.push(url.to_string());
            if self.responses.is_empty() {
                bail!("connection refused");
            }
            self.responses.remove(0)
        }
    }

    fn at(secs: u64) -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(secs)
    }

    /// Requirement 7.7.
    #[test]
    fn polls_faster_while_the_popover_is_open() {
        assert_eq!(poll_interval(true), Duration::from_secs(15));
        assert_eq!(poll_interval(false), Duration::from_secs(60));
    }

    /// Requirement 6.10 is enforced on the URL, and the URL comes from another
    /// process's stdout, so the lookalikes matter as much as the obvious cases.
    #[test]
    fn refuses_any_host_but_loopback() {
        assert_eq!(
            report_url("http://127.0.0.1:4747").unwrap(),
            "http://127.0.0.1:4747/api/kyber/report"
        );
        assert_eq!(
            report_url("http://127.0.0.1").unwrap(),
            "http://127.0.0.1/api/kyber/report"
        );
        // A trailing slash is the same server.
        assert_eq!(
            report_url("http://127.0.0.1:4747/").unwrap(),
            "http://127.0.0.1:4747/api/kyber/report"
        );

        for refused in [
            "http://10.0.0.5:4747",
            "http://localhost:4747",
            "https://127.0.0.1:4747",
            "http://127.0.0.1.evil.test/",
            "http://127.0.0.1:4747@evil.test",
            "http://127.0.0.2:4747",
            "http://[::1]:4747",
            "http://127.0.0.1:1@evil.test",
            "file:///etc/passwd",
            "",
        ] {
            assert!(report_url(refused).is_err(), "{refused} should be refused");
        }
    }

    #[test]
    fn carries_the_tray_scope_into_the_existing_report_request() {
        assert_eq!(
            report_url_for_scope("http://127.0.0.1:4747/", "all", 30).unwrap(),
            "http://127.0.0.1:4747/api/kyber/report?days=30"
        );
        assert_eq!(
            report_url_for_scope("http://127.0.0.1:4747", "claude code/+", 14).unwrap(),
            "http://127.0.0.1:4747/api/kyber/report?days=14&harness=claude%20code%2F%2B"
        );
        assert_eq!(
            api_request_url("http://127.0.0.1:4747/api/kyber/report?days=14&harness=codex")
                .unwrap(),
            "http://127.0.0.1:4747/api/kyber/report?days=14&harness=codex"
        );
    }

    /// The client may reach the fixed endpoints and nothing else, and only the
    /// report carries a query.
    #[test]
    fn the_client_reaches_only_the_fixed_endpoints() {
        for path in API_PATHS {
            let url = endpoint_url("http://127.0.0.1:4747", path).unwrap();
            assert_eq!(api_request_url(&url).unwrap(), url);
        }

        assert!(endpoint_url("http://127.0.0.1:4747", "/api/kyber/sessions").is_err());
        for refused in [
            "http://127.0.0.1:4747/api/kyber/sessions",
            "http://127.0.0.1:4747/api/kyber/clean?all=1",
            "http://127.0.0.1:4747/api/kyber/jobs/../clean",
            "http://127.0.0.1:4747/other",
            "http://evil.test/api/kyber/clean",
            "http://127.0.0.1:1@evil.test/api/kyber/clean",
        ] {
            assert!(
                api_request_url(refused).is_err(),
                "{refused} should be refused"
            );
        }
    }

    #[test]
    fn loopback_origin_rejects_userinfo_and_non_origin_tails() {
        for refused in [
            "http://127.0.0.1:1@evil.test",
            "http://127.0.0.1.evil.test:4747",
            "http://127.0.0.1:4747/api/kyber",
            "http://127.0.0.1:4747?next=evil.test",
            "http://127.0.0.1:4747#fragment",
            "http://127.0.0.1:0",
        ] {
            assert!(
                loopback_origin(refused).is_err(),
                "{refused} should be refused"
            );
        }
    }

    #[test]
    fn a_good_fetch_records_the_document_and_its_time() {
        let mut fetcher = FakeFetcher::ok(vec![json!({"schemaVersion": 1})]);
        let mut cache = ReportCache::default();

        poll_once(
            &mut fetcher,
            "http://127.0.0.1:4747",
            &mut cache,
            at(1_700_000_000),
        );

        assert_eq!(cache.report, Some(json!({"schemaVersion": 1})));
        assert_eq!(
            cache.report_fetched_at.as_deref(),
            Some("2023-11-14T22:13:20.000Z")
        );
        assert_eq!(cache.error, None);
        assert!(!cache.is_stale());
        assert_eq!(fetcher.urls, vec!["http://127.0.0.1:4747/api/kyber/report"]);
    }

    /// Requirement 7.4: the last document survives the failure, with its
    /// original age, and the error rides alongside it.
    #[test]
    fn a_failure_keeps_the_last_document_and_its_age() {
        let mut fetcher = FakeFetcher::ok(vec![json!({"schemaVersion": 1})]);
        let mut cache = ReportCache::default();
        poll_once(
            &mut fetcher,
            "http://127.0.0.1:4747",
            &mut cache,
            at(1_700_000_000),
        );
        let first_fetch = cache.report_fetched_at.clone();

        // The fake is out of responses, so this one fails.
        poll_once(
            &mut fetcher,
            "http://127.0.0.1:4747",
            &mut cache,
            at(1_700_000_300),
        );

        assert_eq!(cache.report, Some(json!({"schemaVersion": 1})));
        assert_eq!(
            cache.report_fetched_at, first_fetch,
            "the age shown must be the age of the document, not of the attempt"
        );
        assert_eq!(cache.error.as_deref(), Some("connection refused"));
        assert!(cache.is_stale());
    }

    /// The capability probe's whole vocabulary: a server that does not serve
    /// the jobs route cannot serve the tray's actions either.
    #[test]
    fn a_missing_route_is_told_apart_from_a_broken_one() {
        let missing = ApiResponse {
            status: 404,
            body: Value::Null,
        };
        let broken = ApiResponse {
            status: 500,
            body: Value::Null,
        };
        assert!(missing.is_not_found());
        assert!(!broken.is_not_found());
        assert!(!missing.is_success());
    }

    /// A test double that answers documents keeps the default probe behaviour:
    /// a document it can produce is a route the server has.
    #[test]
    fn the_default_probe_reads_a_route_the_double_can_answer() {
        let mut fetcher = FakeFetcher::ok(vec![json!({"refresh": {"state": "idle"}})]);
        let response = fetcher
            .get_with_status("http://127.0.0.1:4747/api/kyber/jobs")
            .expect("the fake can answer the jobs route");

        assert!(!response.is_not_found());
        assert!(response.is_success());
    }

    #[test]
    fn a_later_success_clears_the_error() {
        let mut cache = ReportCache::default();
        cache.record_error("connection refused");
        cache.record_success(json!({"schemaVersion": 1}), at(1_700_000_000));

        assert_eq!(cache.error, None);
        assert!(!cache.is_stale());
    }

    /// Nothing fetched yet is the empty state, not the stale state: there is no
    /// old document being passed off as current.
    #[test]
    fn an_error_with_no_document_is_not_stale() {
        let mut cache = ReportCache::default();
        cache.record_error("connection refused");

        assert!(cache.report.is_none());
        assert!(!cache.is_stale());
    }

    /// A bad base URL fails as an error on the cache rather than a panic, and
    /// no request is attempted.
    #[test]
    fn a_refused_url_never_reaches_the_fetcher() {
        let mut fetcher = FakeFetcher::ok(vec![json!({"schemaVersion": 1})]);
        let mut cache = ReportCache::default();

        poll_once(
            &mut fetcher,
            "http://evil.test",
            &mut cache,
            at(1_700_000_000),
        );

        assert!(fetcher.urls.is_empty(), "no socket may be opened");
        assert!(cache.error.as_deref().unwrap().contains("non-loopback"));
        assert!(cache.report.is_none());
    }

    /// The UI reads these as the design's ViewState fields.
    #[test]
    fn serializes_with_the_design_s_names() {
        let mut cache = ReportCache::default();
        cache.record_success(json!({"schemaVersion": 1}), at(1_700_000_000));
        let json = serde_json::to_value(&cache).unwrap();

        assert_eq!(json["report"]["schemaVersion"], 1);
        assert_eq!(json["reportFetchedAt"], "2023-11-14T22:13:20.000Z");
        assert!(json["error"].is_null());
    }
}
