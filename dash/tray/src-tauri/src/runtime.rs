//! Composition root for the tray's long-lived runtime.
//!
//! The tray is a display layer (architecture rule R1): it owns no scheduler, no
//! refresh worker, no receiver and spawns no CLI job. It reads state and relays
//! the operator's actions through the loopback HTTP API of the `kyberdash web`
//! server, and its only process duty is launching or attaching to that server
//! (see [`crate::supervisor`]).
//!
//! The runtime remains synchronous on purpose: the real Tauri bridge runs it
//! through `spawn_blocking`, while tests can substitute every system effect
//! without a network connection or a child process.

use std::path::PathBuf;
use std::time::{Duration, SystemTime};

use anyhow::{anyhow, bail, Result};
use serde_json::{json, Value};

use crate::api::{
    self, LoopbackClient, Method, CLEAN_PATH, COVERAGE_PATH, IMPORT_HISTORY_PATH, JOBS_PATH,
    REFRESH_PATH, SETTINGS_PATH, VERSION_PATH,
};
use crate::cli::{self, Resolution, SetupReason, SetupState};
use crate::ipc::{self, ServerDocs, ViewState};
use crate::settings::{self, TraySettings};
use crate::supervisor::{self, Attempt, Clock, Spawner, Supervisor};

/// The only event the runtime publishes.  Events carry complete snapshots so a
/// late-opening popover never has to reconstruct state from deltas.
pub const VIEW_STATE_CHANGED: &str = "view-state-changed";

/// What the popover sees when the server answers 409 to a refresh.
const BUSY_REFRESH: &str = "a refresh is already in progress";
/// What it sees when any other job (clean, import) is refused for the same reason.
const BUSY_JOB: &str = "a refresh or clean is already running — try again when it finishes";

/// One year, the same ceiling the server enforces on a history window. Checked
/// here too so an absurd value never leaves the process.
const MAX_HISTORY_WEEKS: u32 = 52;

/// What the `clean_database` popover command may wipe (issue #312).
///
/// The tray holds no clean logic: the scope only chooses the body of
/// `POST /api/kyber/clean`. The popover asked twice, so the request is always
/// confirmed. Multi-harness selection stays web-only.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CleanScope {
    /// Wipe every harness.
    All,
    /// Wipe the currently selected harness.
    Harness(String),
}

/// A harness id the server could plausibly know. Rejecting anything else here
/// keeps webview input out of the request entirely.
fn validated_harness(name: &str) -> Result<String> {
    let trimmed = name.trim();
    if trimmed.is_empty()
        || trimmed.len() > 128
        || trimmed.contains(|c: char| !(c.is_ascii_alphanumeric() || c == '-' || c == '_'))
    {
        return Err(anyhow!("unknown harness scope"));
    }
    Ok(trimmed.to_string())
}

fn validated_weeks(weeks: u32) -> Result<u32> {
    if (1..=MAX_HISTORY_WEEKS).contains(&weeks) {
        Ok(weeks)
    } else {
        Err(anyhow!(
            "history window must be between 1 and {MAX_HISTORY_WEEKS} weeks"
        ))
    }
}

impl CleanScope {
    /// The validated harness scope, or `All`.
    pub fn harness(name: impl Into<String>) -> Result<Self> {
        Ok(CleanScope::Harness(validated_harness(&name.into())?))
    }

    /// Parse the popover's `{ scope }` payload: `"all"`, or
    /// `{ "harness": "<id>" }` for the currently selected harness.
    /// Anything else is rejected before any request is made.
    pub fn from_ipc(value: &serde_json::Value) -> Result<Self> {
        match value {
            serde_json::Value::String(scope) if scope == "all" => Ok(CleanScope::All),
            serde_json::Value::Object(map) => match map.get("harness") {
                Some(serde_json::Value::String(name)) => CleanScope::harness(name.clone()),
                _ => Err(anyhow!(
                    "clean scope must be \"all\" or {{\"harness\": \"<id>\"}}"
                )),
            },
            _ => Err(anyhow!(
                "clean scope must be \"all\" or {{\"harness\": \"<id>\"}}"
            )),
        }
    }

    /// The request body. `scope` is the tray's own statement of intent; `all`,
    /// `harnesses` and `confirm` are the fields the server's clean route
    /// validates, so the same body satisfies both. `reingestWeeks` is present
    /// only when the operator asked to re-import, because its absence is what
    /// lets the server apply its own default.
    fn request_body(&self, import_weeks: Option<u32>) -> Value {
        let mut body = match self {
            CleanScope::All => json!({ "scope": "all", "all": true }),
            CleanScope::Harness(harness) => {
                json!({ "scope": { "harness": harness }, "harnesses": [harness] })
            }
        };
        body["confirm"] = json!(true);
        if let Some(weeks) = import_weeks {
            body["reingestWeeks"] = json!(weeks);
        }
        body
    }
}

/// Effects the production event bridge performs after a state transition.
pub trait EventSink: Send {
    fn emit(&mut self, name: &str, state: &ViewState) -> Result<()>;
}

/// Opens a URL only after [`ipc::open_view_url`] has checked it.
pub trait Opener: Send {
    fn open(&mut self, url: &str) -> Result<()>;
}

/// Stable inputs to a runtime instance.
///
/// `cli_program` is optional so the setup state is an ordinary snapshot rather
/// than a failed application launch.  The tests construct this directly;
/// production uses [`Runtime::from_resolution`] to retain the probed paths.
pub struct RuntimeConfig {
    pub cli_program: Option<String>,
    /// Holds the tray's `settings.json`. Unless [`Runtime::from_resolution`]
    /// names a different directory, it also holds the server's `server.json`.
    pub settings_dir: PathBuf,
    pub now: SystemTime,
}

