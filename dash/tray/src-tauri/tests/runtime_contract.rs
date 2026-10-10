//! RED contract: the tray is a display layer over the `kyberdash web` server.
//!
//! The tray owns no scheduler, no refresh worker, no receiver hosting and no
//! CLI-child job spawns. Every job lives in the web server; the tray reads
//! state and invokes actions only through the loopback HTTP API
//! (`/api/kyber/*`). Its only process duty is launching or attaching to that
//! server (`<state dir>/server.json`: pid, url, apiVersion).
//!
//! Test-side seams (stated plainly, because the production methods do not exist
//! yet):
//!
//! * `build()` is the single place that constructs `Runtime`. It uses today's
//!   `RuntimeConfig`/`RuntimeDependencies`; the green task removes the
//!   scheduler/receiver dependencies there and nowhere else. `settings_dir` is
//!   treated as the tray state dir that also holds `server.json`.
//! * `Seam` adapts the command surface. `refresh_now`/`clean_database` map to
//!   today's methods (which spawn CLI children, hence RED). `import_folder_history`
//!   and `set_shared_settings` have no `Runtime` method yet, so their seam bodies
//!   return an error naming the missing method. Green replaces those bodies with
//!   the real calls.
//!
//! All state lives in per-test temp dirs; the real home, `server.json` and
//! `jobs.lock` are never touched. The fake HTTP server binds an ephemeral port.

use std::collections::{HashMap, VecDeque};
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, SystemTime};

use anyhow::Result;
use kyberdash_tray_lib::api::LoopbackFetcher;
use kyberdash_tray_lib::ipc::{Phase, ViewState};
use kyberdash_tray_lib::runtime::{
    CleanScope, EventSink, Opener, Runtime, RuntimeConfig, RuntimeDependencies,
};
use kyberdash_tray_lib::supervisor::{Clock, ServerProcess, Spawner, SERVER_ARGS};
use serde_json::{json, Value};

const CLI: &str = "/opt/kyberdash/bin/kyberdash";
const BUSY_REFRESH: &str = "a refresh is already in progress";
const BUSY_CLEAN: &str = "a refresh or clean is already running — try again when it finishes";
static NEXT_SCRATCH_ID: AtomicU64 = AtomicU64::new(0);

fn at(seconds: u64) -> SystemTime {
    SystemTime::UNIX_EPOCH + Duration::from_secs(seconds)
}

// ---------------------------------------------------------------------------
// Fake loopback HTTP server
// ---------------------------------------------------------------------------

#[derive(Clone, Debug)]
struct Recorded {
    method: String,
    path: String,
    body: Value,
}

type Routes = Arc<Mutex<HashMap<(String, String), (u16, String)>>>;

struct FakeServer {
    url: String,
    requests: Arc<Mutex<Vec<Recorded>>>,
    routes: Routes,
    stop: Arc<AtomicBool>,
    port: u16,
    thread: Option<JoinHandle<()>>,
}

