//! Runs `kyberdash dash refresh` on a cadence, and never twice at once.
//!
//! Requirement 10.1 runs one at start and then every *cadence* (5 minutes by
//! default). 10.2 has Refresh now start one immediately unless one is already
//! running, and 10.3 extends "already running" to a refresh a terminal started —
//! the tray is not the only thing that can hold the store's lock.
//!
//! 10.4 gives that collision a distinct exit code rather than a message to
//! parse. `REFRESH_BUSY_EXIT_CODE` is 3 in `src/cli/register.ts`, and
//! [`RefreshOutcome::from_exit_code`] is the only place this crate decides what
//! an exit code meant.

use std::time::{Duration, SystemTime};

use serde::Serialize;

/// Default cadence of Requirement 10.1.
pub const DEFAULT_CADENCE: Duration = Duration::from_secs(5 * 60);

/// The exit code `dash refresh` uses when another refresh holds the lock
/// (Requirement 10.4, `REFRESH_BUSY_EXIT_CODE` in src/cli/register.ts).
pub const REFRESH_BUSY_EXIT_CODE: i32 = 3;

/// The arguments the tray refreshes with. Incremental, as 10.1 requires — no
/// flag, because incremental is what `dash refresh` already does.
pub const REFRESH_ARGS: [&str; 2] = ["dash", "refresh"];

/// The `refresh` arm of the design's `ViewState`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum RefreshState {
    #[default]
    Idle,
    Running,
    /// Something else holds the store's refresh lock (10.3).
    RunningElsewhere,
    Failed,
}

/// What one refresh run did.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RefreshOutcome {
    Succeeded,
    /// Exit 3: another process holds the lock and nothing was written.
    Busy,
    Failed(String),
}

impl RefreshOutcome {
    /// Requirement 10.4's whole point: "already running" is distinguishable
    /// from "broken" without reading the message.
    pub fn from_exit_code(code: Option<i32>, stderr: &str) -> Self {
        match code {
            Some(0) => RefreshOutcome::Succeeded,
            Some(REFRESH_BUSY_EXIT_CODE) => RefreshOutcome::Busy,
            Some(other) => RefreshOutcome::Failed(summarize(stderr, format!("exit {other}"))),
            // Killed by a signal, or never started.
            None => RefreshOutcome::Failed(summarize(stderr, "terminated".to_string())),
        }
    }
}

/// The footer shows this, so it has to be one readable line, not a wall of
/// someone else's stack trace.
fn summarize(stderr: &str, fallback: String) -> String {
    stderr
        .lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .map(|line| line.chars().take(200).collect())
        .unwrap_or(fallback)
}

/// Runs a refresh to completion.
pub trait RefreshRunner {
    /// The process's exit code and stderr.
    fn run(&mut self, program: &str, args: &[&str]) -> (Option<i32>, String);
}

/// Refresh state the footer renders (10.5): when it last worked, and what went
/// wrong last, each surviving the other.
#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshStatus {
    pub state: RefreshState,
    pub last_success_at: Option<String>,
    pub last_failure: Option<String>,
}

pub struct Scheduler {
    program: String,
    cadence: Duration,
    status: RefreshStatus,
    running: bool,
    last_run_at: Option<SystemTime>,
}

impl Scheduler {
    pub fn new(program: impl Into<String>, cadence: Duration) -> Self {
        Scheduler {
            program: program.into(),
            cadence,
            status: RefreshStatus::default(),
            running: false,
            last_run_at: None,
        }
    }

    pub fn status(&self) -> &RefreshStatus {
        &self.status
    }

    pub fn cadence(&self) -> Duration {
        self.cadence
    }

    pub fn set_cadence(&mut self, cadence: Duration) {
        self.cadence = cadence;
    }

    /// Requirement 10.1: one at start, then one per cadence.
    ///
    /// `never run` is due immediately, which is what makes the start-up refresh
    /// fall out of the same rule rather than being a special case.
    pub fn is_due(&self, now: SystemTime) -> bool {
        match self.last_run_at {
            None => true,
            Some(last) => now
                .duration_since(last)
                .map(|elapsed| elapsed >= self.cadence)
                // A clock that went backwards is not a reason to refresh early.
                .unwrap_or(false),
        }
    }

    /// Requirement 10.3: a refresh a terminal started blocks the tray's, and it
    /// is visible only in the report's `coverage.refresh.inProgress`.
    ///
    /// Taking the report as a `Value` keeps the tray out of the business of
    /// re-modelling the document (7.5); the one field it needs is read here.
    pub fn observe_report(&mut self, report: Option<&serde_json::Value>) {
        let in_progress = report
            .and_then(|r| r.get("coverage"))
            .and_then(|c| c.get("refresh"))
            .and_then(|r| r.get("inProgress"))
            .is_some_and(|v| !v.is_null());

        if in_progress && !self.running {
            self.status.state = RefreshState::RunningElsewhere;
        } else if !in_progress && self.status.state == RefreshState::RunningElsewhere {
            // Whoever held it finished; the next cadence tick may proceed.
            self.status.state = RefreshState::Idle;
        }
    }