/// All effects the runtime needs.  They are trait objects to keep the system
/// boundary out of the policy tests and to make the ownership of children
/// explicit.
pub struct RuntimeDependencies {
    pub server_spawner: Box<dyn Spawner + Send>,
    pub fetcher: Box<dyn LoopbackClient + Send>,
    pub clock: Box<dyn Clock + Send>,
    pub event_sink: Box<dyn EventSink + Send>,
    pub opener: Box<dyn Opener + Send>,
}

/// The managed service behind the popover commands.
pub struct Runtime {
    cli_program: Option<String>,
    probed: Vec<String>,
    setup: Option<SetupState>,
    settings_dir: PathBuf,
    /// Where `server.json` lives; the server's state, not the tray's.
    server_dir: PathBuf,
    settings: TraySettings,
    supervisor: Option<Supervisor>,
    cache: ReportCacheState,
    dependencies: RuntimeDependencies,
    started: bool,
    quitting: bool,
}

/// The report cache plus the two server documents polled with it. They share a
/// lifetime: every poll replaces all three, and none is ever defaulted.
#[derive(Default)]
struct ReportCacheState {
    report: api::ReportCache,
    jobs: Option<Value>,
    shared_settings: Option<Value>,
    /// Shared coverage (`GET /api/kyber/coverage`), carried verbatim like
    /// `jobs`: the tray renders only its `receiver` key for the saber.
    coverage: Option<Value>,
    /// The KyberDash version document (`GET /api/kyber/version`), carried
    /// verbatim; `None` when the latest read failed.
    kyberdash_version: Option<Value>,
}

impl Runtime {
    /// The complete command allowlist.  Keep this alongside the capability
    /// declaration so review can compare both sides of the IPC boundary.
    pub const fn authorized_commands() -> [&'static str; 9] {
        [
            "get_view_state",
            "refresh_now",
            "open_view",
            "set_settings",
            "quit",
            "hide_popover",
            "clean_database",
            "import_folder_history",
            "set_shared_settings",
        ]
    }

    pub fn is_authorized(command: &str) -> bool {
        Self::authorized_commands().contains(&command)
    }

    pub fn new(config: RuntimeConfig, dependencies: RuntimeDependencies) -> Self {
        let server_dir = config.settings_dir.clone();
        Self::new_inner(config, server_dir, dependencies, Vec::new(), None)
    }

    /// Builds a production runtime from the hardened CLI resolution result.
    /// `server_dir` is where the web server records itself (`server.json`).
    pub fn from_resolution(
        settings_dir: PathBuf,
        server_dir: PathBuf,
        resolution: Resolution,
        dependencies: RuntimeDependencies,
    ) -> Self {
        let cli_program = resolution.cli.as_ref().map(|cli| cli.program().to_string());
        let setup = cli::setup_state_for(&resolution, None);
        Self::new_inner(
            RuntimeConfig {
                cli_program,
                settings_dir,
                now: SystemTime::now(),
            },
            server_dir,
            dependencies,
            resolution.probed,
            setup,
        )
    }

    fn new_inner(
        config: RuntimeConfig,
        server_dir: PathBuf,
        dependencies: RuntimeDependencies,
        probed: Vec<String>,
        setup: Option<SetupState>,
    ) -> Self {
        let settings = settings::load(&config.settings_dir);
        let cli_program = config.cli_program;

        Runtime {
            supervisor: cli_program
                .as_ref()
                .map(|program| Supervisor::new(program.clone())),
            cli_program,
            probed,
            setup,
            settings_dir: config.settings_dir,
            server_dir,
            settings,
            cache: ReportCacheState::default(),
            dependencies,
            started: false,
            quitting: false,
        }
    }

    /// Attaches to or launches the one server, then reads the first report.
    ///
    /// This does no work for a missing CLI.  The caller still gets a fully
    /// populated `setup` snapshot rather than an invoke rejection.
    pub fn start(&mut self) -> Result<()> {
        if self.started || self.quitting {
            return Ok(());
        }
        self.started = true;

        if self.cli_program.is_none() {
            self.ensure_missing_cli_setup();
            return self.publish();
        }

        self.ensure_server()?;
        if self.setup.is_none() {
            self.poll_report_inner(SystemTime::now());
        }
        self.publish()
    }

    /// Keeps the server connection alive.  Tauri calls this from a blocking
    /// worker; the method itself never assumes it owns an async runtime.
    ///
    /// There is deliberately no work to schedule here: refreshing is the
    /// server's job, so the clock reading has no use and is accepted only so
    /// the maintenance loop's call shape stays stable.
    pub fn tick(&mut self, _now: SystemTime) -> Result<()> {
        if !self.started || self.setup.is_some() || self.quitting {
            return Ok(());
        }
        self.ensure_server()?;
        if self.setup.is_some() {
            return self.publish();
        }
        Ok(())
    }

    pub fn get_view_state(&self) -> ViewState {
        let server = self
            .supervisor
            .as_ref()
            .map(Supervisor::phase)
            .unwrap_or(supervisor::ServerPhase::Starting);
        ipc::view_state(
            self.setup.clone(),
            server,
            &self.cache.report,
            ServerDocs {
                jobs: self.cache.jobs.clone(),
                shared_settings: self.cache.shared_settings.clone(),
                coverage: self.cache.coverage.clone(),
                kyberdash_version: self.cache.kyberdash_version.clone(),
            },
            &self.settings,
        )
    }

