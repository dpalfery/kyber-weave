//! RED contract for the tray runtime composition.
//!
//! The helper modules in `src/` are intentionally tested in their own unit
//! modules. These tests cover the missing composition boundary: one owned web
//! server, one refresh scheduler, one report cache, event snapshots, the
//! receiver opt-in, and command effects. The `runtime` seam is expected to be
//! supplied by the implementation task; until then this file is intentionally
//! red because the webview-facing runtime does not exist.

use std::collections::VecDeque;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, SystemTime};

use anyhow::Result;
use kyberdash_tray_lib::api::ReportFetcher;
use kyberdash_tray_lib::ipc::{Phase, ViewState};
use kyberdash_tray_lib::receiver::{HealthProbe, Probe, ReceiverSpawner};
use kyberdash_tray_lib::runtime::{EventSink, Opener, Runtime, RuntimeConfig, RuntimeDependencies};
use kyberdash_tray_lib::scheduler::{RefreshRunner, REFRESH_ARGS};
use kyberdash_tray_lib::supervisor::{Clock, ServerProcess, Spawner, SERVER_ARGS};
use serde_json::{json, Value};

const CLI: &str = "/opt/kyberdash/bin/kyberdash";
const SERVER_LINE: &str = r#"{"event":"kyberdash.web.listening","url":"http://127.0.0.1:4811","pid":41,"version":"0.9.23","apiVersion":1}"#;
const GENERATED_AT: &str = "2023-11-14T22:13:20Z";
static NEXT_SCRATCH_ID: AtomicU64 = AtomicU64::new(0);

fn at(seconds: u64) -> SystemTime {
    SystemTime::UNIX_EPOCH + Duration::from_secs(seconds)
}

fn report(pressure: f64) -> Value {
    json!({
        "schemaVersion": 1,
        "generatedAt": GENERATED_AT,
        "latestSession": {
            "sessionId": "session-1",
            "harness": "codex",
            "project": "kyber-weave",
            "latestTurn": { "index": 1, "pressure": { "value": pressure } }
        }
    })
}

#[derive(Clone, Default)]
struct ChildState {
    killed: Arc<Mutex<bool>>,
}

struct FakeProcess {
    lines: VecDeque<String>,
    state: ChildState,
}

impl FakeProcess {
    fn listening() -> (Self, ChildState) {
        let state = ChildState::default();
        (
            FakeProcess {
                lines: VecDeque::from([SERVER_LINE.to_string()]),
                state: state.clone(),
            },
            state,
        )
    }
}

impl ServerProcess for FakeProcess {
    fn next_line(&mut self, _within: Duration) -> Option<String> {
        self.lines.pop_front()
    }

    fn kill_tree(&mut self) {
        *self.state.killed.lock().expect("child state lock") = true;
    }
}

#[derive(Default)]
struct FakeSpawner {
    calls: Vec<(String, Vec<String>)>,
    processes: VecDeque<FakeProcess>,
    child_state: Option<ChildState>,
}

impl FakeSpawner {
    fn with_listening_server() -> Self {
        let (process, child_state) = FakeProcess::listening();
        FakeSpawner {
            calls: Vec::new(),
            processes: VecDeque::from([process]),
            child_state: Some(child_state),
        }
    }

    fn child_state(&self) -> ChildState {
        self.child_state
            .clone()
            .expect("listening server child state")
    }
}

impl Spawner for FakeSpawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> io::Result<Box<dyn ServerProcess>> {
        self.calls.push((
            program.to_string(),
            args.iter().map(|arg| (*arg).to_string()).collect(),
        ));
        self.processes
            .pop_front()
            .map(|process| Box::new(process) as Box<dyn ServerProcess>)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "no scripted process"))
    }
}

#[derive(Default)]
struct FakeClock {
    sleeps: Vec<Duration>,
}

impl Clock for FakeClock {
    fn sleep(&mut self, duration: Duration) {
        self.sleeps.push(duration);
    }
}

#[derive(Default)]
struct FakeRefreshRunner {
    calls: Vec<(String, Vec<String>)>,
    outcomes: VecDeque<(Option<i32>, String)>,
    block_at_call: Option<(usize, RefreshGate)>,
}

