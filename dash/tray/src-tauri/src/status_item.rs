//! What the menu-bar / notification-area item shows.
//!
//! Requirement 9.1 shows the scoped latest session's latest-turn pressure as a
//! whole percentage — title text beside the icon on macOS, a badge on the icon
//! on Windows. 9.2 sets the attention and critical icon states at configurable
//! thresholds, 9.3 shows a neutral icon and no number when there is nothing
//! measurable to show, 9.4 makes stale distinct from every pressure state, 9.5
//! fixes what the tooltip names, and 9.6 forbids cost.
//!
//! [`status_item`] is a pure function of the report, the settings and the
//! phase. Keeping it pure is what lets every rule above be asserted without a
//! tray, a window server, or a running Tauri app.

use std::time::{Duration, SystemTime};

use serde::Serialize;
use serde_json::Value;

/// Icon states of Requirements 9.2 and 9.4.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum IconState {
    #[default]
    Neutral,
    Attention,
    Critical,
    /// Distinct from every pressure state (9.4): what is on screen is not
    /// current, so a pressure colour would be a claim the tray cannot make.
    Stale,
}

/// Live-collector saber state, derived only from the shared coverage API's
/// `receiver` document (`GET /api/kyber/coverage`). Green (`Live`) only when
/// the engine reports healthy, listening, unpaused and error-free; every
/// other shape — including a missing `receiver` key (servers predating the
/// probe) or a stale check — is `Down` (red). Unknown is red, never amber:
/// the saber must not read as "maybe fine".
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum CollectorState {
    Live,
    #[default]
    Down,
}

/// How long a successful probe stays trustworthy without a fresh check.
/// Matches the web Status panel's `RECEIVER_STALE_AFTER_MS`.
pub const COLLECTOR_STALE_AFTER_SECS: u64 = 120;

/// Reads the authoritative saber state from a coverage document.
///
/// `healthy` is the engine's word; the rest guards against a healthy flag
/// riding alongside a paused listener, recent errors, or a stale check.
/// Health probes are excluded from the server error count by the engine —
/// this function never recomputes that exclusion, it only renders it.
pub fn collector_health(coverage: Option<&Value>, now: SystemTime) -> CollectorState {
    let Some(receiver) = coverage.and_then(|c| c.get("receiver")) else {
        return CollectorState::Down;
    };
    if receiver.is_null() {
        return CollectorState::Down;
    }
    if receiver.get("healthy").and_then(Value::as_bool) != Some(true) {
        return CollectorState::Down;
    }
    if receiver.get("paused").and_then(Value::as_bool) == Some(true) {
        return CollectorState::Down;
    }
    if receiver.get("listening").and_then(Value::as_bool) != Some(true) {
        return CollectorState::Down;
    }
    let errors_total = receiver
        .get("errors5m")
        .and_then(|e| e.get("total"))
        .and_then(Value::as_u64);
    if errors_total != Some(0) {
        return CollectorState::Down;
    }
    let Some(checked_at) = receiver.get("checkedAt").and_then(Value::as_str) else {
        return CollectorState::Down;
    };
    let Ok(parsed) = chrono::DateTime::parse_from_rfc3339(checked_at) else {
        return CollectorState::Down;
    };
    let checked: SystemTime = parsed.with_timezone(&chrono::Utc).into();
    let Ok(age) = now.duration_since(checked) else {
        // Checked-at is in the future (clock skew): not current.
        return CollectorState::Down;
    };
    if age.as_secs() > COLLECTOR_STALE_AFTER_SECS {
        return CollectorState::Down;
    }
    CollectorState::Live
}

/// Pressure thresholds, as fractions of the context window (9.2).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Thresholds {
    pub attention: f64,
    pub critical: f64,
}

impl Default for Thresholds {
    fn default() -> Self {
        Thresholds {
            attention: 0.70,
            critical: 0.90,
        }
    }
}

/// Everything the platform layer needs to draw the item.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusItem {
    pub icon: IconState,
    /// The whole-percentage label, or `None` when there is no number to show
    /// (9.3). macOS draws it as title text; Windows draws it as a badge.
    pub title: Option<String>,
    pub tooltip: String,
    /// Green/red collector saber from the shared coverage API. Serialized so
    /// tests and the popover can assert it; the native icon renderer reads it
    /// to pick the coloured asset (template treatment off).
    pub collector: CollectorState,
}

