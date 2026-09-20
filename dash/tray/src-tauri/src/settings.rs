//! The tray's own settings, as JSON in the Tauri app-config directory.
//!
//! The defaults are fixed by the spec rather than by taste: harness `all` and a
//! 7-day window match `kyberdash report`'s own defaults, the 5-minute cadence is
//! Requirement 10.1's, the 0.70 and 0.90 thresholds are 9.2's, and launch at
//! login (6.8) and receiver hosting (10.7) are both off because each starts a
//! process the user did not ask for.
//!
//! `set_settings` takes a partial document, as the design's IPC surface
//! specifies, so the popover can change one field without having to send —
//! and therefore be able to overwrite — the rest.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::status_item::Thresholds;

/// Defaults, named so a test asserts the value rather than restating the
/// literal next to it.
pub const DEFAULT_HARNESS: &str = "all";
pub const DEFAULT_WINDOW_DAYS: u32 = 7;
pub const DEFAULT_REFRESH_MINUTES: u32 = 5;
pub const DEFAULT_ATTENTION_THRESHOLD: f64 = 0.70;
pub const DEFAULT_CRITICAL_THRESHOLD: f64 = 0.90;

/// The design's `TraySettings`.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TraySettings {
    /// A harness id, or `all`.
    pub harness: String,
    pub window_days: u32,
    pub refresh_minutes: u32,
    pub attention_threshold: f64,
    pub critical_threshold: f64,
    pub launch_at_login: bool,
    pub host_receiver: bool,
}

impl Default for TraySettings {
    fn default() -> Self {
        TraySettings {
            harness: DEFAULT_HARNESS.to_string(),
            window_days: DEFAULT_WINDOW_DAYS,
            refresh_minutes: DEFAULT_REFRESH_MINUTES,
            attention_threshold: DEFAULT_ATTENTION_THRESHOLD,
            critical_threshold: DEFAULT_CRITICAL_THRESHOLD,
            launch_at_login: false,
            host_receiver: false,
        }
    }
}

impl TraySettings {
    pub fn thresholds(&self) -> Thresholds {
        Thresholds {
            attention: self.attention_threshold,
            critical: self.critical_threshold,
        }
    }

    pub fn refresh_cadence(&self) -> std::time::Duration {
        std::time::Duration::from_secs(u64::from(self.refresh_minutes) * 60)
    }

    /// Merges a partial document, ignoring fields it does not carry.
    ///
    /// A value of the wrong type, or outside what the field can mean, is
    /// dropped rather than stored: these arrive from the webview, and a
    /// `windowDays` of zero or a threshold of 400% would render as nonsense
    /// long after the call that set it.
    pub fn apply_partial(&mut self, patch: &Value) {
        if let Some(harness) = patch.get("harness").and_then(|v| v.as_str()) {
            if !harness.trim().is_empty() {
                self.harness = harness.to_string();
            }
        }
        if let Some(days) = patch.get("windowDays").and_then(|v| v.as_u64()) {
            if (1..=3650).contains(&days) {
                self.window_days = days as u32;
            }
        }
        if let Some(minutes) = patch.get("refreshMinutes").and_then(|v| v.as_u64()) {
            if (1..=1440).contains(&minutes) {
                self.refresh_minutes = minutes as u32;
            }
        }
        if let Some(value) = patch.get("attentionThreshold").and_then(|v| v.as_f64()) {
            if is_fraction(value) {
                self.attention_threshold = value;
            }
        }
        if let Some(value) = patch.get("criticalThreshold").and_then(|v| v.as_f64()) {
            if is_fraction(value) {
                self.critical_threshold = value;
            }
        }
        if let Some(value) = patch.get("launchAtLogin").and_then(|v| v.as_bool()) {
            self.launch_at_login = value;
        }
        if let Some(value) = patch.get("hostReceiver").and_then(|v| v.as_bool()) {
            self.host_receiver = value;
        }
        // Applied after both, because either assignment can invert the pair.
        self.repair_thresholds();
    }

