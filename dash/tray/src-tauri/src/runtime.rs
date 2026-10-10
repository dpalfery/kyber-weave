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
use std::time::SystemTime;

use anyhow::{anyhow, bail, Result};
use serde_json::{json, Value};

use crate::api::{
    self, LoopbackClient, Method, CLEAN_PATH, IMPORT_HISTORY_PATH, JOBS_PATH, REFRESH_PATH,
    SETTINGS_PATH,
};
use crate::cli::{self, Resolution, SetupReason, SetupState};
use crate::ipc::{self, ViewState};
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
            self.cache.jobs.clone(),
            self.cache.shared_settings.clone(),
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
        match self.dependencies.fetcher.get(&report_url) {
            Ok(report) => self.cache.report.record_success(report, now),
            Err(error) => self.cache.report.record_error(error.to_string()),
        }
        // Unknown stays unknown: a failed fetch clears the value instead of
        // leaving old (or inventing default) job and settings state on screen.
        self.cache.jobs = self.fetch_document(&server_url, JOBS_PATH);
        self.cache.shared_settings = self.fetch_document(&server_url, SETTINGS_PATH);
    }

    fn fetch_document(&mut self, server_url: &str, path: &str) -> Option<Value> {
        let url = api::endpoint_url(server_url, path).ok()?;
        self.dependencies.fetcher.get(&url).ok()
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