impl FakeRefreshRunner {
    fn successful() -> Self {
        FakeRefreshRunner {
            calls: Vec::new(),
            outcomes: VecDeque::from([(Some(0), String::new())]),
            block_at_call: None,
        }
    }

    fn successful_then_holds(gate: RefreshGate) -> Self {
        FakeRefreshRunner {
            calls: Vec::new(),
            outcomes: VecDeque::from([(Some(0), String::new()), (Some(0), String::new())]),
            block_at_call: Some((2, gate)),
        }
    }
}

impl RefreshRunner for FakeRefreshRunner {
    fn run(&mut self, program: &str, args: &[&str]) -> (Option<i32>, String) {
        self.calls.push((
            program.to_string(),
            args.iter().map(|arg| (*arg).to_string()).collect(),
        ));
        if let Some((blocked_call, gate)) = &self.block_at_call {
            if *blocked_call == self.calls.len() {
                gate.hold_until_released();
            }
        }
        self.outcomes
            .pop_front()
            .unwrap_or((Some(0), String::new()))
    }
}

#[derive(Clone, Default)]
struct RefreshGate {
    state: Arc<(Mutex<RefreshGateState>, Condvar)>,
}

#[derive(Default)]
struct RefreshGateState {
    started: bool,
    released: bool,
    finished: bool,
}

impl RefreshGate {
    fn hold_until_released(&self) {
        let (lock, wake) = &*self.state;
        let mut state = lock.lock().expect("refresh gate lock");
        state.started = true;
        wake.notify_all();
        while !state.released {
            state = wake.wait(state).expect("refresh gate wait");
        }
        state.finished = true;
        wake.notify_all();
    }

    fn wait_until_started(&self) {
        let (lock, wake) = &*self.state;
        let mut state = lock.lock().expect("refresh gate lock");
        while !state.started {
            state = wake.wait(state).expect("refresh gate wait");
        }
    }

    fn release(&self) {
        let (lock, wake) = &*self.state;
        let mut state = lock.lock().expect("refresh gate lock");
        state.released = true;
        wake.notify_all();
    }

    fn wait_until_finished(&self) {
        let (lock, wake) = &*self.state;
        let mut state = lock.lock().expect("refresh gate lock");
        while !state.finished {
            state = wake.wait(state).expect("refresh gate wait");
        }
    }
}

struct FakeFetcher {
    responses: VecDeque<Result<Value>>,
    urls: Arc<Mutex<Vec<String>>>,
}

impl FakeFetcher {
    fn with_reports(reports: impl IntoIterator<Item = Result<Value>>) -> Self {
        FakeFetcher {
            responses: reports.into_iter().collect(),
            urls: Arc::new(Mutex::new(Vec::new())),
        }
    }
}

impl ReportFetcher for FakeFetcher {
    fn fetch(&mut self, url: &str) -> Result<Value> {
        self.urls
            .lock()
            .expect("fetch URL lock")
            .push(url.to_string());
        self.responses
            .pop_front()
            .unwrap_or_else(|| Err(anyhow::anyhow!("connection refused")))
    }
}

#[derive(Default)]
struct FakeProbe {
    probes: VecDeque<Probe>,
}

impl HealthProbe for FakeProbe {
    fn probe(&mut self, _url: &str) -> Probe {
        self.probes.pop_front().unwrap_or(Probe::Refused)
    }
}

#[derive(Default)]
struct FakeReceiverSpawner {
    calls: Vec<(String, Vec<String>)>,
}

impl ReceiverSpawner for FakeReceiverSpawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> io::Result<()> {
        self.calls.push((
            program.to_string(),
            args.iter().map(|arg| (*arg).to_string()).collect(),
        ));
        Ok(())
    }
}

#[derive(Clone, Default)]
struct RecordingEvents {
    events: Arc<Mutex<Vec<(String, Value)>>>,
}

impl EventSink for RecordingEvents {
    fn emit(&mut self, name: &str, state: &ViewState) -> Result<()> {
        self.events
            .lock()
            .expect("event lock")
            .push((name.to_string(), serde_json::to_value(state)?));
        Ok(())
    }
}