    /// Fetches the report once.  Fetch failures are represented in the
    /// snapshot, not returned as an IPC error, because stale data is usable
    /// data when clearly labelled.
    pub fn poll_report(&mut self, now: SystemTime) -> Result<()> {
        if self.quitting {
            return Ok(());
        }
        self.poll_report_inner(now);
        self.publish()
    }

    /// Asks the server to refresh, as the tray surface.
    pub fn refresh_now(&mut self, now: SystemTime) -> Result<()> {
        self.ensure_running()?;
        self.request(
            Method::Post,
            REFRESH_PATH,
            &json!({ "surface": "tray" }),
            BUSY_REFRESH,
            "refresh request",
        )?;
        self.after_action(now)
    }

    pub fn open_view(&mut self, view: &str) -> Result<()> {
        self.ensure_running()?;
        let server_url = self
            .supervisor
            .as_ref()
            .and_then(|supervisor| supervisor.url())
            .ok_or_else(|| anyhow!("the KyberDash server is not ready"))?;
        let url = ipc::open_view_url(server_url, view)?;
        self.dependencies.opener.open(&url)
    }

    /// Validates, persists, and applies a partial settings document.  These are
    /// display preferences only: nothing here is sent to the server.
    pub fn set_settings(&mut self, patch: Value) -> Result<()> {
        self.ensure_running()?;
        if !patch.is_object() {
            return Err(anyhow!("settings patch must be an object"));
        }
        let previous_harness = self.settings.harness.clone();
        let previous_window_days = self.settings.window_days;
        self.settings.apply_partial(&patch);
        settings::save(&self.settings_dir, &self.settings)?;
        if self.settings.harness != previous_harness
            || self.settings.window_days != previous_window_days
        {
            // Scope controls take effect immediately; waiting for the next
            // poll would leave the selector and report describing different
            // datasets for a visible interval.
            self.poll_report_inner(SystemTime::now());
        }
        self.publish()
    }

    pub fn settings(&self) -> TraySettings {
        self.settings.clone()
    }

    /// Patches the server-owned settings (`PUT /api/kyber/settings`).  The tray
    /// never stores them: the server validates and persists, and the next poll
    /// reads the result back.
    pub fn set_shared_settings(&mut self, patch: Value) -> Result<()> {
        self.ensure_running()?;
        if !patch.is_object() {
            return Err(anyhow!("shared settings patch must be an object"));
        }
        self.request(
            Method::Put,
            SETTINGS_PATH,
            &patch,
            BUSY_JOB,
            "settings update",
        )?;
        self.after_action(SystemTime::now())
    }

    /// Stops the server only if this runtime launched it.  `Supervisor` also
    /// does this on drop, but command and application-exit paths call it
    /// explicitly so shutdown does not depend on process teardown ordering.
    pub fn quit(&mut self) {
        if self.quitting {
            return;
        }
        self.quitting = true;
        if let Some(supervisor) = self.supervisor.as_mut() {
            supervisor.stop();
        }
    }

    /// Asks the server to wipe a scope, optionally re-importing `import_weeks`
    /// of history afterwards.
    ///
    /// A clean is a foreground, user-confirmed action.  The server does the
    /// pausing, wiping and re-importing itself; the tray only chooses the scope
    /// and relays the answer.  409 (busy) and other failures surface as the
    /// command error the popover already renders.
    pub fn clean_database(&mut self, scope: CleanScope, import_weeks: Option<u32>) -> Result<()> {
        self.ensure_running()?;
        let import_weeks = import_weeks.map(validated_weeks).transpose()?;
        self.request(
            Method::Post,
            CLEAN_PATH,
            &scope.request_body(import_weeks),
            BUSY_JOB,
            "database clean",
        )?;
        self.after_action(SystemTime::now())
    }

    /// Asks the server to import `weeks` of folder history, for one harness or
    /// all of them.
    pub fn import_folder_history(&mut self, weeks: u32, harness: Option<&str>) -> Result<()> {
        self.ensure_running()?;
        let weeks = validated_weeks(weeks)?;
        let mut body = json!({ "weeks": weeks });
        if let Some(harness) = harness {
            body["harness"] = json!(validated_harness(harness)?);
        }
        self.request(
            Method::Post,
            IMPORT_HISTORY_PATH,
            &body,
            BUSY_JOB,
            "history import",
        )?;
        self.after_action(SystemTime::now())
    }

    pub fn clear_cli_for_test(&mut self) {
        self.cli_program = None;
        self.supervisor = None;
        self.setup = None;
    }

    fn ensure_running(&self) -> Result<()> {
        if self.quitting {
            bail!("the tray runtime is shutting down");
        }
        Ok(())
    }

    fn ensure_missing_cli_setup(&mut self) {
        if self.setup.is_none() {
            self.setup = Some(SetupState::new(SetupReason::NotFound, self.probed.clone()));
        }
        self.cache = ReportCacheState::default();
    }

    /// Reuses a healthy server, attaches to one another process runs, and only
    /// launches as a last resort.
    fn ensure_server(&mut self) -> Result<()> {
        let Some(supervisor) = self.supervisor.as_mut() else {
            self.ensure_missing_cli_setup();
            return Ok(());
        };
        if supervisor.url().is_some() && supervisor.is_running() {
            return Ok(());
        }
        if let Some(listening) = supervisor::discover(&self.server_dir) {
            supervisor.attach(listening);
            if self.server_url().is_ok() && !self.server_serves_tray_routes() {
                self.enter_too_old_setup();
            }
            return Ok(());
        }
        let attempt = supervisor.attempt(
            &mut *self.dependencies.server_spawner,
            &mut *self.dependencies.clock,
        );
        match attempt {
            Attempt::Listening(_) => {
                // An API older than this tray understands is setup, not data —
                // and so is an API new enough on paper but missing the routes.
                let too_old = supervisor
                    .api_version()
                    .is_some_and(|version| version < cli::MIN_API_VERSION);
                if too_old || !self.server_serves_tray_routes() {
                    self.enter_too_old_setup();
                }
            }
            Attempt::Failed { reason, .. } => self.cache.report.record_error(reason),
        }
        Ok(())
    }