impl FakeServer {
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind fake server");
        let port = listener.local_addr().expect("local addr").port();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let routes: Routes = Arc::new(Mutex::new(HashMap::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let (r, ro, st) = (requests.clone(), routes.clone(), stop.clone());
        let thread = std::thread::spawn(move || {
            for stream in listener.incoming() {
                if st.load(Ordering::SeqCst) {
                    break;
                }
                if let Ok(stream) = stream {
                    serve(stream, &r, &ro);
                }
            }
        });
        let server = FakeServer {
            url: format!("http://127.0.0.1:{port}"),
            requests,
            routes,
            stop,
            port,
            thread: Some(thread),
        };
        server.route("GET", "/api/kyber/report", 200, report(0.42));
        // The tray probes the routes it needs, so a fake server stands for a
        // *current* one by default: unrouted means "this server never had
        // that route" (404), which is a state only the too-old tests want.
        server.route(
            "GET",
            "/api/kyber/jobs",
            200,
            json!({ "refresh": { "state": "idle", "lastSuccessAt": null, "lastFailure": null,
                                 "nextDueAt": null },
                    "paused": false, "storeGeneration": 1 }),
        );
        server
    }

    fn route(&self, method: &str, path: &str, status: u16, body: Value) {
        self.routes.lock().expect("routes").insert(
            (method.to_string(), path.to_string()),
            (status, body.to_string()),
        );
    }

    /// Requests other than the read-only report/jobs/settings polls.
    fn actions(&self) -> Vec<Recorded> {
        self.requests
            .lock()
            .expect("requests")
            .iter()
            .filter(|request| request.method != "GET")
            .cloned()
            .collect()
    }

    fn all_requests(&self) -> Vec<Recorded> {
        self.requests.lock().expect("requests").clone()
    }

    /// The status a route answers with; 404 for anything not routed, which is
    /// what an older server does for every route it never had.
    fn route_status_of(&self, method: &str, path: &str) -> u16 {
        self.routes
            .lock()
            .expect("routes")
            .get(&(method.to_string(), path.to_string()))
            .map(|(status, _)| *status)
            .unwrap_or(404)
    }
}

impl Drop for FakeServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        let _ = TcpStream::connect(("127.0.0.1", self.port));
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn serve(mut stream: TcpStream, requests: &Mutex<Vec<Recorded>>, routes: &Routes) {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
    let mut buffer = Vec::new();
    let mut chunk = [0u8; 4096];
    let header_end = loop {
        if let Some(position) = buffer.windows(4).position(|window| window == b"\r\n\r\n") {
            break position + 4;
        }
        match stream.read(&mut chunk) {
            Ok(0) | Err(_) => return,
            Ok(read) => buffer.extend_from_slice(&chunk[..read]),
        }
    };
    let head = String::from_utf8_lossy(&buffer[..header_end]).to_string();
    let mut first = head.lines().next().unwrap_or("").split_whitespace();
    let method = first.next().unwrap_or("").to_string();
    let target = first.next().unwrap_or("").to_string();
    let path = target.split('?').next().unwrap_or("").to_string();
    let length = head
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("content-length")
                .then(|| value.trim().parse::<usize>().ok())?
        })
        .unwrap_or(0);
    while buffer.len() < header_end + length {
        match stream.read(&mut chunk) {
            Ok(0) | Err(_) => break,
            Ok(read) => buffer.extend_from_slice(&chunk[..read]),
        }
    }
    let body = serde_json::from_slice(&buffer[header_end..]).unwrap_or(Value::Null);
    requests.lock().expect("requests").push(Recorded {
        method: method.clone(),
        path: path.clone(),
        body,
    });
    let (status, payload) = routes
        .lock()
        .expect("routes")
        .get(&(method, path))
        .cloned()
        .unwrap_or((404, "{}".to_string()));
    let response = format!(
        "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{payload}",
        payload.len()
    );
    let _ = stream.write_all(response.as_bytes());
}

fn report(pressure: f64) -> Value {
    json!({
        "schemaVersion": 1,
        "generatedAt": "2023-11-14T22:13:20Z",
        "latestSession": {
            "sessionId": "session-1",
            "harness": "codex",
            "project": "kyber-weave",
            "latestTurn": { "index": 1, "pressure": { "value": pressure } }
        }
    })
}

// ---------------------------------------------------------------------------
// Recording fakes for every effect the tray must NOT perform
// ---------------------------------------------------------------------------

type Calls = Arc<Mutex<Vec<(String, Vec<String>)>>>;

fn record(calls: &Calls, program: &str, args: &[&str]) {
    calls.lock().expect("calls").push((
        program.to_string(),
        args.iter().map(|arg| (*arg).to_string()).collect(),
    ));
}

struct FakeProcess {
    lines: VecDeque<String>,
    killed: Arc<AtomicBool>,
}

impl ServerProcess for FakeProcess {
    fn next_line(&mut self, _within: Duration) -> Option<String> {
        self.lines.pop_front()
    }
    fn kill_tree(&mut self) {
        self.killed.store(true, Ordering::SeqCst);
    }
}

/// The server launcher: announces the fake HTTP server as its listening URL.
struct FakeLauncher {
    calls: Calls,
    url: String,
    killed: Arc<AtomicBool>,
}

impl Spawner for FakeLauncher {
    fn spawn(&mut self, program: &str, args: &[&str]) -> io::Result<Box<dyn ServerProcess>> {
        record(&self.calls, program, args);
        let line = json!({
            "event": "kyberdash.web.listening",
            "url": self.url,
            "pid": std::process::id(),
            "version": "0.9.23",
            "apiVersion": 1
        })
        .to_string();
        Ok(Box::new(FakeProcess {
            lines: VecDeque::from([line]),
            killed: self.killed.clone(),
        }))
    }
}

struct NoopClock;
impl Clock for NoopClock {
    fn sleep(&mut self, _duration: Duration) {}
}

type EventLog = Arc<Mutex<Vec<(String, Value)>>>;

struct RecordingEvents {
    events: EventLog,
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