    /// Attention must not sit above critical; the status item would then reach
    /// critical without ever passing through attention.
    fn repair_thresholds(&mut self) {
        if self.attention_threshold > self.critical_threshold {
            std::mem::swap(&mut self.attention_threshold, &mut self.critical_threshold);
        }
    }
}

fn is_fraction(value: f64) -> bool {
    value.is_finite() && (0.0..=1.0).contains(&value)
}

pub fn settings_path(config_dir: &Path) -> PathBuf {
    config_dir.join("settings.json")
}

/// Reads the settings, falling back to defaults.
///
/// A missing file is a first run. An unreadable or malformed one is also
/// answered with defaults: this is read at startup, and a hand-edited file must
/// leave the user with a working tray they can fix from, not a tray that will
/// not start.
pub fn load(config_dir: &Path) -> TraySettings {
    let Ok(text) = std::fs::read_to_string(settings_path(config_dir)) else {
        return TraySettings::default();
    };
    match serde_json::from_str::<TraySettings>(&text) {
        Ok(settings) => settings,
        Err(_) => {
            // A document missing fields is still worth honouring for the ones
            // it has, which is the shape an older version's file takes.
            let mut settings = TraySettings::default();
            if let Ok(value) = serde_json::from_str::<Value>(&text) {
                settings.apply_partial(&value);
            }
            settings
        }
    }
}