#[derive(Clone, Default)]
struct RecordingOpener {
    opened: Arc<Mutex<Vec<String>>>,
}

impl Opener for RecordingOpener {
    fn open(&mut self, url: &str) -> Result<()> {
        self.opened
            .lock()
            .expect("opener lock")
            .push(url.to_string());
        Ok(())
    }
}

struct Scratch {
    root: PathBuf,
}

impl Scratch {
    fn new() -> Self {
        let root = std::env::temp_dir().join(format!(
            "kyberdash-runtime-contract-{}-{}",
            std::process::id(),
            NEXT_SCRATCH_ID.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir_all(&root).expect("scratch directory");
        Scratch { root }
    }

    fn path(&self) -> &Path {
        &self.root
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn runtime(
    settings_dir: &Path,
    spawner: FakeSpawner,
    refresh: FakeRefreshRunner,
    fetcher: FakeFetcher,
    events: RecordingEvents,
    opener: RecordingOpener,
) -> Runtime {
    Runtime::new(
        RuntimeConfig {
            cli_program: Some(CLI.to_string()),
            settings_dir: settings_dir.to_path_buf(),
            cadence: Duration::from_secs(300),
            now: at(1_700_000_000),
        },
        RuntimeDependencies {
            server_spawner: Box::new(spawner),
            refresh_runner: Box::new(refresh),
            fetcher: Box::new(fetcher),
            clock: Box::new(FakeClock::default()),
            receiver_probe: Box::new(FakeProbe::default()),
            receiver_spawner: Box::new(FakeReceiverSpawner::default()),
            event_sink: Box::new(events),
            opener: Box::new(opener),
        },
    )
}

#[test]
fn startup_resolves_one_cli_starts_one_server_and_refreshes_once() {
    let scratch = Scratch::new();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");

    let state = runtime.get_view_state();
    assert_eq!(state.phase, Phase::Ready);
    assert_eq!(state.report, Some(report(0.42)));
    assert_eq!(
        runtime.server_calls(),
        vec![(
            CLI.to_string(),
            SERVER_ARGS.iter().map(|arg| (*arg).to_string()).collect(),
        )]
    );
    assert_eq!(
        runtime.refresh_calls(),
        vec![(
            CLI.to_string(),
            REFRESH_ARGS.iter().map(|arg| (*arg).to_string()).collect(),
        )]
    );
}

#[test]
fn report_changes_publish_full_snapshots_and_reuse_the_supervised_server() {
    let scratch = Scratch::new();
    let events = RecordingEvents::default();
    let event_log = events.events.clone();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42)), Ok(report(0.91))]),
        events,
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");
    runtime.poll_report(at(1_700_000_015)).expect("report poll");

    let events = event_log.lock().expect("event log lock");
    assert_eq!(events.len(), 2);
    assert!(events.iter().all(|(name, _)| name == "view-state-changed"));
    assert_eq!(
        events[1].1["report"]["latestSession"]["latestTurn"]["pressure"]["value"],
        0.91
    );
    assert_eq!(
        runtime.server_calls().len(),
        1,
        "polling must not spawn a CLI per request"
    );
}

#[test]
fn authorized_commands_are_exactly_the_six_popover_commands() {
    let commands = Runtime::authorized_commands();

    assert_eq!(
        commands,
        [
            "get_view_state",
            "refresh_now",
            "open_view",
            "set_settings",
            "quit",
            "hide_popover",
        ]
    );
    assert!(!Runtime::is_authorized("run_arbitrary_process"));
}

#[test]
fn refresh_now_rejects_only_while_the_first_explicit_refresh_is_in_progress() {
    let scratch = Scratch::new();
    let gate = RefreshGate::default();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful_then_holds(gate.clone()),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");
    runtime.refresh_now(at(1_700_000_030)).expect("refresh now");
    gate.wait_until_started();
    assert_eq!(runtime.refresh_calls().len(), 2);

    assert!(
        runtime.refresh_now(at(1_700_000_031)).is_err(),
        "a second refresh is rejected while the first is held"
    );
    assert_eq!(runtime.refresh_calls().len(), 2);

    gate.release();
    gate.wait_until_finished();
    runtime
        .refresh_now(at(1_700_000_032))
        .expect("refresh after the first completes");
    assert_eq!(runtime.refresh_calls().len(), 3);
}