/// Builds the status item from the report, the settings and the phase.
///
/// `stale` folds together the two causes Requirement 9.4 names — an unreachable
/// server and a failed refresh — because the item renders them identically and
/// the distinction belongs in the footer, which has room to explain it.
///
/// `coverage` is the shared `GET /api/kyber/coverage` document (only its
/// `receiver` key is read). A `None` coverage, like a stale phase, forces the
/// collector saber red: unavailable health is down, never "maybe".
pub fn status_item(
    report: Option<&Value>,
    thresholds: &Thresholds,
    stale: bool,
    now: SystemTime,
) -> StatusItem {
    status_item_with_coverage(report, None, thresholds, stale, now)
}

/// Same as [`status_item`], plus the collector saber from coverage.
pub fn status_item_with_coverage(
    report: Option<&Value>,
    coverage: Option<&Value>,
    thresholds: &Thresholds,
    stale: bool,
    now: SystemTime,
) -> StatusItem {
    let session = report
        .and_then(|r| r.get("latestSession"))
        .filter(|s| !s.is_null());
    let pressure = session
        .and_then(|s| s.get("latestTurn"))
        .and_then(|t| t.get("pressure"))
        .and_then(measured_value);

    // 9.4 outranks pressure: a number drawn over stale data would read as
    // current. The tooltip still carries the figure, labelled by its age.
    let icon = if stale {
        IconState::Stale
    } else {
        match pressure {
            Some(fraction) => icon_for(fraction, thresholds),
            // 9.3: unmeasurable, or no session in scope.
            None => IconState::Neutral,
        }
    };

    let title = match (stale, pressure) {
        (false, Some(fraction)) => Some(format_percent(fraction)),
        _ => None,
    };

    // The collector saber is red when health is unavailable or stale too:
    // a stale phase means the coverage underneath is not current either.
    let collector = if stale {
        CollectorState::Down
    } else {
        collector_health(coverage, now)
    };

    StatusItem {
        icon,
        title,
        tooltip: tooltip_for(session, pressure, stale, report, now, collector),
        collector,
    }
}

/// 9.2's bands. Both are "reaches", so the threshold itself is inside the band.
fn icon_for(fraction: f64, thresholds: &Thresholds) -> IconState {
    if fraction >= thresholds.critical {
        IconState::Critical
    } else if fraction >= thresholds.attention {
        IconState::Attention
    } else {
        IconState::Neutral
    }
}

/// 9.1: a whole percentage. Rounded, not truncated, so 0.699 reads as 70% the
/// same way the attention band does.
fn format_percent(fraction: f64) -> String {
    format!("{}%", (fraction * 100.0).round() as i64)
}

/// Reads a `Measured<T>`: a value, or `null` with a reason. An absent figure is
/// `None` here and `—` on screen; it is never `0`.
fn measured_value(measured: &Value) -> Option<f64> {
    measured.get("value").and_then(|v| v.as_f64())
}

/// 9.5: the harness, the project, the pressure and the age of the data.
///
/// 9.6 is a property of what is *not* here: no cost figure reaches this string.
///
/// The collector saber rides at the end in words (`collector live` / down),
/// so the pressure detail is preserved and the health is named, not colour
/// alone.
fn tooltip_for(
    session: Option<&Value>,
    pressure: Option<f64>,
    stale: bool,
    report: Option<&Value>,
    now: SystemTime,
    collector: CollectorState,
) -> String {
    let collector_text = match collector {
        CollectorState::Live => "collector live",
        CollectorState::Down => "collector down",
    };
    let Some(session) = session else {
        return if stale {
            format!("KyberDash — data may be out of date · {collector_text}")
        } else {
            format!("KyberDash — no session in scope · {collector_text}")
        };
    };

    let harness = session
        .get("harness")
        .and_then(|h| h.as_str())
        .unwrap_or("unknown harness");
    let project = session
        .get("project")
        .and_then(|p| p.as_str())
        .unwrap_or("no project");
    let pressure_text = match pressure {
        Some(fraction) => format!("{} context", format_percent(fraction)),
        // The reason is the report's own words, so the tooltip explains the
        // missing number rather than implying there is none to have.
        None => session
            .get("latestTurn")
            .and_then(|t| t.get("pressure"))
            .and_then(|p| p.get("reason"))
            .and_then(|r| r.as_str())
            .map(|reason| format!("context — ({reason})"))
            .unwrap_or_else(|| "context —".to_string()),
    };

    let age = report
        .and_then(|r| r.get("generatedAt"))
        .and_then(|g| g.as_str())
        .and_then(|at| age_of(at, now))
        .map(format_age)
        .unwrap_or_else(|| "age unknown".to_string());

    let suffix = if stale { ", not current" } else { "" };
    format!("{harness} · {project} · {pressure_text} · {age}{suffix} · {collector_text}")
}