    /// A server the tray cannot drive is setup, exactly as an API version below
    /// [`cli::MIN_API_VERSION`] is: nothing is presented as working.
    fn enter_too_old_setup(&mut self) {
        self.setup = Some(SetupState::new(SetupReason::TooOld, self.probed.clone()));
        if let Some(supervisor) = self.supervisor.as_mut() {
            // A launched server is stopped; an attached one is only let go of,
            // because the user may be using it through the browser.
            supervisor.stop();
        }
        self.cache = ReportCacheState::default();
    }

    /// Whether the server serves the routes the tray needs, probed once per
    /// server by asking for the read-only jobs document.
    ///
    /// The `apiVersion` gate is not enough on its own: it has not moved, so a
    /// server that predates `/api/kyber/jobs`, `/settings`, `/refresh` and
    /// `/import-history` passes it, and then every action 404s while jobs and
    /// settings stay silently null. A 404 is that answer. Anything else — a
    /// 500, a refused connection, a server still warming up — is inconclusive
    /// and leaves the tray running rather than declaring a good server too old.
    fn server_serves_tray_routes(&mut self) -> bool {
        let Ok(server_url) = self.server_url() else {
            return true;
        };
        let Ok(url) = api::endpoint_url(&server_url, JOBS_PATH) else {
            return true;
        };
        match self.dependencies.fetcher.get_with_status(&url) {
            Ok(response) => !response.is_not_found(),
            // Unreachable is not "too old": the popover shows a stale banner.
            Err(_) => true,
        }
    }

    fn server_url(&self) -> Result<String> {
        self.supervisor
            .as_ref()
            .and_then(|supervisor| supervisor.url())
            .map(str::to_string)
            .ok_or_else(|| anyhow!("the KyberDash server is not ready"))
    }

    /// Sends one action to the server and maps its answer.  `busy` is the
    /// message for 409, the server's "a job is already running".
    fn request(
        &mut self,
        method: Method,
        path: &str,
        body: &Value,
        busy: &str,
        what: &str,
    ) -> Result<()> {
        let url = api::endpoint_url(&self.server_url()?, path)?;
        let response = self
            .dependencies
            .fetcher
            .send(method, &url, body)
            .map_err(|error| anyhow!("could not reach the KyberDash server: {error}"))?;
        if response.is_success() {
            return Ok(());
        }
        if response.status == 409 {
            bail!("{busy}");
        }
        match response.error_message() {
            Some(message) => bail!("{what} failed: {message}"),
            None => bail!("{what} failed: server returned {}", response.status),
        }
    }

    /// An action changes what the server would report, so read it back now
    /// rather than showing the pre-action state until the next poll.
    fn after_action(&mut self, now: SystemTime) -> Result<()> {
        self.poll_report_inner(now);
        self.publish()
    }

    fn poll_report_inner(&mut self, now: SystemTime) {
        let Ok(server_url) = self.server_url() else {
            return;
        };
        let report_url = match api::report_url_for_scope(
            &server_url,
            &self.settings.harness,
            self.settings.window_days,
        ) {
            Ok(url) => url,
            Err(error) => {
                self.cache.report.record_error(error.to_string());
                return;
            }
        };
        // A hung secondary read must not hold one poll past its 15 s interval,
        // and a read that never ran must not leave old data shown as current.
        // So the whole poll spends one budget: each read gets the lesser of
        // its own cap and what is left, and once nothing is left the rest are
        // skipped and cleared (unknown, never stale).
        let started = self.dependencies.clock.elapsed();
        let budget = api::POLL_WHILE_OPEN;
        match self
            .dependencies
            .fetcher
            .get_with_timeout(&report_url, api::REQUEST_TIMEOUT.min(budget))
        {
            Ok(report) => self.cache.report.record_success(report, now),
            Err(error) => {
                self.cache.report.record_error(error.to_string());
                // A server that cannot answer the report will not answer the
                // rest, and the poll must stay inside its 15 s interval.
                // Unknown stays unknown: nothing old is left on screen.
                self.clear_secondary_documents();
                return;
            }
        }
        // Unknown stays unknown: a failed or skipped fetch clears the value
        // instead of leaving old (or inventing default) state on screen.
        // Jobs, settings, coverage (the saber's red-not-stale-green) and the
        // version are read in order under the shared budget.
        self.clear_secondary_documents();
        let reads = [
            (JOBS_PATH, api::REQUEST_TIMEOUT),
            (SETTINGS_PATH, api::REQUEST_TIMEOUT),
            (COVERAGE_PATH, api::REQUEST_TIMEOUT),
            (VERSION_PATH, api::VERSION_REQUEST_TIMEOUT),
        ];
        for (path, cap) in reads {
            let spent = self.dependencies.clock.elapsed().saturating_sub(started);
            let remaining = budget.saturating_sub(spent);
            if remaining.is_zero() {
                break;
            }
            let document = self.fetch_document(&server_url, path, cap.min(remaining));
            match path {
                JOBS_PATH => self.cache.jobs = document,
                SETTINGS_PATH => self.cache.shared_settings = document,
                COVERAGE_PATH => self.cache.coverage = document,
                _ => self.cache.kyberdash_version = document,
            }
        }
    }

