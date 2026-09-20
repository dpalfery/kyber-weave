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
}

/// Builds the status item from the report, the settings and the phase.
///
/// `stale` folds together the two causes Requirement 9.4 names — an unreachable
/// server and a failed refresh — because the item renders them identically and
/// the distinction belongs in the footer, which has room to explain it.
pub fn status_item(
    report: Option<&Value>,
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

    StatusItem {
        icon,
        title,
        tooltip: tooltip_for(session, pressure, stale, report, now),
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
fn tooltip_for(
    session: Option<&Value>,
    pressure: Option<f64>,
    stale: bool,
    report: Option<&Value>,
    now: SystemTime,
) -> String {
    let Some(session) = session else {
        return if stale {
            "KyberDash — data may be out of date".to_string()
        } else {
            "KyberDash — no session in scope".to_string()
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
    format!("{harness} · {project} · {pressure_text} · {age}{suffix}")
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
    }
}