fn age_of(generated_at: &str, now: SystemTime) -> Option<Duration> {
    let parsed = chrono::DateTime::parse_from_rfc3339(generated_at).ok()?;
    let generated: SystemTime = parsed.with_timezone(&chrono::Utc).into();
    now.duration_since(generated).ok()
}

/// Coarse on purpose: the item is glanced at, and a second-accurate age would
/// change on every poll without telling the reader anything.
fn format_age(age: Duration) -> String {
    let seconds = age.as_secs();
    if seconds < 60 {
        "just now".to_string()
    } else if seconds < 3600 {
        format!("{}m ago", seconds / 60)
    } else if seconds < 86_400 {
        format!("{}h ago", seconds / 3600)
    } else {
        format!("{}d ago", seconds / 86_400)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn at(secs: u64) -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(secs)
    }

    const GENERATED: &str = "2023-11-14T22:13:20Z";
    /// The instant `GENERATED` names, so a test can choose the age it wants.
    const GENERATED_EPOCH: u64 = 1_700_000_000;

    fn report_with_pressure(pressure: Value) -> Value {
        json!({
            "schemaVersion": 1,
            "generatedAt": GENERATED,
            "latestSession": {
                "sessionId": "abc123",
                "harness": "claude-code",
                "project": "kyber-weave",
                "latestTurn": { "index": 7, "pressure": pressure },
            },
            "cost": [{ "basis": "anthropic", "amountUsd": { "value": 12.34 } }],
        })
    }

    fn measured(value: f64) -> Value {
        json!({ "value": value })
    }

    fn unmeasurable(reason: &str) -> Value {
        json!({ "value": null, "reason": reason })
    }

    /// Requirement 9.1.
    #[test]
    fn shows_pressure_as_a_whole_percentage() {
        let report = report_with_pressure(measured(0.42));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );

        assert_eq!(item.title.as_deref(), Some("42%"));
    }

    #[test]
    fn rounds_rather_than_truncating() {
        for (fraction, expected) in [(0.0, "0%"), (0.005, "1%"), (0.699, "70%"), (1.0, "100%")] {
            let report = report_with_pressure(measured(fraction));
            let item = status_item(
                Some(&report),
                &Thresholds::default(),
                false,
                at(GENERATED_EPOCH),
            );
            assert_eq!(item.title.as_deref(), Some(expected), "for {fraction}");
        }
    }

    /// Requirement 9.2, including that the threshold itself is inside the band.
    #[test]
    fn crosses_into_attention_at_seventy_and_critical_at_ninety() {
        let cases = [
            (0.0, IconState::Neutral),
            (0.69, IconState::Neutral),
            (0.70, IconState::Attention),
            (0.89, IconState::Attention),
            (0.90, IconState::Critical),
            (1.0, IconState::Critical),
        ];
        for (fraction, expected) in cases {
            let report = report_with_pressure(measured(fraction));
            let item = status_item(
                Some(&report),
                &Thresholds::default(),
                false,
                at(GENERATED_EPOCH),
            );
            assert_eq!(item.icon, expected, "at {fraction}");
        }
    }

    #[test]
    fn the_thresholds_are_configurable() {
        let strict = Thresholds {
            attention: 0.50,
            critical: 0.60,
        };
        let report = report_with_pressure(measured(0.55));

        assert_eq!(
            status_item(Some(&report), &strict, false, at(GENERATED_EPOCH)).icon,
            IconState::Attention
        );
        assert_eq!(
            status_item(
                Some(&report),
                &Thresholds::default(),
                false,
                at(GENERATED_EPOCH)
            )
            .icon,
            IconState::Neutral,
            "the same figure is unremarkable under the defaults"
        );
    }

    /// Requirement 9.3: no number, neutral icon — and never a `0%` standing in
    /// for an absent figure.
    #[test]
    fn unmeasurable_pressure_shows_neutral_with_no_number() {
        let report = report_with_pressure(unmeasurable("no context window reported"));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );

        assert_eq!(item.icon, IconState::Neutral);
        assert_eq!(item.title, None);
        assert_ne!(item.title.as_deref(), Some("0%"));
        assert!(
            item.tooltip.contains("no context window reported"),
            "the tooltip explains the gap: {}",
            item.tooltip
        );
    }

    #[test]
    fn no_session_in_scope_shows_neutral_with_no_number() {
        let empty = json!({ "schemaVersion": 1, "generatedAt": GENERATED, "latestSession": null });
        let item = status_item(
            Some(&empty),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );

        assert_eq!(item.icon, IconState::Neutral);
        assert_eq!(item.title, None);
        assert!(item.tooltip.contains("no session in scope"));
    }

    #[test]
    fn no_report_at_all_shows_neutral_with_no_number() {
        let item = status_item(None, &Thresholds::default(), false, at(GENERATED_EPOCH));

        assert_eq!(item.icon, IconState::Neutral);
        assert_eq!(item.title, None);
    }

    /// Requirement 9.4: stale is its own state, and it outranks pressure —
    /// a critical number drawn over data from an hour ago would read as current.
    #[test]
    fn stale_replaces_every_pressure_state_and_drops_the_number() {
        for fraction in [0.1, 0.75, 0.95] {
            let report = report_with_pressure(measured(fraction));
            let item = status_item(
                Some(&report),
                &Thresholds::default(),
                true,
                at(GENERATED_EPOCH),
            );

            assert_eq!(item.icon, IconState::Stale, "at {fraction}");
            assert_eq!(item.title, None, "no number over stale data");
            assert!(item.tooltip.contains("not current"), "{}", item.tooltip);
        }
    }

    /// Requirement 9.5.
    #[test]
    fn the_tooltip_names_harness_project_pressure_and_age() {
        let report = report_with_pressure(measured(0.42));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH + 3 * 60),
        );

        assert!(item.tooltip.contains("claude-code"), "{}", item.tooltip);
        assert!(item.tooltip.contains("kyber-weave"), "{}", item.tooltip);
        assert!(item.tooltip.contains("42%"), "{}", item.tooltip);
        assert!(item.tooltip.contains("3m ago"), "{}", item.tooltip);
    }

    #[test]
    fn a_session_without_a_project_still_reads() {
        let mut report = report_with_pressure(measured(0.42));
        report["latestSession"]["project"] = json!(null);

        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );
        assert!(item.tooltip.contains("no project"), "{}", item.tooltip);
    }

    #[test]
    fn ages_read_coarsely() {
        let report = report_with_pressure(measured(0.42));
        let cases = [
            (0, "just now"),
            (59, "just now"),
            (60, "1m ago"),
            (3_600, "1h ago"),
            (86_400, "1d ago"),
        ];
        for (elapsed, expected) in cases {
            let item = status_item(
                Some(&report),
                &Thresholds::default(),
                false,
                at(GENERATED_EPOCH + elapsed),
            );
            assert!(
                item.tooltip.contains(expected),
                "after {elapsed}s expected {expected}, got {}",
                item.tooltip
            );
        }
    }

    /// Requirement 9.6. The fixture carries a cost, so this would fail if any
    /// of it leaked into the item.
    #[test]
    fn never_shows_cost() {
        let report = report_with_pressure(measured(0.42));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );

        let rendered = format!("{}{}", item.title.clone().unwrap_or_default(), item.tooltip);
        for forbidden in ["12.34", "$", "usd", "USD", "cost"] {
            assert!(
                !rendered.contains(forbidden),
                "{forbidden} reached the status item: {rendered}"
            );
        }
    }

    /// A report whose `generatedAt` is unusable must not panic or invent an age.
    #[test]
    fn an_unparseable_timestamp_reads_as_unknown_age() {
        let mut report = report_with_pressure(measured(0.42));
        report["generatedAt"] = json!("not a timestamp");

        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );
        assert!(item.tooltip.contains("age unknown"), "{}", item.tooltip);
    }

    /// A clock behind the report's own timestamp is not a negative age.
    #[test]
    fn a_report_from_the_future_reads_as_unknown_age() {
        let report = report_with_pressure(measured(0.42));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH - 600),
        );
        assert!(item.tooltip.contains("age unknown"), "{}", item.tooltip);
    }

    #[test]
    fn serializes_with_the_design_s_names() {
        let report = report_with_pressure(measured(0.95));
        let item = status_item(
            Some(&report),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );
        let json = serde_json::to_value(&item).unwrap();

        assert_eq!(json["icon"], "critical");
        assert_eq!(json["title"], "95%");
        assert!(json["tooltip"].is_string());
        assert_eq!(json["collector"], "down");
    }

    fn coverage_with(receiver: Value) -> Value {
        json!({ "receiver": receiver })
    }

    fn live_receiver() -> Value {
        json!({
            "status": "hosted",
            "listening": true,
            "hostedByUs": true,
            "paused": false,
            "healthy": true,
            "endpoint": "http://127.0.0.1:4318",
            "checkedAt": "2023-11-14T22:13:20Z",
            "lastAcceptedAt": "2023-11-14T22:13:00Z",
            "errors5m": { "total": 0, "last": null },
        })
    }

    /// Green only on the engine's own healthy word with a fresh check.
    #[test]
    fn collector_is_live_only_for_a_healthy_fresh_receiver() {
        let coverage = coverage_with(live_receiver());
        assert_eq!(
            collector_health(Some(&coverage), at(GENERATED_EPOCH)),
            CollectorState::Live
        );
    }

    /// Red for off, paused, failing, missing, or stale health. Unknown is red.
    #[test]
    fn collector_is_down_when_off_paused_failing_missing_or_stale() {
        // Missing coverage entirely.
        assert_eq!(
            collector_health(None, at(GENERATED_EPOCH)),
            CollectorState::Down
        );
        // No receiver key (servers predating the probe).
        assert_eq!(
            collector_health(Some(&json!({})), at(GENERATED_EPOCH)),
            CollectorState::Down
        );
        let mut cases: Vec<Value> = Vec::new();
        let mut paused = live_receiver();
        paused["paused"] = json!(true);
        cases.push(paused);
        let mut not_listening = live_receiver();
        not_listening["listening"] = json!(false);
        cases.push(not_listening);
        let mut unhealthy = live_receiver();
        unhealthy["healthy"] = json!(false);
        cases.push(unhealthy);
        let mut erroring = live_receiver();
        erroring["errors5m"] = json!({ "total": 2, "last": null });
        cases.push(erroring);
        let mut unknown_errors = live_receiver();
        unknown_errors["errors5m"] = json!({ "total": null, "last": null });
        cases.push(unknown_errors);
        let mut stale = live_receiver();
        stale["checkedAt"] = json!("2023-11-14T22:10:00Z");
        cases.push(stale);
        let mut no_check = live_receiver();
        no_check["checkedAt"] = Value::Null;
        cases.push(no_check);
        for receiver in cases {
            let coverage = coverage_with(receiver);
            assert_eq!(
                collector_health(Some(&coverage), at(GENERATED_EPOCH)),
                CollectorState::Down
            );
        }
    }

    /// Stale outranks everything, and the tooltip keeps the pressure detail
    /// while naming the collector state in words.
    #[test]
    fn stale_forces_the_collector_red_and_the_tooltip_names_both() {
        let report = report_with_pressure(measured(0.42));
        let coverage = coverage_with(live_receiver());
        let item = status_item_with_coverage(
            Some(&report),
            Some(&coverage),
            &Thresholds::default(),
            true,
            at(GENERATED_EPOCH),
        );
        assert_eq!(item.collector, CollectorState::Down);
        assert!(item.tooltip.contains("collector down"), "{}", item.tooltip);
        assert!(item.tooltip.contains("42%"), "{}", item.tooltip);
    }

    #[test]
    fn a_live_collector_is_named_in_the_tooltip() {
        let report = report_with_pressure(measured(0.42));
        let coverage = coverage_with(live_receiver());
        let item = status_item_with_coverage(
            Some(&report),
            Some(&coverage),
            &Thresholds::default(),
            false,
            at(GENERATED_EPOCH),
        );
        assert_eq!(item.collector, CollectorState::Live);
        assert!(item.tooltip.contains("collector live"), "{}", item.tooltip);
    }
}