    fn clear_secondary_documents(&mut self) {
        self.cache.jobs = None;
        self.cache.shared_settings = None;
        self.cache.coverage = None;
        self.cache.kyberdash_version = None;
    }

    fn fetch_document(&mut self, server_url: &str, path: &str, timeout: Duration) -> Option<Value> {
        let url = api::endpoint_url(server_url, path).ok()?;
        self.dependencies
            .fetcher
            .get_with_timeout(&url, timeout)
            .ok()
    }

    fn publish(&mut self) -> Result<()> {
        let snapshot = self.get_view_state();
        self.dependencies
            .event_sink
            .emit(VIEW_STATE_CHANGED, &snapshot)
    }
}

impl Drop for Runtime {
    fn drop(&mut self) {
        self.quit();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api::ApiResponse;
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    const SERVER: &str = "http://127.0.0.1:4747";
    const VERSION_ROUTE: &str = "/api/kyber/version";

    /// Routes answered by path. A poll reads five documents, so an ordered
    /// queue like the api tests' `FakeFetcher` would tie these tests to the
    /// order the runtime fetches them in.
    type Routes = Arc<Mutex<HashMap<String, Value>>>;
    type Fetched = Arc<Mutex<Vec<String>>>;
    /// Each timed read, with the deadline the runtime asked the client for.
    type Timeouts = Arc<Mutex<Vec<(String, Duration)>>>;
    /// Monotonic fake time, shared by the fake clock and the fake fetcher. A
    /// read that takes time advances it, so the poll's budget is spent by the
    /// reads themselves, as it would be on a real socket.
    type SharedTime = Arc<Mutex<Duration>>;
    /// What a read costs in fake time, keyed by route path. A missing route
    /// answers at once.
    type SharedCosts = Arc<Mutex<HashMap<String, Cost>>>;

    #[derive(Clone, Copy)]
    enum Cost {
        /// Answers after this long. When that exceeds the read's deadline, the
        /// read times out instead, having run to the deadline.
        Takes(Duration),
        /// Never answers: runs to its deadline, then fails.
        Hangs,
    }

    struct RoutedFetcher {
        routes: Routes,
        fetched: Fetched,
        timeouts: Timeouts,
        time: SharedTime,
        costs: SharedCosts,
    }

    impl LoopbackClient for RoutedFetcher {
        fn send(&mut self, _method: Method, _url: &str, _body: &Value) -> Result<ApiResponse> {
            bail!("the poll tests never send actions")
        }

        fn get(&mut self, url: &str) -> Result<Value> {
            self.fetched
                .lock()
                .expect("fetched lock")
                .push(url.to_string());
            let path = match url.find("/api/kyber/") {
                Some(start) => url[start..].split('?').next().unwrap_or_default(),
                None => bail!("connection refused"),
            };
            match self.routes.lock().expect("routes lock").get(path) {
                Some(document) => Ok(document.clone()),
                None => bail!("connection refused"),
            }
        }

        fn get_with_timeout(&mut self, url: &str, timeout: Duration) -> Result<Value> {
            self.timeouts
                .lock()
                .expect("timeouts lock")
                .push((url.to_string(), timeout));
            let path = url
                .find("/api/kyber/")
                .map(|start| {
                    url[start..]
                        .split('?')
                        .next()
                        .unwrap_or_default()
                        .to_string()
                })
                .unwrap_or_default();
            let cost = self
                .costs
                .lock()
                .expect("costs lock")
                .get(&path)
                .copied()
                .unwrap_or(Cost::Takes(Duration::ZERO));
            let answers = matches!(cost, Cost::Takes(took) if took <= timeout);
            let spent = match cost {
                Cost::Takes(took) if answers => took,
                _ => timeout,
            };
            *self.time.lock().expect("time lock") += spent;
            if answers {
                return self.get(url);
            }
            self.fetched
                .lock()
                .expect("fetched lock")
                .push(url.to_string());
            bail!("the read timed out after {timeout:?}")
        }
    }

    struct NoLaunch;

    impl Spawner for NoLaunch {
        fn spawn(
            &mut self,
            _program: &str,
            _args: &[&str],
        ) -> std::io::Result<Box<dyn supervisor::ServerProcess>> {
            Err(std::io::Error::other(
                "the poll tests never launch a server",
            ))
        }
    }

    /// Reads the shared fake time, so a budget spent by the fetcher is visible
    /// to the poll. Sleeping is free.
    struct ManualClock {
        time: SharedTime,
    }

    impl Clock for ManualClock {
        fn sleep(&mut self, _duration: Duration) {}

        fn elapsed(&self) -> Duration {
            *self.time.lock().expect("time lock")
        }
    }

    struct SilentEvents;

    impl EventSink for SilentEvents {
        fn emit(&mut self, _name: &str, _state: &ViewState) -> Result<()> {
            Ok(())
        }
    }

    struct SilentOpener;

    impl Opener for SilentOpener {
        fn open(&mut self, _url: &str) -> Result<()> {
            Ok(())
        }
    }

    fn at(secs: u64) -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(secs)
    }

