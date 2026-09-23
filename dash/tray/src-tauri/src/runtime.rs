//! Composition root for the tray's long-lived runtime.
//!
//! The helper modules own individual policies.  This module owns the one
//! instance of each policy, joining them into the state the popover reads.  It
//! remains synchronous on purpose: the real Tauri bridge runs it through
//! `spawn_blocking`, while tests can substitute every system effect without a
//! network connection or a child process.

use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver as MpscReceiver, RecvTimeoutError, TryRecvError};
use std::time::{Duration, SystemTime};

use anyhow::{anyhow, Result};
use serde_json::Value;

use crate::api::{self, ReportCache, ReportFetcher};
use crate::cli::{self, Resolution, SetupReason, SetupState};
use crate::ipc::{self, ViewState};
use crate::receiver::{HealthProbe, Probe, Receiver, ReceiverSpawner, ReceiverStatus};
use crate::scheduler::{RefreshCancellation, RefreshRunner, Scheduler};
use crate::settings::{self, TraySettings};
use crate::supervisor::{Attempt, Clock, Spawner, Supervisor};

/// The only event the runtime publishes.  Events carry complete snapshots so a
/// late-opening popover never has to reconstruct state from deltas.
pub const VIEW_STATE_CHANGED: &str = "view-state-changed";

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
    pub settings_dir: PathBuf,
    pub cadence: Duration,
    pub now: SystemTime,
}

/// All effects the runtime needs.  They are trait objects to keep the system
/// boundary out of the policy tests and to make the ownership of children
/// explicit.
pub struct RuntimeDependencies {
    pub server_spawner: Box<dyn Spawner + Send>,
    pub refresh_runner: Box<dyn RefreshRunner + Send>,
    pub fetcher: Box<dyn ReportFetcher + Send>,
    pub clock: Box<dyn Clock + Send>,
    pub receiver_probe: Box<dyn HealthProbe + Send>,
    pub receiver_spawner: Box<dyn ReceiverSpawner + Send>,
    pub event_sink: Box<dyn EventSink + Send>,
    pub opener: Box<dyn Opener + Send>,
}

/// The managed service behind the six popover commands.
pub struct Runtime {
    cli_program: Option<String>,
    probed: Vec<String>,
    setup: Option<SetupState>,
    settings_dir: PathBuf,
    settings: TraySettings,
    supervisor: Option<Supervisor>,
    scheduler: Option<Scheduler>,
    receiver: Option<Receiver>,
    cache: ReportCache,
    dependencies: RuntimeDependencies,
    started: bool,
    refresh_task: Option<PendingRefresh>,
    receiver_retry_at: Option<SystemTime>,
    quitting: bool,
    server_calls: Vec<(String, Vec<String>)>,
    refresh_calls: Vec<(String, Vec<String>)>,
    receiver_calls: Vec<(String, Vec<String>)>,
    receiver_probe_override: Option<Probe>,
}

/// The runner moves to a dedicated thread for an explicit or cadenced refresh
/// and comes back through this channel with its result.  That gives the
/// scheduler one in-flight owner without holding the runtime mutex during a
/// potentially slow subprocess wait.
type RefreshCompletion = (
    Box<dyn RefreshRunner + Send>,
    Option<i32>,
    String,
    SystemTime,
);

struct PendingRefresh {
    completion: MpscReceiver<RefreshCompletion>,
    cancellation: RefreshCancellation,
    worker: Option<std::thread::JoinHandle<()>>,
}

impl Runtime {
    /// The complete command allowlist.  Keep this alongside the capability
    /// declaration so review can compare both sides of the IPC boundary.
    pub const fn authorized_commands() -> [&'static str; 6] {
        [
            "get_view_state",
            "refresh_now",
            "open_view",
            "set_settings",
            "quit",
            "hide_popover",
        ]
    }

    pub fn is_authorized(command: &str) -> bool {
        Self::authorized_commands().contains(&command)
    }

    pub fn new(config: RuntimeConfig, dependencies: RuntimeDependencies) -> Self {
        Self::new_inner(config, dependencies, Vec::new(), None)
    }