/// Writes the settings atomically, so a crash mid-write cannot leave a file
/// that `load` has to fall back from on the next start.
pub fn save(config_dir: &Path, settings: &TraySettings) -> Result<()> {
    std::fs::create_dir_all(config_dir).context("creating the tray config directory")?;
    let path = settings_path(config_dir);
    let temporary = path.with_extension("json.tmp");
    let text = serde_json::to_string_pretty(settings).context("serializing the tray settings")?;
    std::fs::write(&temporary, text).context("writing the tray settings")?;
    std::fs::rename(&temporary, &path).context("installing the tray settings")?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    struct Scratch {
        root: PathBuf,
    }

    impl Scratch {
        fn new(tag: &str) -> Self {
            let root = std::env::temp_dir().join(format!(
                "kyberdash-settings-{tag}-{}-{:?}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            std::fs::create_dir_all(&root).unwrap();
            Scratch { root }
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            std::fs::remove_dir_all(&self.root).ok();
        }
    }

    /// The defaults the task pins, each read off the struct rather than the
    /// constant it was built from.
    #[test]
    fn the_defaults_are_the_specified_ones() {
        let settings = TraySettings::default();

        assert_eq!(settings.harness, "all");
        assert_eq!(settings.window_days, 7);
        assert_eq!(settings.refresh_minutes, 5);
        assert_eq!(settings.attention_threshold, 0.70);
        assert_eq!(settings.critical_threshold, 0.90);
        assert!(!settings.launch_at_login, "6.8 defaults to off");
        assert!(!settings.host_receiver, "10.7 defaults to off");
    }

    #[test]
    fn the_defaults_feed_the_status_item_and_the_scheduler() {
        let settings = TraySettings::default();

        assert_eq!(settings.thresholds(), Thresholds::default());
        assert_eq!(
            settings.refresh_cadence(),
            crate::scheduler::DEFAULT_CADENCE
        );
    }

    #[test]
    fn round_trips_through_json() {
        let scratch = Scratch::new("roundtrip");
        let settings = TraySettings {
            harness: "claude-code".to_string(),
            window_days: 30,
            refresh_minutes: 15,
            attention_threshold: 0.6,
            critical_threshold: 0.85,
            launch_at_login: true,
            host_receiver: true,
        };

        save(&scratch.root, &settings).unwrap();
        assert_eq!(load(&scratch.root), settings);
    }

    /// The popover reads and writes these names.
    #[test]
    fn serializes_with_the_design_s_names() {
        let json = serde_json::to_value(TraySettings::default()).unwrap();

        assert_eq!(json["harness"], "all");
        assert_eq!(json["windowDays"], 7);
        assert_eq!(json["refreshMinutes"], 5);
        assert_eq!(json["attentionThreshold"], 0.70);
        assert_eq!(json["criticalThreshold"], 0.90);
        assert_eq!(json["launchAtLogin"], false);
        assert_eq!(json["hostReceiver"], false);
    }

    #[test]
    fn a_first_run_reads_the_defaults() {
        let scratch = Scratch::new("firstrun");
        assert_eq!(load(&scratch.root), TraySettings::default());
    }

    /// A hand-edited file must leave a working tray, not an unstartable one.
    #[test]
    fn a_malformed_file_falls_back_to_defaults() {
        let scratch = Scratch::new("malformed");
        std::fs::write(settings_path(&scratch.root), "{ not json").unwrap();

        assert_eq!(load(&scratch.root), TraySettings::default());
    }

    /// An older version's file has fewer fields; the ones it does carry still count.
    #[test]
    fn a_partial_file_keeps_what_it_carries() {
        let scratch = Scratch::new("partial");
        std::fs::write(
            settings_path(&scratch.root),
            r#"{"harness":"codex","launchAtLogin":true}"#,
        )
        .unwrap();

        let loaded = load(&scratch.root);
        assert_eq!(loaded.harness, "codex");
        assert!(loaded.launch_at_login);
        assert_eq!(loaded.window_days, DEFAULT_WINDOW_DAYS, "the rest default");
    }

    /// The design's `set_settings` takes a partial document.
    #[test]
    fn a_partial_patch_changes_only_what_it_names() {
        let mut settings = TraySettings::default();
        settings.apply_partial(&json!({ "harness": "codex" }));

        assert_eq!(settings.harness, "codex");
        assert_eq!(settings.window_days, DEFAULT_WINDOW_DAYS);
        assert_eq!(settings.refresh_minutes, DEFAULT_REFRESH_MINUTES);
        assert!(!settings.launch_at_login);
    }

    /// These arrive from the webview, so a value that cannot mean anything is
    /// dropped rather than stored and rendered later.
    #[test]
    fn nonsense_values_are_refused() {
        let mut settings = TraySettings::default();
        settings.apply_partial(&json!({
            "windowDays": 0,
            "refreshMinutes": 0,
            "attentionThreshold": 4.0,
            "criticalThreshold": -1.0,
            "harness": "   ",
        }));

        assert_eq!(settings, TraySettings::default(), "nothing may be stored");

        settings.apply_partial(&json!({ "windowDays": "seven", "launchAtLogin": "yes" }));
        assert_eq!(settings, TraySettings::default(), "wrong types are ignored");
    }

    /// Attention above critical would let the icon reach critical without ever
    /// passing through attention.
    #[test]
    fn inverted_thresholds_are_put_back_in_order() {
        let mut settings = TraySettings::default();
        settings.apply_partial(&json!({ "attentionThreshold": 0.95, "criticalThreshold": 0.5 }));

        assert_eq!(settings.attention_threshold, 0.5);
        assert_eq!(settings.critical_threshold, 0.95);

        // Also when only one of the pair moves.
        let mut one_sided = TraySettings::default();
        one_sided.apply_partial(&json!({ "attentionThreshold": 0.99 }));
        assert!(one_sided.attention_threshold <= one_sided.critical_threshold);
    }

    #[test]
    fn saving_twice_leaves_no_temporary_file() {
        let scratch = Scratch::new("atomic");
        save(&scratch.root, &TraySettings::default()).unwrap();
        save(&scratch.root, &TraySettings::default()).unwrap();

        let entries: Vec<String> = std::fs::read_dir(&scratch.root)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(entries, vec!["settings.json"]);
    }
}