    /// Every document a poll reads, with the version document the server
    /// reports for this build.
    fn server_routes() -> Routes {
        let routes = HashMap::from([
            ("/api/kyber/report".to_string(), json!({"schemaVersion": 1})),
            (
                "/api/kyber/jobs".to_string(),
                json!({"refresh": {"state": "idle"}}),
            ),
            (
                "/api/kyber/settings".to_string(),
                json!({"refreshMinutes": 15}),
            ),
            (
                "/api/kyber/coverage".to_string(),
                json!({"receiver": {"healthy": true}}),
            ),
            (
                VERSION_ROUTE.to_string(),
                json!({"version": "9.8.7", "apiVersion": 1}),
            ),
        ]);
        Arc::new(Mutex::new(routes))
    }

    /// Hands out a distinct number to each settings directory in this process.
    static NEXT_SETTINGS_DIR: AtomicUsize = AtomicUsize::new(0);

    /// A settings directory that no other test in this process shares. Tests
    /// run in parallel inside one process, so the process id alone names the
    /// same directory for all of them; the counter separates them, and the
    /// process id separates concurrent test runs.
    fn unique_settings_dir() -> PathBuf {
        let n = NEXT_SETTINGS_DIR.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir().join(format!("kyberdash-version-poll-{}-{n}", std::process::id()))
    }

    /// A runtime whose supervisor is already attached to the fake server, so a
    /// poll has a server URL to read from without launching anything.
    fn attached_runtime(routes: Routes, fetched: Fetched) -> Runtime {
        attached_runtime_recording(routes, fetched, Timeouts::default())
    }

    /// As [`attached_runtime`], also recording the deadline of each timed read.
    fn attached_runtime_recording(routes: Routes, fetched: Fetched, timeouts: Timeouts) -> Runtime {
        attached_runtime_timed(
            routes,
            fetched,
            timeouts,
            SharedTime::default(),
            SharedCosts::default(),
        )
    }

    /// As [`attached_runtime_recording`], with fake time: the poll's clock and
    /// the fetcher share `time`, and each route's reads cost what `costs` says.
    fn attached_runtime_timed(
        routes: Routes,
        fetched: Fetched,
        timeouts: Timeouts,
        time: SharedTime,
        costs: SharedCosts,
    ) -> Runtime {
        let settings_dir = unique_settings_dir();
        let mut runtime = Runtime::new(
            RuntimeConfig {
                cli_program: Some("kyberdash".to_string()),
                settings_dir,
                now: at(1_700_000_000),
            },
            RuntimeDependencies {
                server_spawner: Box::new(NoLaunch),
                fetcher: Box::new(RoutedFetcher {
                    routes,
                    fetched,
                    timeouts,
                    time: time.clone(),
                    costs,
                }),
                clock: Box::new(ManualClock { time }),
                event_sink: Box::new(SilentEvents),
                opener: Box::new(SilentOpener),
            },
        );
        if let Some(attached) = runtime.supervisor.as_mut() {
            attached.attach(supervisor::Listening {
                url: SERVER.to_string(),
                pid: std::process::id(),
                version: "9.8.7".to_string(),
                api_version: 1,
            });
        }
        runtime
    }

    fn snapshot(runtime: &Runtime) -> Value {
        serde_json::to_value(runtime.get_view_state()).expect("serialize view state")
    }

    fn secs(n: u64) -> Duration {
        Duration::from_secs(n)
    }

    /// Sets what the named routes cost, leaving the other routes as they were.
    fn with_costs(costs: &SharedCosts, entries: &[(&str, Cost)]) {
        let mut map = costs.lock().expect("costs lock");
        for (path, cost) in entries {
            map.insert(path.to_string(), *cost);
        }
    }

    /// A request URL reduced to its route path: no origin, no query.
    fn route_path(url: &str) -> String {
        let rest = url.strip_prefix(SERVER).unwrap_or(url);
        rest.split('?').next().unwrap_or_default().to_string()
    }

    /// The route path of each request, in the order the poll issued them.
    fn requested_paths(fetched: &Fetched) -> Vec<String> {
        fetched
            .lock()
            .expect("fetched lock")
            .iter()
            .map(|url| route_path(url))
            .collect()
    }

    /// Each timed read as (route path, deadline), in the order the poll issued them.
    fn timed_reads(timeouts: &Timeouts) -> Vec<(String, Duration)> {
        timeouts
            .lock()
            .expect("timeouts lock")
            .iter()
            .map(|(url, deadline)| (route_path(url), *deadline))
            .collect()
    }

    fn timed(path: &str, deadline: Duration) -> (String, Duration) {
        (path.to_string(), deadline)
    }

    /// The popover shows the KyberDash version the server reports. A poll must
    /// fetch it from the version route and carry the document through unchanged,
    /// as it does the jobs, settings and coverage documents.
    #[test]
    fn a_poll_carries_the_server_version_document_unchanged() {
        let fetched: Fetched = Arc::default();
        let mut runtime = attached_runtime(server_routes(), fetched.clone());

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        assert_eq!(
            snapshot(&runtime)["kyberdashVersion"],
            json!({"version": "9.8.7", "apiVersion": 1})
        );
        let version_url = format!("{SERVER}{VERSION_ROUTE}");
        assert!(
            fetched.lock().expect("fetched lock").contains(&version_url),
            "the poll must read {version_url}"
        );
    }

    /// A failed version read must not keep the version an earlier poll read.
    /// Unknown stays unknown, as with jobs, settings and coverage; the popover
    /// must not show a version the latest poll could not confirm.
    #[test]
    fn a_failed_version_read_clears_the_version_from_an_earlier_poll() {
        let routes = server_routes();
        let mut runtime = attached_runtime(routes.clone(), Arc::default());
        runtime.poll_report(at(1_700_000_000)).expect("first poll");
        assert_eq!(
            snapshot(&runtime)["kyberdashVersion"],
            json!({"version": "9.8.7", "apiVersion": 1}),
            "precondition: the first poll reads the version"
        );

        routes.lock().expect("routes lock").remove(VERSION_ROUTE);
        runtime.poll_report(at(1_700_000_300)).expect("second poll");

        assert!(
            snapshot(&runtime)["kyberdashVersion"].is_null(),
            "a failed version read must clear the earlier version"
        );
    }