    /// True when a refresh must not be started: one of ours is running, or
    /// someone else's is (10.2, 10.3).
    pub fn is_blocked(&self) -> bool {
        self.running || self.status.state == RefreshState::RunningElsewhere
    }

    /// Runs a refresh unless one is already running.
    ///
    /// Returns `None` when it declined, which is what Refresh now needs to show
    /// "a refresh is in progress" rather than silently doing nothing.
    pub fn run_now<R: RefreshRunner>(
        &mut self,
        runner: &mut R,
        now: SystemTime,
    ) -> Option<RefreshOutcome> {
        if self.is_blocked() {
            return None;
        }

        self.running = true;
        self.status.state = RefreshState::Running;
        let (code, stderr) = runner.run(&self.program, &REFRESH_ARGS);
        self.running = false;
        self.last_run_at = Some(now);

        let outcome = RefreshOutcome::from_exit_code(code, &stderr);
        match &outcome {
            RefreshOutcome::Succeeded => {
                self.status.state = RefreshState::Idle;
                self.status.last_success_at = Some(format_rfc3339(now));
            }
            RefreshOutcome::Busy => {
                // Nothing was written, so this is not a failure to report —
                // the store is simply busy.
                self.status.state = RefreshState::RunningElsewhere;
            }
            RefreshOutcome::Failed(reason) => {
                // Requirement 10.5: the last success time survives the failure.
                self.status.state = RefreshState::Failed;
                self.status.last_failure = Some(format!("{} at {}", reason, format_rfc3339(now)));
            }
        }
        Some(outcome)
    }

    /// The cadence tick: runs only when due and not blocked.
    pub fn tick<R: RefreshRunner>(
        &mut self,
        runner: &mut R,
        now: SystemTime,
    ) -> Option<RefreshOutcome> {
        if !self.is_due(now) {
            return None;
        }
        self.run_now(runner, now)
    }
}