    /// Alias used by the contract to make it explicit that no real system
    /// adapters are involved.
    pub fn new_for_test(config: RuntimeConfig, dependencies: RuntimeDependencies) -> Self {
        Self::new(config, dependencies)
    }

    /// Builds a production runtime from the hardened CLI resolution result.
    pub fn from_resolution(
        settings_dir: PathBuf,
        resolution: Resolution,
        dependencies: RuntimeDependencies,
    ) -> Self {
        let cli_program = resolution.cli.as_ref().map(|cli| cli.program().to_string());
        let setup = cli::setup_state_for(&resolution, None);
        Self::new_inner(
            RuntimeConfig {
                cli_program,
                settings_dir,
                // Settings own the persisted cadence. Zero is the constructor's
                // explicit "use persisted setting" sentinel; direct tests pass
                // their own non-zero cadence.
                cadence: Duration::ZERO,
                now: SystemTime::now(),
            },
            dependencies,
            resolution.probed,
            setup,
        )
    }

    fn new_inner(
        config: RuntimeConfig,
        dependencies: RuntimeDependencies,
        probed: Vec<String>,
        setup: Option<SetupState>,
    ) -> Self {
        let settings = settings::load(&config.settings_dir);
        let cadence = if config.cadence.is_zero() {
            settings.refresh_cadence()
        } else {
            config.cadence
        };
        let cli_program = config.cli_program;
        let mut receiver = cli_program
            .as_ref()
            .map(|program| Receiver::new(program.clone()));
        if let Some(receiver) = receiver.as_mut() {
            receiver.set_hosting_enabled(settings.host_receiver);
        }

        Runtime {
            supervisor: cli_program
                .as_ref()
                .map(|program| Supervisor::new(program.clone())),
            scheduler: cli_program
                .as_ref()
                .map(|program| Scheduler::new(program.clone(), cadence)),
            cli_program,
            probed,
            setup,
            settings_dir: config.settings_dir,
            settings,
            receiver,
            cache: ReportCache::default(),
            dependencies,
            started: false,
            refresh_task: None,
            receiver_retry_at: None,
            quitting: false,
            server_calls: Vec::new(),
            refresh_calls: Vec::new(),
            receiver_calls: Vec::new(),
            receiver_probe_override: None,
        }
    }