    /// The version route is store-free and answers fast, so its read has a
    /// short deadline of its own. Sharing the 10 s request timeout would let a
    /// hung version read hold the poll past its 15 s interval.
    #[test]
    fn the_version_read_uses_its_own_short_timeout() {
        let fetched: Fetched = Arc::default();
        let timeouts: Timeouts = Arc::default();
        let mut runtime = attached_runtime_recording(server_routes(), fetched, timeouts.clone());

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        let version_url = format!("{SERVER}{}", api::VERSION_PATH);
        let version_deadlines: Vec<Duration> = timeouts
            .lock()
            .expect("timeouts lock")
            .iter()
            .filter(|(url, _)| *url == version_url)
            .map(|(_, deadline)| *deadline)
            .collect();
        assert_eq!(
            version_deadlines,
            vec![api::VERSION_REQUEST_TIMEOUT],
            "the poll must read {version_url} with VERSION_REQUEST_TIMEOUT"
        );
        assert_ne!(
            api::VERSION_REQUEST_TIMEOUT,
            api::REQUEST_TIMEOUT,
            "the version read must not share the general request deadline"
        );
    }

    /// When the report read fails the server is unreachable, so the poll must
    /// not wait out a further timeout on each secondary document. Those
    /// documents are not attempted, and their cached values read as unknown.
    #[test]
    fn a_failed_report_read_skips_the_secondary_documents_and_clears_them() {
        let routes = server_routes();
        let fetched: Fetched = Arc::default();
        let mut runtime = attached_runtime(routes.clone(), fetched.clone());
        runtime.poll_report(at(1_700_000_000)).expect("first poll");
        let before = snapshot(&runtime);
        assert!(
            !before["jobs"].is_null() && !before["coverage"].is_null(),
            "precondition: the first poll reads the secondary documents"
        );

        routes.lock().expect("routes lock").remove(api::REPORT_PATH);
        fetched.lock().expect("fetched lock").clear();
        runtime.poll_report(at(1_700_000_300)).expect("second poll");

        let requested = fetched.lock().expect("fetched lock").clone();
        assert_eq!(
            requested.len(),
            1,
            "a failed report read must be the only request in that poll, got {requested:?}"
        );
        assert!(
            requested[0].starts_with(&format!("{SERVER}{}", api::REPORT_PATH)),
            "the one request must be the report, got {requested:?}"
        );

        let after = snapshot(&runtime);
        assert!(
            after["error"].is_string(),
            "the report failure must be shown"
        );
        for key in ["jobs", "sharedSettings", "coverage", "kyberdashVersion"] {
            assert!(
                after[key].is_null(),
                "{key} must read as unknown after a failed report read, got {}",
                after[key]
            );
        }
    }

    /// A healthy poll reads each of the five documents exactly once. This pins
    /// the success path, which the bail-out above must leave untouched.
    #[test]
    fn a_healthy_poll_fetches_each_of_the_five_documents_once() {
        let fetched: Fetched = Arc::default();
        let mut runtime = attached_runtime(server_routes(), fetched.clone());

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        let mut paths: Vec<String> = fetched
            .lock()
            .expect("fetched lock")
            .iter()
            .map(|url| {
                let rest = url.strip_prefix(SERVER).unwrap_or(url);
                rest.split('?').next().unwrap_or_default().to_string()
            })
            .collect();
        paths.sort();
        let mut expected = vec![
            api::REPORT_PATH,
            api::JOBS_PATH,
            api::SETTINGS_PATH,
            api::COVERAGE_PATH,
            api::VERSION_PATH,
        ];
        expected.sort_unstable();
        assert_eq!(paths, expected);
    }

    /// The poll gets one budget, the length of its 15 s interval, so a slow
    /// secondary read cannot leave stale data shown as current. Here the report
    /// and jobs reads spend all of it, so settings, coverage and the version are
    /// not attempted, and their values read as unknown rather than as what an
    /// earlier poll read.
    #[test]
    fn a_poll_that_spends_its_budget_skips_the_remaining_reads_and_clears_them() {
        assert_eq!(
            api::POLL_WHILE_OPEN,
            secs(15),
            "the scenario below assumes a 15 s budget"
        );
        let fetched: Fetched = Arc::default();
        let costs: SharedCosts = Arc::default();
        let mut runtime = attached_runtime_timed(
            server_routes(),
            fetched.clone(),
            Timeouts::default(),
            SharedTime::default(),
            costs.clone(),
        );
        runtime.poll_report(at(1_700_000_000)).expect("first poll");
        assert!(
            !snapshot(&runtime)["sharedSettings"].is_null(),
            "precondition: the first poll reads settings"
        );

        fetched.lock().expect("fetched lock").clear();
        with_costs(
            &costs,
            &[
                (api::REPORT_PATH, Cost::Takes(secs(5))),
                (api::JOBS_PATH, Cost::Takes(secs(10))),
            ],
        );
        runtime.poll_report(at(1_700_000_300)).expect("second poll");

        assert_eq!(
            requested_paths(&fetched),
            vec![api::REPORT_PATH.to_string(), api::JOBS_PATH.to_string()],
            "with the budget spent, no secondary read may be issued"
        );
        let after = snapshot(&runtime);
        assert_eq!(
            after["jobs"],
            json!({"refresh": {"state": "idle"}}),
            "the jobs read answered in time, so it is current"
        );
        assert!(after["error"].is_null(), "the report answered");
        for key in ["sharedSettings", "coverage", "kyberdashVersion"] {
            assert!(
                after[key].is_null(),
                "{key} must read as unknown once the budget is spent, got {}",
                after[key]
            );
        }
    }

