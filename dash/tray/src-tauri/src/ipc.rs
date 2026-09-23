//! The only surface the webview can reach, and the state it reads.
//!
//! The popover's CSP is `default-src 'self'; connect-src ipc: http://ipc.localhost`,
//! so the UI opens no sockets of its own (6.10) and everything it knows arrives
//! through here. [`ViewState`] is that everything, assembled from the parts each
//! module already owns rather than re-derived — Requirement 7.5 keeps analysis
//! out of the tray.
//!
//! `open_view` is the one command that takes a path from the webview and turns
//! it into a URL, so it is the one that validates. The route table is
//! `src/server/view-paths.json`, embedded with `include_str!` and matched the
//! same way `src/server/view-paths.ts` matches it: the CLI's `--view`, the web
//! router and this command have to accept exactly the same paths, or a report
//! that tells the user to run `kyberdash web --view finding/<id>` names a view
//! the tray then refuses.

use anyhow::{bail, Result};
use serde::Serialize;

use crate::api::{self, ReportCache};
use crate::cli::SetupState;
use crate::receiver::ReceiverStatus;
use crate::scheduler::RefreshStatus;
use crate::settings::TraySettings;
use crate::supervisor::ServerPhase;

/// The route table, shared with the CLI and the web router.
const VIEW_PATHS_JSON: &str = include_str!("../../../src/server/view-paths.json");

/// The design's `ViewState.phase`. `Setup` is the arm the supervisor has no
/// opinion about: it means the CLI itself is missing or too old (6.7), which is
/// decided before a server is ever started.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Phase {
    Setup,
    Starting,
    Ready,
    Stale,
}

/// Everything the popover renders.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ViewState {
    pub phase: Phase,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub setup: Option<SetupState>,
    pub report: Option<serde_json::Value>,
    pub report_fetched_at: Option<String>,
    pub error: Option<String>,
    pub refresh: RefreshStatus,
    pub receiver: ReceiverStatus,
    pub settings: TraySettings,
}

/// Assembles the state the popover reads.
///
/// The phase is decided here rather than by any one module because it is a
/// statement about the whole tray: setup outranks everything (there is nothing
/// to show), then the supervisor's own phase, and a cache holding an error over
/// an old document is stale however healthy the server looks.
pub fn view_state(
    setup: Option<SetupState>,
    server: ServerPhase,
    cache: &ReportCache,
    refresh: &RefreshStatus,
    receiver: ReceiverStatus,
    settings: &TraySettings,
) -> ViewState {
    let phase = match (&setup, server) {
        (Some(_), _) => Phase::Setup,
        (None, ServerPhase::Stale) => Phase::Stale,
        (None, _) if cache.is_stale() => Phase::Stale,
        (None, ServerPhase::Ready) => Phase::Ready,
        (None, ServerPhase::Starting) => Phase::Starting,
    };

    ViewState {
        phase,
        setup,
        report: cache.report.clone(),
        report_fetched_at: cache.report_fetched_at.clone(),
        error: cache.error.clone(),
        refresh: refresh.clone(),
        receiver,
        settings: settings.clone(),
    }
}

/// The patterns from `view-paths.json`.
pub fn view_path_patterns() -> Vec<String> {
    serde_json::from_str::<Vec<String>>(VIEW_PATHS_JSON)
        .expect("view-paths.json is a list of strings; it is compiled in")
}

fn split_query(value: &str) -> (String, String) {
    let trimmed = value.trim();
    match trimmed.find('?') {
        Some(index) => (
            trimmed[..index].to_string(),
            trimmed[index + 1..].to_string(),
        ),
        None => (trimmed.to_string(), String::new()),
    }
}

fn normalize_path(path: &str) -> &str {
    path.trim_matches('/')
}

/// Segment-wise match, where a `:name` segment stands for exactly one segment.
fn path_matches(view: &str, pattern: &str) -> bool {
    let view = normalize_path(view);
    let pattern = normalize_path(pattern);
    if pattern.is_empty() {
        return view.is_empty();
    }
    if view.is_empty() {
        return false;
    }

    let view_segments: Vec<&str> = view.split('/').collect();
    let pattern_segments: Vec<&str> = pattern.split('/').collect();
    if view_segments.len() != pattern_segments.len() {
        return false;
    }
    view_segments
        .iter()
        .zip(pattern_segments.iter())
        .all(|(actual, expected)| {
            if let Some(name) = expected.strip_prefix(':') {
                // A placeholder stands for one non-empty segment; the name
                // itself is not matched against.
                let _ = name;
                !actual.is_empty()
            } else {
                actual == expected
            }
        })
}

