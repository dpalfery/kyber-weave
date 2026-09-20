//! Reads the report from the supervised server, over loopback only.
//!
//! Requirement 6.10 allows the tray no network connection except to loopback,
//! and 7.7 fixes the poll rate at 15 s while the popover is open and 60 s while
//! it is closed. 7.4 is the one that shapes this module: when the server is
//! unreachable the last document is still shown, labelled with its age and the
//! error, and never presented as current.
//!
//! The tray holds no analysis logic (7.5), so the report is carried as the JSON
//! the engine produced. Re-modelling it here is how the two would drift.

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
    let trimmed = base.trim_end_matches('/');
    let Some(rest) = trimmed.strip_prefix("http://127.0.0.1") else {
        bail!("refusing a non-loopback server URL: {base}");
    };
    // Either nothing follows the host, or a port does. A path, or a longer
    // hostname such as `127.0.0.1.evil.test`, is not this server.
    if !rest.is_empty() {
        let Some(port) = rest.strip_prefix(':') else {
            bail!("refusing a non-loopback server URL: {base}");
        };
        if port.is_empty() || !port.chars().all(|c| c.is_ascii_digit()) {
            bail!("refusing a non-loopback server URL: {base}");
        }
    }
    Ok(format!("{trimmed}{REPORT_PATH}"))
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

/// One report fetch. A trait so the poll loop is testable without a server, and
/// so the loopback check is enforced in one place regardless of transport.
pub trait ReportFetcher {
    fn fetch(&mut self, url: &str) -> Result<Value>;
}

/// The real client: loopback, no redirects, no proxy, bounded.
///
/// Redirects are refused because following one is how a loopback-only client
/// ends up talking to somewhere else. The proxy is disabled for the same
/// reason — an inherited `http_proxy` would send loopback traffic off the box.
pub struct LoopbackFetcher {
    client: reqwest::blocking::Client,
}

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

impl ReportFetcher for LoopbackFetcher {
    fn fetch(&mut self, url: &str) -> Result<Value> {
        // Checked again here, not just by the caller: this is the only place
        // that opens a socket, so it is the only place the guarantee holds.
        let checked = report_url(url.trim_end_matches(REPORT_PATH))?;
        let response = self.client.get(&checked).send()?;
        if !response.status().is_success() {
            bail!("server returned {}", response.status());
        }
        Ok(response.json()?)
    }
}

/// Fetches once and folds the outcome into the cache.
pub fn poll_once<F: ReportFetcher>(
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
    match fetcher.fetch(&url) {
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

    impl ReportFetcher for FakeFetcher {
        fn fetch(&mut self, url: &str) -> Result<Value> {
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
            "file:///etc/passwd",
            "",
        ] {
            assert!(report_url(refused).is_err(), "{refused} should be refused");
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