    /// Starts the one server, startup refresh, and first report read.
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
            self.refresh_startup()?;
            self.poll_report_inner(SystemTime::now());
            self.poll_receiver_inner(SystemTime::now());
        }
        self.publish()
    }

    /// Performs the low-frequency maintenance work.  Tauri calls this from a
    /// blocking worker; the method itself never assumes it owns an async
    /// runtime.
    pub fn tick(&mut self, now: SystemTime) -> Result<()> {
        if !self.started || self.setup.is_some() || self.quitting {
            return Ok(());
        }
        self.ensure_server()?;
        if self.setup.is_some() {
            return self.publish();
        }

        let completed = self.finish_pending_refresh(None)?;
        let due = self
            .scheduler
            .as_ref()
            .is_some_and(|scheduler| scheduler.is_due(now));
        let started = if due {
            self.start_background_refresh(now)?
        } else {
            false
        };
        if completed || started {
            self.publish()?;
        }
        self.poll_receiver_inner(now);
        Ok(())
    }

    pub fn get_view_state(&self) -> ViewState {
        let server = self
            .supervisor
            .as_ref()
            .map(Supervisor::phase)
            .unwrap_or(crate::supervisor::ServerPhase::Starting);
        let refresh = self
            .scheduler
            .as_ref()
            .map(Scheduler::status)
            .cloned()
            .unwrap_or_default();
        let receiver = self
            .receiver
            .as_ref()
            .map(Receiver::status)
            .unwrap_or(ReceiverStatus::Unknown);
        ipc::view_state(
            self.setup.clone(),
            server,
            &self.cache,
            &refresh,
            receiver,
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

    pub fn refresh_now(&mut self, now: SystemTime) -> Result<()> {
        if self.quitting {
            return Err(anyhow!("the tray runtime is shutting down"));
        }
        // Give a just-completed process a short chance to return its runner.
        // This is not a UI-thread wait: Tauri invokes this runtime method from
        // `spawn_blocking`, and it prevents a completion/second-click race.
        let _ = self.finish_pending_refresh(Some(Duration::from_millis(50)))?;
        if self.refresh_task.is_some() {
            return Err(anyhow!("a refresh is already in progress"));
        }
        if !self.start_background_refresh(now)? {
            return Err(anyhow!("a refresh is already in progress"));
        }
        self.publish()
    }

    pub fn open_view(&mut self, view: &str) -> Result<()> {
        if self.quitting {
            return Err(anyhow!("the tray runtime is shutting down"));
        }
        let server_url = self
            .supervisor
            .as_ref()
            .and_then(|supervisor| supervisor.url())
            .ok_or_else(|| anyhow!("the KyberDash server is not ready"))?;
        let url = ipc::open_view_url(server_url, view)?;
        self.dependencies.opener.open(&url)
    }

    /// Validates, persists, and applies a partial settings document.
    pub fn set_settings(&mut self, patch: Value) -> Result<()> {
        if self.quitting {
            return Err(anyhow!("the tray runtime is shutting down"));
        }
        if !patch.is_object() {
            return Err(anyhow!("settings patch must be an object"));
        }
        let previous_harness = self.settings.harness.clone();
        let previous_window_days = self.settings.window_days;
        self.settings.apply_partial(&patch);
        settings::save(&self.settings_dir, &self.settings)?;
        if let Some(scheduler) = self.scheduler.as_mut() {
            scheduler.set_cadence(self.settings.refresh_cadence());
        }
        if let Some(receiver) = self.receiver.as_mut() {
            receiver.set_hosting_enabled(self.settings.host_receiver);
        }
        if self.settings.harness != previous_harness
            || self.settings.window_days != previous_window_days
        {
            // Scope controls take effect immediately; waiting for the next
            // cadence would leave the selector and report describing different
            // datasets for a visible interval.
            self.poll_report_inner(SystemTime::now());
        }
        self.publish()
    }

    pub fn settings(&self) -> TraySettings {
        self.settings.clone()
    }

    /// Stops every owned child.  `Supervisor` also performs this on drop, but
    /// command and application-exit paths call it explicitly so shutdown does
    /// not depend on process teardown ordering.
    pub fn quit(&mut self) {
        if self.quitting {
            return;
        }
        self.quitting = true;
        self.cancel_pending_refresh();
        if let Some(supervisor) = self.supervisor.as_mut() {
            supervisor.stop();
        }
        self.dependencies.receiver_spawner.stop_all();
    }

    pub fn server_calls(&self) -> Vec<(String, Vec<String>)> {
        self.server_calls.clone()
    }

    pub fn refresh_calls(&self) -> Vec<(String, Vec<String>)> {
        self.refresh_calls.clone()
    }

    pub fn receiver_calls(&self) -> Vec<(String, Vec<String>)> {
        self.receiver_calls.clone()
    }

    pub fn clear_cli_for_test(&mut self) {
        self.cli_program = None;
        self.supervisor = None;
        self.scheduler = None;
        self.receiver = None;
        self.setup = None;
    }

    pub fn set_receiver_probe(&mut self, probe: Probe) {
        self.receiver_probe_override = Some(probe);
    }

    pub fn poll_receiver(&mut self) -> Result<()> {
        if self.quitting {
            return Ok(());
        }
        self.poll_receiver_inner(SystemTime::now());
        self.publish()
    }

    fn ensure_missing_cli_setup(&mut self) {
        if self.setup.is_none() {
            self.setup = Some(SetupState::new(SetupReason::NotFound, self.probed.clone()));
        }
        self.cache = ReportCache::default();
    }

    fn ensure_server(&mut self) -> Result<()> {
        let Some(supervisor) = self.supervisor.as_mut() else {
            self.ensure_missing_cli_setup();
            return Ok(());
        };
        if supervisor.url().is_some() && supervisor.is_running() {
            return Ok(());
        }
        let program = self
            .cli_program
            .as_ref()
            .ok_or_else(|| anyhow!("KyberDash CLI is unavailable"))?
            .clone();
        self.server_calls.push((
            program,
            crate::supervisor::SERVER_ARGS
                .iter()
                .map(|argument| (*argument).to_string())
                .collect(),
        ));
        let attempt = supervisor.attempt(
            &mut *self.dependencies.server_spawner,
            &mut *self.dependencies.clock,
        );
        match attempt {
            Attempt::Listening(_) => {
                if let Some(api_version) = supervisor.api_version() {
                    let resolution = Resolution {
                        cli: None,
                        probed: self.probed.clone(),
                    };
                    // A direct test config does not carry a Resolution.  It is
                    // nevertheless already known to have a CLI, so only the
                    // API-version half is relevant here.
                    if api_version < crate::cli::MIN_API_VERSION {
                        self.setup = Some(SetupState::new(SetupReason::TooOld, resolution.probed));
                        supervisor.stop();
                    }
                }
            }
            Attempt::Failed { reason, .. } => self.cache.record_error(reason),
        }
        Ok(())
    }

    fn refresh_startup(&mut self) -> Result<()> {
        let program = self
            .cli_program
            .as_ref()
            .ok_or_else(|| anyhow!("KyberDash CLI is unavailable"))?
            .clone();
        let scheduler = self
            .scheduler
            .as_mut()
            .ok_or_else(|| anyhow!("refresh scheduler is unavailable"))?;
        self.refresh_calls.push((
            program,
            crate::scheduler::REFRESH_ARGS
                .iter()
                .map(|argument| (*argument).to_string())
                .collect(),
        ));
        let _ = scheduler.tick(&mut *self.dependencies.refresh_runner, SystemTime::now());
        Ok(())
    }

    /// Starts an owned refresh without blocking the caller.  The scheduler is
    /// transitioned before spawning, so a second command sees `Running` even
    /// if the worker has not reached the process launcher yet.
    fn start_background_refresh(&mut self, now: SystemTime) -> Result<bool> {
        if self.quitting || self.refresh_task.is_some() {
            return Ok(false);
        }
        let program = self
            .cli_program
            .as_ref()
            .ok_or_else(|| anyhow!("KyberDash CLI is unavailable"))?
            .clone();
        let scheduler = self
            .scheduler
            .as_mut()
            .ok_or_else(|| anyhow!("refresh scheduler is unavailable"))?;
        if !scheduler.begin_refresh() {
            return Ok(false);
        }
        self.refresh_calls.push((
            program.clone(),
            crate::scheduler::REFRESH_ARGS
                .iter()
                .map(|argument| (*argument).to_string())
                .collect(),
        ));
        let mut runner = std::mem::replace(
            &mut self.dependencies.refresh_runner,
            Box::new(UnavailableRefreshRunner),
        );
        let cancellation = RefreshCancellation::default();
        let worker_cancellation = cancellation.clone();
        let (sender, completion) = mpsc::channel();
        let worker = std::thread::spawn(move || {
            let (code, stderr) = runner.run_cancellable(
                &program,
                &crate::scheduler::REFRESH_ARGS,
                &worker_cancellation,
            );
            let _ = sender.send((runner, code, stderr, now));
        });
        self.refresh_task = Some(PendingRefresh {
            completion,
            cancellation,
            worker: Some(worker),
        });
        Ok(true)
    }

    /// Restores the runner and reports its outcome when an owned refresh has
    /// completed.  A caller can wait briefly only to bridge a worker-complete
    /// race; normal maintenance uses the non-blocking form.
    fn finish_pending_refresh(&mut self, wait_for: Option<Duration>) -> Result<bool> {
        let Some(mut task) = self.refresh_task.take() else {
            return Ok(false);
        };
        let completed = match wait_for {
            Some(timeout) => match task.completion.recv_timeout(timeout) {
                Ok(result) => Some(result),
                Err(RecvTimeoutError::Timeout) => None,
                Err(RecvTimeoutError::Disconnected) => {
                    return Err(anyhow!(
                        "refresh worker stopped without returning its runner"
                    ));
                }
            },
            None => match task.completion.try_recv() {
                Ok(result) => Some(result),
                Err(TryRecvError::Empty) => None,
                Err(TryRecvError::Disconnected) => {
                    return Err(anyhow!(
                        "refresh worker stopped without returning its runner"
                    ));
                }
            },
        };
        let Some((runner, code, stderr, finished_at)) = completed else {
            self.refresh_task = Some(task);
            return Ok(false);
        };
        if let Some(worker) = task.worker.take() {
            let _ = worker.join();
        }
        self.dependencies.refresh_runner = runner;
        let scheduler = self
            .scheduler
            .as_mut()
            .ok_or_else(|| anyhow!("refresh scheduler is unavailable"))?;
        let _ = scheduler.finish_refresh(code, &stderr, finished_at);
        Ok(true)
    }

    /// Cancels and joins the one worker that owns the refresh runner.  Joining
    /// is intentional: dropping the receiver alone would let a refresh child
    /// continue writing canon.db after the tray has exited.
    fn cancel_pending_refresh(&mut self) {
        let Some(mut task) = self.refresh_task.take() else {
            return;
        };
        task.cancellation.cancel();
        if let Ok((runner, _, _, _)) = task.completion.recv() {
            self.dependencies.refresh_runner = runner;
        }
        if let Some(worker) = task.worker.take() {
            let _ = worker.join();
        }
        if let Some(scheduler) = self.scheduler.as_mut() {
            scheduler.cancel_refresh();
        }
    }

    fn poll_report_inner(&mut self, now: SystemTime) {
        let Some(server_url) = self
            .supervisor
            .as_ref()
            .and_then(|supervisor| supervisor.url())
            .map(str::to_string)
        else {
            return;
        };
        let report_url = match api::report_url_for_scope(
            &server_url,
            &self.settings.harness,
            self.settings.window_days,
        ) {
            Ok(url) => url,
            Err(error) => {
                self.cache.record_error(error.to_string());
                return;
            }
        };
        match self.dependencies.fetcher.fetch(&report_url) {
            Ok(report) => self.cache.record_success(report, now),
            Err(error) => self.cache.record_error(error.to_string()),
        }
        if let Some(scheduler) = self.scheduler.as_mut() {
            scheduler.observe_report(self.cache.report.as_ref());
        }
    }

    fn poll_receiver_inner(&mut self, now: SystemTime) {
        let Some(receiver) = self.receiver.as_mut() else {
            return;
        };
        if self
            .receiver_retry_at
            .is_some_and(|retry_at| now < retry_at)
        {
            return;
        }
        self.receiver_retry_at = None;
        let delay = if let Some(probe) = self.receiver_probe_override.clone() {
            let mut probe = StaticProbe(probe);
            receiver.poll(&mut probe, &mut *self.dependencies.receiver_spawner)
        } else {
            receiver.poll(
                &mut *self.dependencies.receiver_probe,
                &mut *self.dependencies.receiver_spawner,
            )
        };
        if let Some(delay) = delay {
            self.receiver_retry_at = Some(now.checked_add(delay).unwrap_or(now));
        }
    }

    fn publish(&mut self) -> Result<()> {
        let snapshot = self.get_view_state();
        self.dependencies
            .event_sink
            .emit(VIEW_STATE_CHANGED, &snapshot)
    }
}

struct StaticProbe(Probe);

impl HealthProbe for StaticProbe {
    fn probe(&mut self, _url: &str) -> Probe {
        self.0.clone()
    }
}

/// Placeholder while a worker owns the real runner.  It is never called: the
/// pending-task guard prevents a second spawn until the worker returns it.
struct UnavailableRefreshRunner;

impl RefreshRunner for UnavailableRefreshRunner {
    fn run(&mut self, _program: &str, _args: &[&str]) -> (Option<i32>, String) {
        (
            None,
            "refresh runner is already owned by a worker".to_string(),
        )
    }
}

impl Drop for Runtime {
    fn drop(&mut self) {
        self.quit();
    }
}