    /// Each timed read gets the smaller of its own cap and the budget left when
    /// it is issued. Report and jobs take 1 s each. Settings hangs and is cut
    /// off at its 10 s cap, leaving 3 s for coverage, which also hangs. The
    /// version read then has no budget left and is not attempted.
    #[test]
    fn each_timed_read_gets_the_lesser_of_its_cap_and_the_budget_left() {
        let fetched: Fetched = Arc::default();
        let timeouts: Timeouts = Arc::default();
        let costs: SharedCosts = Arc::default();
        let mut runtime = attached_runtime_timed(
            server_routes(),
            fetched.clone(),
            timeouts.clone(),
            SharedTime::default(),
            costs.clone(),
        );
        with_costs(
            &costs,
            &[
                (api::REPORT_PATH, Cost::Takes(secs(1))),
                (api::JOBS_PATH, Cost::Takes(secs(1))),
                (api::SETTINGS_PATH, Cost::Hangs),
                (api::COVERAGE_PATH, Cost::Hangs),
            ],
        );

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        assert_eq!(
            timed_reads(&timeouts),
            vec![
                timed(api::REPORT_PATH, secs(10)),
                timed(api::JOBS_PATH, secs(10)),
                timed(api::SETTINGS_PATH, secs(10)),
                timed(api::COVERAGE_PATH, secs(3)),
            ],
            "deadline = min(own cap, budget left at issue)"
        );
        assert_eq!(
            requested_paths(&fetched),
            vec![
                api::REPORT_PATH.to_string(),
                api::JOBS_PATH.to_string(),
                api::SETTINGS_PATH.to_string(),
                api::COVERAGE_PATH.to_string(),
            ],
            "the version read is not attempted once the budget is spent"
        );
        assert!(snapshot(&runtime)["kyberdashVersion"].is_null());
    }

    /// The version read has a 2 s cap, but when less than that is left in the
    /// budget it gets what is left. Coverage here answers after 8 s, leaving 1 s.
    #[test]
    fn the_version_read_gets_the_budget_left_when_that_is_less_than_its_cap() {
        let timeouts: Timeouts = Arc::default();
        let costs: SharedCosts = Arc::default();
        let mut runtime = attached_runtime_timed(
            server_routes(),
            Fetched::default(),
            timeouts.clone(),
            SharedTime::default(),
            costs.clone(),
        );
        with_costs(
            &costs,
            &[
                (api::REPORT_PATH, Cost::Takes(secs(6))),
                (api::COVERAGE_PATH, Cost::Takes(secs(8))),
            ],
        );

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        assert_eq!(
            timed_reads(&timeouts),
            vec![
                timed(api::REPORT_PATH, secs(10)),
                timed(api::JOBS_PATH, secs(9)),
                timed(api::SETTINGS_PATH, secs(9)),
                timed(api::COVERAGE_PATH, secs(9)),
                timed(api::VERSION_PATH, secs(1)),
            ]
        );
    }

    /// With no time spent the budget never binds: each of the five reads keeps
    /// its own cap, and they are issued in the order the poll reads them.
    #[test]
    fn a_healthy_fast_poll_reads_all_five_with_their_own_caps() {
        let timeouts: Timeouts = Arc::default();
        let mut runtime =
            attached_runtime_recording(server_routes(), Fetched::default(), timeouts.clone());

        runtime.poll_report(at(1_700_000_000)).expect("poll");

        assert_eq!(
            timed_reads(&timeouts),
            vec![
                timed(api::REPORT_PATH, api::REQUEST_TIMEOUT),
                timed(api::JOBS_PATH, api::REQUEST_TIMEOUT),
                timed(api::SETTINGS_PATH, api::REQUEST_TIMEOUT),
                timed(api::COVERAGE_PATH, api::REQUEST_TIMEOUT),
                timed(api::VERSION_PATH, api::VERSION_REQUEST_TIMEOUT),
            ]
        );
    }

    #[test]
    fn a_clean_body_omits_reingest_weeks_unless_asked() {
        let all = CleanScope::All.request_body(None);
        assert_eq!(all["scope"], "all");
        assert_eq!(all["confirm"], true);
        assert!(all.get("reingestWeeks").is_none());

        let harness = CleanScope::harness("cursor").unwrap().request_body(Some(4));
        assert_eq!(harness["scope"], json!({ "harness": "cursor" }));
        assert_eq!(harness["harnesses"], json!(["cursor"]));
        assert_eq!(harness["reingestWeeks"], 4);
    }

    #[test]
    fn history_weeks_are_bounded_to_one_year() {
        assert!(validated_weeks(0).is_err());
        assert!(validated_weeks(1).is_ok());
        assert!(validated_weeks(52).is_ok());
        assert!(validated_weeks(53).is_err());
    }

    #[test]
    fn webview_harness_ids_cannot_carry_path_or_markup() {
        for bad in ["", "   ", "../etc", "a b", "a/b", "<x>", &"x".repeat(129)] {
            assert!(validated_harness(bad).is_err(), "{bad:?} must be refused");
        }
        assert_eq!(validated_harness(" claude-code ").unwrap(), "claude-code");
    }
}