    fn write_server_json(&self, url: &str) {
        let document = json!({ "pid": std::process::id(), "url": url, "apiVersion": 1 });
        std::fs::write(self.root.join("server.json"), document.to_string())
            .expect("write server.json");
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Everything a scenario needs: the runtime plus the recorders for effects the
/// display layer must never perform.
struct Harness {
    runtime: Runtime,
    server: FakeServer,
    scratch: Scratch,
    launcher_calls: Calls,
    refresh_child_calls: Calls,
    receiver_child_calls: Calls,
    events: EventLog,
    opened: Arc<Mutex<Vec<String>>>,
    launcher_killed: Arc<AtomicBool>,
}

/// `attached`: server.json names the live fake server, so no launch is needed.
/// Otherwise the launcher is the only way to reach it.
fn build(attached: bool) -> Harness {
    let server = FakeServer::start();
    let scratch = Scratch::new();
    if attached {
        scratch.write_server_json(&server.url);
    }
    let launcher_calls: Calls = Arc::default();
    let refresh_child_calls: Calls = Arc::default();
    let receiver_child_calls: Calls = Arc::default();
    let events: EventLog = Arc::default();
    let opened: Arc<Mutex<Vec<String>>> = Arc::default();
    let launcher_killed = Arc::new(AtomicBool::new(false));
    let runtime = Runtime::new(
        RuntimeConfig {
            cli_program: Some(CLI.to_string()),
            settings_dir: scratch.path().to_path_buf(),
            now: at(1_700_000_000),
        },
        RuntimeDependencies {
            server_spawner: Box::new(FakeLauncher {
                calls: launcher_calls.clone(),
                url: server.url.clone(),
                killed: launcher_killed.clone(),
            }),
            fetcher: Box::new(LoopbackFetcher::new().expect("loopback fetcher")),
            clock: Box::new(NoopClock),
            event_sink: Box::new(RecordingEvents {
                events: events.clone(),
            }),
            opener: Box::new(RecordingOpener {
                opened: opened.clone(),
            }),
        },
    );
    Harness {
        runtime,
        server,
        scratch,
        launcher_calls,
        refresh_child_calls,
        receiver_child_calls,
        events,
        opened,
        launcher_killed,
    }
}

fn started(attached: bool) -> Harness {
    let mut harness = build(attached);
    harness.runtime.start().expect("runtime start");
    harness
}

fn state_json(runtime: &Runtime) -> Value {
    serde_json::to_value(runtime.get_view_state()).expect("serialize view state")
}

// ---------------------------------------------------------------------------
// Command seam (see module docs)
// ---------------------------------------------------------------------------

struct Seam;

impl Seam {
    fn refresh_now(rt: &mut Runtime) -> Result<()> {
        rt.refresh_now(at(1_700_000_100))
    }

    fn clean(rt: &mut Runtime, scope: CleanScope, import_weeks: Option<u32>) -> Result<()> {
        rt.clean_database(scope, import_weeks)
    }

    fn import_folder_history(rt: &mut Runtime, weeks: u32, harness: Option<&str>) -> Result<()> {
        rt.import_folder_history(weeks, harness)
    }

    fn set_shared_settings(rt: &mut Runtime, patch: Value) -> Result<()> {
        rt.set_shared_settings(patch)
    }
}

fn launcher_only(calls: &Calls) -> Vec<(String, Vec<String>)> {
    calls.lock().expect("calls").clone()
}

fn server_launch() -> (String, Vec<String>) {
    (
        CLI.to_string(),
        SERVER_ARGS.iter().map(|arg| (*arg).to_string()).collect(),
    )
}

// ---------------------------------------------------------------------------
// No scheduler, no receiver hosting, no CLI-child jobs
// ---------------------------------------------------------------------------

/// Regression guard for the architecture rule: with a live compatible server
/// named by server.json, the tray attaches and spawns nothing at all, across
/// start, a tick a day later, and the display-only settings command.
#[test]
fn attaching_to_a_live_server_spawns_nothing_across_start_tick_and_settings() {
    let mut h = started(true);

    h.runtime.tick(at(1_700_000_000 + 86_400)).expect("tick");
    h.runtime
        .set_settings(json!({ "harness": "codex", "windowDays": 14 }))
        .expect("display settings");
    h.runtime.quit();

    assert!(
        launcher_only(&h.launcher_calls).is_empty(),
        "attach, not launch"
    );
    assert!(
        launcher_only(&h.refresh_child_calls).is_empty(),
        "the tray has no refresh worker"
    );
    assert!(
        launcher_only(&h.receiver_child_calls).is_empty(),
        "the tray hosts no receiver"
    );
    assert!(
        h.server.actions().is_empty(),
        "a tick must not schedule work on the server either: {:?}",
        h.server.actions()
    );
}

/// With no server.json the launcher is the only child the tray may start, and
/// it starts it exactly once.
#[test]
fn without_a_live_server_the_launcher_is_the_only_child_ever_spawned() {
    let mut h = started(false);

    h.runtime.tick(at(1_700_000_000 + 86_400)).expect("tick");
    h.runtime
        .tick(at(1_700_000_000 + 2 * 86_400))
        .expect("tick");
    h.runtime.quit();

    assert_eq!(launcher_only(&h.launcher_calls), vec![server_launch()]);
    assert!(launcher_only(&h.refresh_child_calls).is_empty());
    assert!(launcher_only(&h.receiver_child_calls).is_empty());
}

/// Every command, including the ones that used to spawn `dash refresh` and
/// `dash clean`, goes through HTTP and spawns no child.
#[test]
fn no_command_spawns_a_cli_child() {
    let mut h = started(true);
    h.server.route("POST", "/api/kyber/refresh", 200, json!({}));
    h.server.route("POST", "/api/kyber/clean", 200, json!({}));
    h.server
        .route("POST", "/api/kyber/import-history", 200, json!({}));
    h.server.route("PUT", "/api/kyber/settings", 200, json!({}));

    let _ = Seam::refresh_now(&mut h.runtime);
    let _ = Seam::clean(&mut h.runtime, CleanScope::All, None);
    let _ = Seam::import_folder_history(&mut h.runtime, 4, None);
    let _ = Seam::set_shared_settings(&mut h.runtime, json!({ "x": 1 }));
    h.runtime.quit();

    assert!(
        launcher_only(&h.refresh_child_calls).is_empty(),
        "commands must not run CLI children: {:?}",
        launcher_only(&h.refresh_child_calls)
    );
    assert!(launcher_only(&h.receiver_child_calls).is_empty());
}

// ---------------------------------------------------------------------------
// Commands map to the loopback HTTP API
// ---------------------------------------------------------------------------

#[test]
fn refresh_now_posts_the_tray_surface_to_the_server() {
    let mut h = started(true);
    h.server.route("POST", "/api/kyber/refresh", 200, json!({}));

    Seam::refresh_now(&mut h.runtime).expect("refresh_now");

    let actions = h.server.actions();
    assert_eq!(actions.len(), 1, "one request: {actions:?}");
    assert_eq!(
        (actions[0].method.as_str(), actions[0].path.as_str()),
        ("POST", "/api/kyber/refresh")
    );
    assert_eq!(actions[0].body, json!({ "surface": "tray" }));
}

#[test]
fn clean_database_posts_scope_and_omits_reingest_weeks_unless_given() {
    let mut h = started(true);
    h.server.route("POST", "/api/kyber/clean", 200, json!({}));

    Seam::clean(&mut h.runtime, CleanScope::All, None).expect("clean all");
    Seam::clean(
        &mut h.runtime,
        CleanScope::harness("cursor").unwrap(),
        Some(4),
    )
    .expect("clean harness with import");

    let actions = h.server.actions();
    assert_eq!(actions.len(), 2, "two POSTs: {actions:?}");
    assert!(actions
        .iter()
        .all(|a| a.method == "POST" && a.path == "/api/kyber/clean"));
    assert_eq!(actions[0].body["scope"], json!("all"));
    assert!(
        actions[0].body.get("reingestWeeks").is_none(),
        "no importWeeks -> no reingestWeeks: {}",
        actions[0].body
    );
    assert!(
        actions[1].body["scope"].to_string().contains("cursor"),
        "harness scope carries the harness: {}",
        actions[1].body
    );
    assert_eq!(actions[1].body["reingestWeeks"], json!(4));
}

#[test]
fn import_folder_history_posts_weeks_and_optional_harness() {
    let mut h = started(true);
    h.server
        .route("POST", "/api/kyber/import-history", 200, json!({}));

    Seam::import_folder_history(&mut h.runtime, 12, None).expect("import all harnesses");
    Seam::import_folder_history(&mut h.runtime, 52, Some("codex")).expect("import one harness");

    let actions = h.server.actions();
    assert_eq!(actions.len(), 2, "two POSTs: {actions:?}");
    assert!(actions
        .iter()
        .all(|a| a.method == "POST" && a.path == "/api/kyber/import-history"));
    assert_eq!(actions[0].body, json!({ "weeks": 12 }));
    assert_eq!(actions[1].body, json!({ "weeks": 52, "harness": "codex" }));
}

#[test]
fn import_folder_history_validates_weeks_before_any_request() {
    let mut h = started(true);
    h.server
        .route("POST", "/api/kyber/import-history", 200, json!({}));
    let before = h.server.all_requests().len();

    for weeks in [0, 53, 1000] {
        assert!(
            Seam::import_folder_history(&mut h.runtime, weeks, None).is_err(),
            "weeks={weeks} must be rejected"
        );
    }

    assert_eq!(
        h.server.all_requests().len(),
        before,
        "invalid weeks must not reach the server"
    );
    Seam::import_folder_history(&mut h.runtime, 1, None).expect("1 is the lower bound");
    assert_eq!(h.server.actions().len(), 1);
}

#[test]
fn set_shared_settings_puts_the_patch_to_the_server_settings_endpoint() {
    let mut h = started(true);
    h.server.route("PUT", "/api/kyber/settings", 200, json!({}));
    let patch = json!({ "refreshMinutes": 15, "hostReceiver": true });

    Seam::set_shared_settings(&mut h.runtime, patch.clone()).expect("set_shared_settings");

    let actions = h.server.actions();
    assert_eq!(actions.len(), 1, "one request: {actions:?}");
    assert_eq!(
        (actions[0].method.as_str(), actions[0].path.as_str()),
        ("PUT", "/api/kyber/settings")
    );
    assert_eq!(actions[0].body, patch);
}

/// Shared (server-owned) knobs must not be persisted by the tray; only the
/// display preferences are.
#[test]
fn set_settings_persists_display_preferences_only_and_calls_no_server_action() {
    let mut h = started(true);

    h.runtime
        .set_settings(json!({
            "harness": "codex",
            "windowDays": 14,
            "refreshMinutes": 15,
            "hostReceiver": true
        }))
        .expect("set_settings");

    let persisted: Value = serde_json::from_str(
        &std::fs::read_to_string(h.scratch.path().join("settings.json")).expect("settings.json"),
    )
    .expect("settings json");
    assert_eq!(persisted["harness"], json!("codex"));
    assert_eq!(persisted["windowDays"], json!(14));
    for shared in ["refreshMinutes", "hostReceiver"] {
        assert!(
            persisted.get(shared).is_none(),
            "{shared} is server-owned and must not be persisted by the tray: {persisted}"
        );
    }
    assert!(h.server.actions().is_empty());
}

#[test]
fn http_409_maps_to_the_existing_busy_messages() {
    let mut h = started(true);
    h.server.route(
        "POST",
        "/api/kyber/refresh",
        409,
        json!({ "error": "busy" }),
    );
    h.server
        .route("POST", "/api/kyber/clean", 409, json!({ "error": "busy" }));
    h.server.route(
        "POST",
        "/api/kyber/import-history",
        409,
        json!({ "error": "busy" }),
    );

    let refresh = Seam::refresh_now(&mut h.runtime).expect_err("409 is an error");
    let clean = Seam::clean(&mut h.runtime, CleanScope::All, None).expect_err("409 is an error");
    let import = Seam::import_folder_history(&mut h.runtime, 4, None).expect_err("409 is an error");

    assert_eq!(refresh.to_string(), BUSY_REFRESH);
    assert_eq!(clean.to_string(), BUSY_CLEAN);
    assert_eq!(import.to_string(), BUSY_CLEAN);
}

// ---------------------------------------------------------------------------
// ViewState.jobs / ViewState.sharedSettings come from the server
// ---------------------------------------------------------------------------

#[test]
fn view_state_jobs_and_shared_settings_come_from_the_loopback_api() {
    let mut h = build(true);
    let jobs = json!({ "refresh": { "state": "idle" }, "clean": { "state": "running" } });
    let settings = json!({ "refreshMinutes": 15, "hostReceiver": false });
    h.server.route("GET", "/api/kyber/jobs", 200, jobs.clone());
    h.server
        .route("GET", "/api/kyber/settings", 200, settings.clone());

    h.runtime.start().expect("start");
    h.runtime.poll_report(at(1_700_000_015)).expect("poll");

    let state = state_json(&h.runtime);
    assert_eq!(state["jobs"], jobs);
    assert_eq!(state["sharedSettings"], settings);
}

/// Unknown must stay unknown: a default object would present invented job and
/// settings state as if the server had said it.
#[test]
fn jobs_and_shared_settings_are_null_not_defaults_when_the_fetch_fails() {
    let mut h = build(true);
    h.server
        .route("GET", "/api/kyber/jobs", 500, json!({ "error": "boom" }));
    // /api/kyber/settings is unrouted -> 404.

    h.runtime.start().expect("start");
    h.runtime.poll_report(at(1_700_000_015)).expect("poll");

    let state = state_json(&h.runtime);
    let object = state.as_object().expect("view state is an object");
    for key in ["jobs", "sharedSettings"] {
        assert!(object.contains_key(key), "ViewState must carry {key}");
        assert!(
            object[key].is_null(),
            "{key} must be null on failure, got {}",
            object[key]
        );
    }
}

#[test]
fn a_later_fetch_failure_never_invents_a_default_jobs_object() {
    let mut h = build(true);
    h.server.route(
        "GET",
        "/api/kyber/jobs",
        200,
        json!({ "refresh": { "state": "idle" } }),
    );
    h.server.route(
        "GET",
        "/api/kyber/settings",
        200,
        json!({ "refreshMinutes": 5 }),
    );
    h.runtime.start().expect("start");
    assert!(
        !state_json(&h.runtime)["jobs"].is_null(),
        "precondition: jobs known"
    );

    h.server.route("GET", "/api/kyber/jobs", 500, json!({}));
    h.runtime.poll_report(at(1_700_000_030)).expect("poll");

    let state = state_json(&h.runtime);
    assert!(state.as_object().expect("object").contains_key("jobs"));
    // The contract only says null on fetch failure: no invented default may
    // appear. Either unknown (null) or the last value the server really said.
    assert!(
        state["jobs"].is_null() || state["jobs"] == json!({ "refresh": { "state": "idle" } }),
        "no default jobs object may be invented after a failed fetch: {}",
        state["jobs"]
    );
}

// ---------------------------------------------------------------------------
// The IPC allowlist is exactly nine commands
// ---------------------------------------------------------------------------

const NINE_COMMANDS: [&str; 9] = [
    "get_view_state",
    "refresh_now",
    "open_view",
    "set_settings",
    "quit",
    "hide_popover",
    "clean_database",
    "import_folder_history",
    "set_shared_settings",
];

#[test]
fn authorized_commands_are_exactly_the_nine_popover_commands() {
    let mut commands: Vec<&str> = Runtime::authorized_commands().to_vec();
    commands.sort_unstable();
    let mut expected = NINE_COMMANDS.to_vec();
    expected.sort_unstable();

    assert_eq!(commands, expected);
    assert!(!Runtime::is_authorized("run_arbitrary_process"));
}

#[test]
fn the_capability_json_lists_exactly_the_nine_custom_command_permissions() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("capabilities/tray.json");
    let manifest: Value =
        serde_json::from_str(&std::fs::read_to_string(&path).expect("read capability"))
            .expect("parse capability");
    let mut custom: Vec<String> = manifest["permissions"]
        .as_array()
        .expect("permissions array")
        .iter()
        .filter_map(Value::as_str)
        .filter(|permission| !permission.contains(':'))
        .map(str::to_string)
        .collect();
    custom.sort_unstable();
    let mut expected: Vec<String> = NINE_COMMANDS
        .iter()
        .map(|command| format!("allow-{}", command.replace('_', "-")))
        .collect();
    expected.sort_unstable();

    assert_eq!(custom, expected);
}

#[test]
fn the_generate_handler_registers_exactly_the_nine_commands() {
    // The third place the nine names appear (after `authorized_commands` and
    // `capabilities/tray.json`). Tauri serves an IPC call only if the command is
    // registered here, so an allowlist that drifts from this list turns the webview's
    // buttons into silent no-ops -- or worse, leaves a command routable that the
    // capability file no longer permits.
    let source =
        std::fs::read_to_string(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("src/lib.rs"))
            .expect("read lib.rs");
    // Comments are stripped before parsing so a commented-out command line cannot
    // silently join (or leave) the list.
    let uncommented: String = source
        .lines()
        .map(|line| line.split_once("//").map_or(line, |(head, _)| head))
        .collect::<Vec<_>>()
        .join("\n");
    let start = uncommented
        .find("generate_handler![")
        .expect("lib.rs registers an invoke_handler");
    let body_start = start + "generate_handler![".len();
    let end = uncommented[body_start..]
        .find(']')
        .expect("generate_handler! list is closed")
        + body_start;

    let mut registered: Vec<String> = uncommented[body_start..end]
        .split(',')
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .map(str::to_string)
        .collect();
    registered.sort();
    let mut expected: Vec<String> = NINE_COMMANDS.iter().map(|c| c.to_string()).collect();
    expected.sort();

    assert_eq!(registered, expected);
}

// ---------------------------------------------------------------------------
// Display and lifecycle behavior the tray keeps (restored, re-expressed over
// the loopback HTTP API and server.json)
// ---------------------------------------------------------------------------

fn pressure_of(value: &Value) -> Value {
    value["latestSession"]["latestTurn"]["pressure"]["value"].clone()
}

#[test]
fn a_failed_fetch_keeps_the_last_report_stale_until_a_later_success() {
    let mut h = started(true);
    assert_eq!(state_json(&h.runtime)["phase"], json!("ready"));

    h.server.route(
        "GET",
        "/api/kyber/report",
        503,
        json!({ "error": "server stopped" }),
    );
    h.runtime
        .poll_report(at(1_700_000_015))
        .expect("failed poll is state");
    let stale = h.runtime.get_view_state();
    assert_eq!(stale.phase, Phase::Stale);
    assert_eq!(stale.report, Some(report(0.42)));
    assert!(
        stale
            .error
            .as_deref()
            .is_some_and(|error| !error.is_empty()),
        "stale state carries the failure message: {:?}",
        stale.error
    );

    h.server
        .route("GET", "/api/kyber/report", 200, report(0.43));
    h.runtime
        .poll_report(at(1_700_000_030))
        .expect("recovery poll");
    let recovered = h.runtime.get_view_state();
    assert_eq!(recovered.phase, Phase::Ready);
    assert_eq!(recovered.report, Some(report(0.43)));
    assert!(recovered.error.is_none());
}

#[test]
fn report_changes_publish_full_snapshots_without_respawning_the_server() {
    let mut h = started(false);
    h.server
        .route("GET", "/api/kyber/report", 200, report(0.91));

    h.runtime
        .poll_report(at(1_700_000_015))
        .expect("report poll");

    let events = h.events.lock().expect("event log").clone();
    assert_eq!(events.len(), 2, "one snapshot at start, one per poll");
    assert!(events.iter().all(|(name, _)| name == "view-state-changed"));
    assert_eq!(pressure_of(&events[1].1["report"]), json!(0.91));
    assert_eq!(
        launcher_only(&h.launcher_calls),
        vec![server_launch()],
        "polling must not spawn the server again"
    );
}

#[test]
fn startup_readiness_launches_only_the_server() {
    let h = started(false);

    let state = h.runtime.get_view_state();
    assert_eq!(state.phase, Phase::Ready);
    assert_eq!(state.report, Some(report(0.42)));
    assert_eq!(launcher_only(&h.launcher_calls), vec![server_launch()]);
    // "No refresh child" is asserted by the RED guards above, not here, so
    // this readiness contract stays green on today's runtime.
}

#[test]
fn open_view_reaches_the_opener_only_after_loopback_route_validation() {
    let mut h = started(true);

    h.runtime.open_view("finding/f-1").expect("valid route");
    assert!(h.runtime.open_view("../etc/passwd").is_err());

    assert_eq!(
        h.opened.lock().expect("opened").as_slice(),
        [format!("{}/finding/f-1", h.server.url)]
    );
}

#[test]
fn set_settings_persists_a_partial_patch_and_updates_the_snapshot() {
    let mut h = started(true);

    h.runtime
        .set_settings(json!({ "harness": "codex", "windowDays": 14 }))
        .expect("settings update");

    let state = h.runtime.get_view_state();
    assert_eq!(state.settings.harness, "codex");
    assert_eq!(state.settings.window_days, 14);
    assert_eq!(
        kyberdash_tray_lib::settings::load(h.scratch.path()),
        state.settings
    );
}

#[test]
fn quitting_reaps_the_owned_server_child() {
    let mut h = started(false);
    assert!(!h.launcher_killed.load(Ordering::SeqCst));

    h.runtime.quit();

    assert!(
        h.launcher_killed.load(Ordering::SeqCst),
        "the tray-owned server child must be killed on quit"
    );
}

#[test]
fn quitting_never_kills_an_attached_server() {
    let mut h = started(true);

    h.runtime.quit();

    assert!(
        launcher_only(&h.launcher_calls).is_empty(),
        "attached, not owned"
    );
    assert!(
        !h.launcher_killed.load(Ordering::SeqCst),
        "an attached server is not the tray's to kill"
    );
}

#[test]
fn refresh_now_busy_rejection_clears_once_the_server_stops_answering_409() {
    let mut h = started(true);
    h.server.route(
        "POST",
        "/api/kyber/refresh",
        409,
        json!({ "error": "busy" }),
    );

    let busy = Seam::refresh_now(&mut h.runtime).expect_err("409 is busy");
    assert_eq!(busy.to_string(), BUSY_REFRESH);

    h.server.route("POST", "/api/kyber/refresh", 200, json!({}));
    Seam::refresh_now(&mut h.runtime).expect("the next request after a non-409 succeeds");
}

#[test]
fn clean_database_rejects_an_unknown_harness_without_any_request() {
    let h = started(true);
    let before = h.server.all_requests().len();

    assert!(CleanScope::harness("../etc/passwd").is_err());

    assert_eq!(h.server.all_requests().len(), before, "nothing was sent");
    assert!(h.server.actions().is_empty());
}

#[test]
fn a_missing_server_enters_setup_without_spawning_or_presenting_old_data() {
    let mut h = build(false);
    h.runtime.clear_cli_for_test();

    h.runtime.start().expect("setup is a visible state");

    let state = h.runtime.get_view_state();
    assert_eq!(state.phase, Phase::Setup);
    assert!(state.report.is_none());
    assert!(launcher_only(&h.launcher_calls).is_empty());
    assert!(launcher_only(&h.refresh_child_calls).is_empty());
    assert!(
        h.server.all_requests().is_empty(),
        "no old data was fetched"
    );
}

// ---------------------------------------------------------------------------
// Capability: an apiVersion that passes is not the same as routes that exist
// ---------------------------------------------------------------------------

/// The version gate cannot catch this case on its own. `REPORT_SCHEMA_VERSION`
/// is still 1, so a hand-started older `kyberdash web` passes `MIN_API_VERSION`
/// and the tray then finds no `/api/kyber/jobs`, no `/settings`, no `/refresh`
/// and no `/import-history`. Every action would 404 and jobs/settings would sit
/// at null with nothing saying why. The tray must probe the routes itself and
/// treat a 404 the way it treats an API version below the minimum: setup, with
/// no action presented as working.
#[test]
fn a_server_without_the_tray_routes_enters_setup_and_sends_no_actions() {
    let mut h = build(true);
    // `build(true)` already wrote an apiVersion of 1, which is exactly what a
    // hand-started older server records: the version gate waves it through.
    // Every route the tray needs answers 404; only /report exists, which is
    // exactly the shape of the server this change is about.
    h.server.route(
        "GET",
        "/api/kyber/jobs",
        404,
        json!({ "error": "not found" }),
    );
    h.server.route(
        "GET",
        "/api/kyber/settings",
        404,
        json!({ "error": "not found" }),
    );
    assert_eq!(
        h.server.route_status_of("GET", "/api/kyber/jobs"),
        404,
        "precondition: the old server has no jobs route"
    );

    h.runtime.start().expect("setup is a visible state");

    let state = state_json(&h.runtime);
    assert_eq!(state["phase"], "setup");
    assert_eq!(
        state["setup"]["reason"], "too-old",
        "an older server is the same condition as an older API"
    );
    assert!(
        !state["setup"]["remedy"]
            .as_str()
            .unwrap_or_default()
            .is_empty(),
        "the setup state must carry a remedy"
    );
    assert!(state["report"].is_null());
    assert!(state["jobs"].is_null());
    assert!(state["sharedSettings"].is_null());

    // Nothing was posted: an action against a server that cannot serve it
    // would 404, so the tray must not put the popover's actions on screen.
    assert!(
        h.server.actions().is_empty(),
        "no action may be sent to a server that cannot serve them: {:?}",
        h.server.actions()
    );
}

/// The probe is a capability check, not a health check: a server that has the
/// route but is failing it must not be declared too old, because that would
/// replace a stale banner (which says "try again") with setup (which says
/// "update KyberDash").
#[test]
fn a_server_whose_jobs_route_is_failing_is_not_declared_too_old() {
    let mut h = build(true);
    h.server
        .route("GET", "/api/kyber/jobs", 500, json!({ "error": "boom" }));
    h.server.route(
        "GET",
        "/api/kyber/settings",
        200,
        json!({ "folderImportScheduled": false, "jobsPaused": false,
                "refreshCadenceMinutes": 15, "receiverHosted": false }),
    );

    h.runtime.start().expect("start");

    let state = state_json(&h.runtime);
    assert_ne!(state["phase"], "setup", "a 500 is not a missing route");
    assert!(state["sharedSettings"].is_object());
}