fn format_rfc3339(at: SystemTime) -> String {
    let datetime: chrono::DateTime<chrono::Utc> = at.into();
    datetime.to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// Replays scripted exit codes and records every invocation.
    #[derive(Default)]
    struct FakeRunner {
        outcomes: Vec<(Option<i32>, String)>,
        calls: Vec<(String, Vec<String>)>,
    }

    impl FakeRunner {
        fn with(codes: Vec<i32>) -> Self {
            FakeRunner {
                outcomes: codes
                    .into_iter()
                    .map(|c| (Some(c), String::new()))
                    .collect(),
                calls: Vec::new(),
            }
        }
    }

    impl RefreshRunner for FakeRunner {
        fn run(&mut self, program: &str, args: &[&str]) -> (Option<i32>, String) {
            self.calls.push((
                program.to_string(),
                args.iter().map(|a| a.to_string()).collect(),
            ));
            if self.outcomes.is_empty() {
                (Some(0), String::new())
            } else {
                self.outcomes.remove(0)
            }
        }
    }

    fn at(secs: u64) -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(secs)
    }

    fn report_with_refresh_in_progress(in_progress: bool) -> serde_json::Value {
        json!({
            "coverage": {
                "refresh": {
                    "lastSuccessAt": null,
                    "lastFailure": null,
                    "inProgress": if in_progress { json!({"pid": 999, "since": "2026-01-01T00:00:00Z"}) } else { json!(null) },
                }
            }
        })
    }

    /// Requirement 10.1: at start, then every cadence.
    #[test]
    fn runs_at_start_then_once_per_cadence() {
        let mut runner = FakeRunner::with(vec![0, 0]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        assert!(scheduler.is_due(at(0)), "a tray that just started is due");
        assert!(scheduler.tick(&mut runner, at(0)).is_some());

        // Inside the cadence: not due.
        assert!(!scheduler.is_due(at(299)));
        assert!(scheduler.tick(&mut runner, at(299)).is_none());

        // On the cadence: due again.
        assert!(scheduler.is_due(at(300)));
        assert!(scheduler.tick(&mut runner, at(300)).is_some());

        assert_eq!(runner.calls.len(), 2);
        assert_eq!(
            runner.calls[0],
            (
                "kyberdash".to_string(),
                vec!["dash".to_string(), "refresh".to_string()]
            )
        );
    }

    #[test]
    fn the_default_cadence_is_five_minutes() {
        assert_eq!(DEFAULT_CADENCE, Duration::from_secs(300));
    }

    /// Requirement 10.2: Refresh now does not wait for the cadence.
    #[test]
    fn refresh_now_runs_off_cadence() {
        let mut runner = FakeRunner::with(vec![0, 0]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.run_now(&mut runner, at(0));
        assert!(!scheduler.is_due(at(10)), "not due yet");

        assert_eq!(
            scheduler.run_now(&mut runner, at(10)),
            Some(RefreshOutcome::Succeeded),
            "Refresh now ignores the cadence"
        );
        assert_eq!(runner.calls.len(), 2);
    }

    /// Requirement 10.4: exit 3 is "busy", and it is read as a code, not a
    /// message.
    #[test]
    fn exit_three_is_running_elsewhere() {
        assert_eq!(
            RefreshOutcome::from_exit_code(Some(3), ""),
            RefreshOutcome::Busy
        );
        assert_eq!(
            RefreshOutcome::from_exit_code(Some(0), ""),
            RefreshOutcome::Succeeded
        );
        assert!(matches!(
            RefreshOutcome::from_exit_code(Some(1), "boom"),
            RefreshOutcome::Failed(_)
        ));
        assert!(matches!(
            RefreshOutcome::from_exit_code(None, ""),
            RefreshOutcome::Failed(_)
        ));
    }

    #[test]
    fn a_busy_exit_puts_the_state_in_running_elsewhere() {
        let mut runner = FakeRunner::with(vec![REFRESH_BUSY_EXIT_CODE]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        assert_eq!(
            scheduler.run_now(&mut runner, at(0)),
            Some(RefreshOutcome::Busy)
        );
        assert_eq!(scheduler.status().state, RefreshState::RunningElsewhere);
        // Nothing was written, so this is not a failure to show the user.
        assert_eq!(scheduler.status().last_failure, None);
    }

    /// Requirement 10.5: a failure records when and why, and does not erase the
    /// last success.
    #[test]
    fn a_failure_keeps_the_last_success_time() {
        let mut runner = FakeRunner {
            outcomes: vec![
                (Some(0), String::new()),
                (
                    Some(1),
                    "canon.db is locked by another writer\n".to_string(),
                ),
            ],
            calls: Vec::new(),
        };
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.run_now(&mut runner, at(1_700_000_000));
        let success = scheduler.status().last_success_at.clone();
        assert_eq!(success.as_deref(), Some("2023-11-14T22:13:20.000Z"));

        scheduler.run_now(&mut runner, at(1_700_000_600));

        assert_eq!(scheduler.status().state, RefreshState::Failed);
        assert_eq!(
            scheduler.status().last_success_at,
            success,
            "the last success must survive the failure"
        );
        let failure = scheduler.status().last_failure.clone().unwrap();
        assert!(failure.contains("canon.db is locked"), "{failure}");
        assert!(failure.contains("2023-11-14T22:23:20"), "{failure}");
    }

    /// Requirement 10.3: a terminal's refresh blocks the tray's, and the only
    /// evidence is the report.
    #[test]
    fn a_refresh_started_from_a_terminal_blocks_a_new_one() {
        let mut runner = FakeRunner::with(vec![0]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.observe_report(Some(&report_with_refresh_in_progress(true)));

        assert!(scheduler.is_blocked());
        assert_eq!(scheduler.status().state, RefreshState::RunningElsewhere);
        assert_eq!(
            scheduler.run_now(&mut runner, at(0)),
            None,
            "Refresh now must decline while someone else holds the lock"
        );
        assert!(runner.calls.is_empty(), "no process may be started");
    }

    /// When the other refresh finishes, the tray is free again.
    #[test]
    fn the_block_clears_when_the_other_refresh_finishes() {
        let mut runner = FakeRunner::with(vec![0]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.observe_report(Some(&report_with_refresh_in_progress(true)));
        assert!(scheduler.is_blocked());

        scheduler.observe_report(Some(&report_with_refresh_in_progress(false)));
        assert!(!scheduler.is_blocked());
        assert_eq!(scheduler.status().state, RefreshState::Idle);
        assert!(scheduler.run_now(&mut runner, at(0)).is_some());
    }

    /// A report that has not arrived, or carries no coverage block, is not
    /// evidence of a refresh in progress.
    #[test]
    fn a_missing_report_does_not_block() {
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.observe_report(None);
        assert!(!scheduler.is_blocked());

        scheduler.observe_report(Some(&json!({})));
        assert!(!scheduler.is_blocked());
    }

    /// A clock that jumps backwards must not trigger a refresh storm.
    #[test]
    fn a_backwards_clock_does_not_make_a_refresh_due() {
        let mut runner = FakeRunner::with(vec![0]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);

        scheduler.run_now(&mut runner, at(1_700_000_000));
        assert!(!scheduler.is_due(at(1_699_999_000)));
    }

    #[test]
    fn the_cadence_is_configurable() {
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);
        let mut runner = FakeRunner::with(vec![0]);

        scheduler.set_cadence(Duration::from_secs(60));
        scheduler.run_now(&mut runner, at(0));

        assert!(!scheduler.is_due(at(59)));
        assert!(scheduler.is_due(at(60)));
    }

    /// The UI reads these as the design's `ViewState.refresh`.
    #[test]
    fn serializes_with_the_design_s_names() {
        let mut runner = FakeRunner::with(vec![REFRESH_BUSY_EXIT_CODE]);
        let mut scheduler = Scheduler::new("kyberdash", DEFAULT_CADENCE);
        scheduler.run_now(&mut runner, at(0));

        let json = serde_json::to_value(scheduler.status()).unwrap();
        assert_eq!(json["state"], "running-elsewhere");
        assert!(json["lastSuccessAt"].is_null());
        assert!(json["lastFailure"].is_null());
    }
}