/// Every key the pattern names must be present and non-empty.
fn query_matches(view_query: &str, pattern_query: &str) -> bool {
    if pattern_query.is_empty() {
        return true;
    }
    for part in pattern_query.split('&').filter(|p| !p.is_empty()) {
        let key = part.split('=').next().unwrap_or(part);
        let present = view_query.split('&').any(|actual| {
            let mut halves = actual.splitn(2, '=');
            halves.next() == Some(key) && !halves.next().unwrap_or("").is_empty()
        });
        if !present {
            return false;
        }
    }
    true
}

/// True when the dashboard has a route for this view.
pub fn matches_view_path(view: &str) -> bool {
    let (asked_path, asked_query) = split_query(view);
    view_path_patterns().iter().any(|pattern| {
        let (expected_path, expected_query) = split_query(pattern);
        path_matches(&asked_path, &expected_path) && query_matches(&asked_query, &expected_query)
    })
}

/// Turns a view into the URL to open, refusing anything off the route table.
///
/// The check is not cosmetic: this value comes from the webview, and the result
/// is handed to the system opener. An unchecked path is how `../`, a `file:`
/// URL, or a whole different origin would be opened as the user.
pub fn open_view_url(server_url: &str, view: &str) -> Result<String> {
    let base = api::loopback_origin(server_url)
        .map_err(|_| anyhow::anyhow!("refusing to open a non-loopback URL: {server_url}"))?;
    // A scheme, an authority, or a traversal in the view is never a route.
    if view.contains("://") || view.starts_with("//") || view.split('/').any(|s| s == "..") {
        bail!("refusing a view that is not a dashboard route: {view}");
    }
    if !matches_view_path(view) {
        bail!("refusing a view that is not a dashboard route: {view}");
    }
    let path = view.trim_start_matches('/');
    if path.is_empty() {
        return Ok(base.to_string());
    }
    Ok(format!("{base}/{path}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cli::SetupReason;
    use crate::scheduler::RefreshState;
    use serde_json::json;
    use std::time::{Duration, SystemTime};

    fn cache_with_report() -> ReportCache {
        let mut cache = ReportCache::default();
        cache.record_success(
            json!({"schemaVersion": 1}),
            SystemTime::UNIX_EPOCH + Duration::from_secs(1_700_000_000),
        );
        cache
    }

    fn state(setup: Option<SetupState>, server: ServerPhase, cache: &ReportCache) -> ViewState {
        view_state(
            setup,
            server,
            cache,
            &RefreshStatus::default(),
            ReceiverStatus::Unknown,
            &TraySettings::default(),
        )
    }

    /// The design's ViewState, by its own field names.
    #[test]
    fn carries_the_designs_view_state() {
        let json =
            serde_json::to_value(state(None, ServerPhase::Ready, &cache_with_report())).unwrap();

        assert_eq!(json["phase"], "ready");
        assert_eq!(json["report"]["schemaVersion"], 1);
        assert_eq!(json["reportFetchedAt"], "2023-11-14T22:13:20.000Z");
        assert!(json["error"].is_null());
        assert_eq!(json["refresh"]["state"], "idle");
        assert_eq!(json["receiver"], "unknown");
        assert_eq!(json["settings"]["harness"], "all");
        assert!(json.get("setup").is_none(), "absent unless in setup");
    }

    /// Requirement 6.7: setup outranks every other phase, because there is
    /// nothing to present as current.
    #[test]
    fn setup_outranks_the_server_phase() {
        let setup = SetupState::new(SetupReason::NotFound, vec!["PATH".to_string()]);
        for server in [
            ServerPhase::Starting,
            ServerPhase::Ready,
            ServerPhase::Stale,
        ] {
            let view = state(Some(setup.clone()), server, &cache_with_report());
            assert_eq!(view.phase, Phase::Setup, "with server {server:?}");
        }

        let json = serde_json::to_value(state(
            Some(setup),
            ServerPhase::Ready,
            &ReportCache::default(),
        ))
        .unwrap();
        assert_eq!(json["phase"], "setup");
        assert_eq!(json["setup"]["reason"], "not-found");
        assert_eq!(json["setup"]["probed"][0], "PATH");
    }

    /// A cache holding an error over an old document is stale however healthy
    /// the server looks — 7.4 forbids showing it as current.
    #[test]
    fn an_erroring_cache_is_stale_even_when_the_server_is_ready() {
        let mut cache = cache_with_report();
        cache.record_error("connection refused");

        assert_eq!(state(None, ServerPhase::Ready, &cache).phase, Phase::Stale);
    }

    #[test]
    fn phases_follow_the_supervisor_when_nothing_else_intervenes() {
        let cache = cache_with_report();
        assert_eq!(
            state(None, ServerPhase::Starting, &cache).phase,
            Phase::Starting
        );
        assert_eq!(state(None, ServerPhase::Ready, &cache).phase, Phase::Ready);
        assert_eq!(state(None, ServerPhase::Stale, &cache).phase, Phase::Stale);
    }

    #[test]
    fn a_failed_refresh_is_carried_verbatim() {
        let refresh = RefreshStatus {
            state: RefreshState::Failed,
            last_success_at: Some("2023-11-14T22:13:20.000Z".to_string()),
            last_failure: Some("canon.db is locked at 2023-11-14T22:23:20.000Z".to_string()),
        };
        let json = serde_json::to_value(view_state(
            None,
            ServerPhase::Ready,
            &cache_with_report(),
            &refresh,
            ReceiverStatus::Hosted,
            &TraySettings::default(),
        ))
        .unwrap();

        assert_eq!(json["refresh"]["state"], "failed");
        assert_eq!(json["refresh"]["lastSuccessAt"], "2023-11-14T22:13:20.000Z");
        assert!(json["refresh"]["lastFailure"]
            .as_str()
            .unwrap()
            .contains("canon.db is locked"));
        assert_eq!(json["receiver"], "hosted");
    }

    /// The tray matches the same table the CLI and the web router match.
    #[test]
    fn the_route_table_is_the_shared_one() {
        let patterns = view_path_patterns();

        assert!(patterns.contains(&"/".to_string()));
        assert!(patterns.contains(&"/finding/:findingId".to_string()));
        assert!(patterns.contains(&"/session/:sessionId/turn/:turnIndex".to_string()));
    }

    #[test]
    fn accepts_every_documented_view_form() {
        for view in [
            "/",
            "",
            "harness/claude-code",
            "/run/run-123",
            "session/abc",
            "session/abc/turn/7",
            "finding/f-1",
            "quarantine",
            "problems",
            "compare?a=run-1&b=run-2",
        ] {
            assert!(matches_view_path(view), "{view} should be a route");
        }
    }

    #[test]
    fn refuses_paths_that_are_not_routes() {
        for view in [
            "usage",
            "session",
            "session/abc/turn",
            "session/abc/turn/7/extra",
            "finding",
            "harness/a/b",
        ] {
            assert!(!matches_view_path(view), "{view} should not be a route");
        }
    }

    /// The compare route names both keys, so one of them is not enough.
    #[test]
    fn compare_needs_both_of_its_query_keys() {
        assert!(matches_view_path("compare?a=run-1&b=run-2"));
        assert!(!matches_view_path("compare?a=run-1"));
        assert!(!matches_view_path("compare?a=run-1&b="));
        assert!(!matches_view_path("compare"));
    }

    #[test]
    fn builds_the_url_for_a_valid_view() {
        assert_eq!(
            open_view_url("http://127.0.0.1:4747", "finding/f-1").unwrap(),
            "http://127.0.0.1:4747/finding/f-1"
        );
        assert_eq!(
            open_view_url("http://127.0.0.1:4747/", "/finding/f-1").unwrap(),
            "http://127.0.0.1:4747/finding/f-1"
        );
        assert_eq!(
            open_view_url("http://127.0.0.1:4747", "/").unwrap(),
            "http://127.0.0.1:4747"
        );
    }

    /// This value comes from the webview and the result is handed to the system
    /// opener, so the refusals matter more than the acceptances.
    #[test]
    fn refuses_anything_that_is_not_a_dashboard_route() {
        for view in [
            "https://evil.test/",
            "//evil.test/",
            "file:///etc/passwd",
            "../../etc/passwd",
            "finding/../../../etc/passwd",
            "javascript:alert(1)",
            "usage",
        ] {
            assert!(
                open_view_url("http://127.0.0.1:4747", view).is_err(),
                "{view} must be refused"
            );
        }
    }

    /// Requirement 6.10 applies to what is opened as much as to what is fetched.
    #[test]
    fn refuses_a_non_loopback_server_url() {
        assert!(open_view_url("http://evil.test", "finding/f-1").is_err());
        assert!(open_view_url("https://127.0.0.1:4747", "finding/f-1").is_err());
        assert!(open_view_url("http://127.0.0.1:1@evil.test", "finding/f-1").is_err());
    }
}