#[test]
fn open_view_reaches_the_opener_only_after_loopback_route_validation() {
    let scratch = Scratch::new();
    let opener = RecordingOpener::default();
    let opened = opener.opened.clone();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        opener,
    );

    runtime.start().expect("runtime startup");
    runtime.open_view("finding/f-1").expect("valid route");
    assert!(runtime.open_view("../etc/passwd").is_err());
    assert_eq!(
        opened.lock().expect("opened URL lock").as_slice(),
        ["http://127.0.0.1:4811/finding/f-1"]
    );
}

#[test]
fn set_settings_persists_a_partial_patch_and_updates_the_snapshot() {
    let scratch = Scratch::new();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");
    runtime
        .set_settings(json!({ "harness": "codex", "refreshMinutes": 15 }))
        .expect("settings update");

    let state = runtime.get_view_state();
    assert_eq!(state.settings.harness, "codex");
    assert_eq!(state.settings.refresh_minutes, 15);
    assert_eq!(
        kyberdash_tray_lib::settings::load(scratch.path()),
        state.settings
    );
}

#[test]
fn missing_cli_enters_setup_without_spawning_or_presenting_old_data() {
    let scratch = Scratch::new();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::default(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );
    runtime.clear_cli_for_test();

    runtime.start().expect("setup is a visible state");

    let state = runtime.get_view_state();
    assert_eq!(state.phase, Phase::Setup);
    assert!(state.report.is_none());
    assert!(runtime.server_calls().is_empty());
    assert!(runtime.refresh_calls().is_empty());
}

#[test]
fn a_failed_fetch_keeps_the_last_report_stale_until_a_later_success() {
    let scratch = Scratch::new();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([
            Ok(report(0.42)),
            Err(anyhow::anyhow!("server stopped")),
            Ok(report(0.43)),
        ]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");
    runtime
        .poll_report(at(1_700_000_015))
        .expect("failed poll is state");
    let stale = runtime.get_view_state();
    assert_eq!(stale.phase, Phase::Stale);
    assert_eq!(stale.report, Some(report(0.42)));
    assert!(stale
        .error
        .as_deref()
        .is_some_and(|error| error.contains("server stopped")));

    runtime
        .poll_report(at(1_700_000_030))
        .expect("recovery poll");
    let recovered = runtime.get_view_state();
    assert_eq!(recovered.phase, Phase::Ready);
    assert_eq!(recovered.report, Some(report(0.43)));
    assert!(recovered.error.is_none());
}

#[test]
fn receiver_hosting_is_opt_in_and_port_held_by_another_process_is_not_retried() {
    let scratch = Scratch::new();
    let mut runtime = runtime(
        scratch.path(),
        FakeSpawner::with_listening_server(),
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );

    runtime.start().expect("runtime startup");
    assert!(runtime.receiver_calls().is_empty(), "hosting defaults off");

    runtime
        .set_settings(json!({ "hostReceiver": true }))
        .expect("enable receiver");
    runtime.set_receiver_probe(Probe::OtherService);
    runtime.poll_receiver().expect("receiver probe");
    runtime.poll_receiver().expect("latched receiver probe");
    assert!(runtime.receiver_calls().is_empty());
}

#[test]
fn quitting_reaps_the_owned_server_child() {
    let scratch = Scratch::new();
    let spawner = FakeSpawner::with_listening_server();
    let child = spawner.child_state();
    let mut runtime = runtime(
        scratch.path(),
        spawner,
        FakeRefreshRunner::successful(),
        FakeFetcher::with_reports([Ok(report(0.42))]),
        RecordingEvents::default(),
        RecordingOpener::default(),
    );
    runtime.start().expect("runtime startup");

    runtime.quit();

    assert!(*child.killed.lock().expect("child state lock"));
}
