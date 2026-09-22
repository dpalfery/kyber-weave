pub mod api;
mod args;
pub mod autostart;
pub mod cli;
pub mod ipc;
#[cfg(target_os = "macos")]
pub mod login_item_macos;
pub mod position;
pub mod receiver;
pub mod runtime;
mod runtime_adapters;
pub mod scheduler;
pub mod settings;
pub mod status_item;
pub mod supervisor;
pub mod tray_badge;

use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, SystemTime};

use anyhow::{anyhow, Context, Result};
use tauri::{
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, State, WebviewWindow, WindowEvent,
};
use tauri_plugin_opener::OpenerExt;

use crate::runtime::{EventSink, Opener, Runtime, RuntimeDependencies};
use crate::runtime_adapters::{
    SystemHealthProbe, SystemReceiverSpawner, SystemRefreshRunner, SystemReportFetcher,
    SystemServerSpawner, ThreadClock,
};

pub use args::wants_quit;

/// Shared ownership deliberately lives behind an `Arc`: async commands clone
/// it before moving the blocking policy work onto Tauri's worker pool.
struct ManagedRuntime(Arc<RuntimeState>);

/// A read-mostly snapshot remains available while the runtime owns a blocking
/// server launch, refresh, or loopback request.  The WebView can therefore get
/// its initial `starting` state before the first fetch succeeds (or times out).
struct RuntimeState {
    runtime: Mutex<Runtime>,
    snapshot: RwLock<ipc::ViewState>,
}

#[derive(Debug, thiserror::Error)]
enum CommandError {
    #[error("{0}")]
    Runtime(String),
}

#[derive(serde::Serialize)]
#[serde(tag = "kind", content = "message", rename_all = "camelCase")]
enum CommandErrorPayload {
    Runtime(String),
}

impl serde::Serialize for CommandError {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        match self {
            CommandError::Runtime(message) => {
                CommandErrorPayload::Runtime(message.clone()).serialize(serializer)
            }
        }
    }
}

fn command_error(command: &str, error: impl std::fmt::Display) -> CommandError {
    let message = error.to_string();
    eprintln!("kyberdash-tray: {command} failed: {message}");
    CommandError::Runtime(message)
}

async fn with_runtime<T, F>(
    command: &'static str,
    state: Arc<RuntimeState>,
    operation: F,
) -> std::result::Result<T, CommandError>
where
    T: Send + 'static,
    F: FnOnce(&mut Runtime) -> Result<T> + Send + 'static,
{
    match tauri::async_runtime::spawn_blocking(move || {
        let mut runtime = state
            .runtime
            .lock()
            .map_err(|_| anyhow!("the tray runtime lock is poisoned"))?;
        let result = operation(&mut runtime);
        if result.is_ok() {
            let snapshot = runtime.get_view_state();
            let mut published = state
                .snapshot
                .write()
                .map_err(|_| anyhow!("the tray snapshot lock is poisoned"))?;
            *published = snapshot;
        }
        result
    })
    .await
    {
        Ok(Ok(value)) => Ok(value),
        Ok(Err(error)) => Err(command_error(command, error)),
        Err(error) => Err(command_error(command, error)),
    }
}

/// IPC contract: `get_view_state() -> ViewState`.
#[tauri::command]
async fn get_view_state(
    state: State<'_, ManagedRuntime>,
) -> std::result::Result<ipc::ViewState, CommandError> {
    state
        .0
        .snapshot
        .read()
        .map(|snapshot| snapshot.clone())
        .map_err(|_| command_error("get_view_state", "the tray snapshot lock is poisoned"))
}

/// IPC contract: `refresh_now() -> null`.
#[tauri::command]
async fn refresh_now(state: State<'_, ManagedRuntime>) -> std::result::Result<(), CommandError> {
    with_runtime("refresh_now", Arc::clone(&state.0), |runtime| {
        runtime.refresh_now(SystemTime::now())
    })
    .await
}

/// IPC contract: `open_view({ view: string }) -> null`.
#[tauri::command]
async fn open_view(
    state: State<'_, ManagedRuntime>,
    view: String,
) -> std::result::Result<(), CommandError> {
    with_runtime("open_view", Arc::clone(&state.0), move |runtime| {
        runtime.open_view(&view)
    })
    .await
}

/// IPC contract: `set_settings({ patch: Partial<TraySettings> }) -> ViewState`.
#[tauri::command]
async fn set_settings(
    state: State<'_, ManagedRuntime>,
    patch: serde_json::Value,
) -> std::result::Result<ipc::ViewState, CommandError> {
    with_runtime("set_settings", Arc::clone(&state.0), move |runtime| {
        runtime.set_settings(patch)?;
        // This is intentionally after validation and persistence: no command
        // path can register a login item from an unvalidated webview payload.
        autostart::set_enabled(runtime.settings().launch_at_login)
            .context("applying launch-at-login setting")?;
        Ok(runtime.get_view_state())
    })
    .await
}

/// IPC contract: `quit() -> null`.
#[tauri::command]
async fn quit(
    app: AppHandle,
    state: State<'_, ManagedRuntime>,
) -> std::result::Result<(), CommandError> {
    with_runtime("quit", Arc::clone(&state.0), |runtime| {
        runtime.quit();
        Ok(())
    })
    .await?;
    app.exit(0);
    Ok(())
}

/// IPC contract: `hide_popover() -> null`.
#[tauri::command]
fn hide_popover(app: AppHandle) {
    if let Some(window) = app.get_webview_window("popover") {
        let _ = window.hide();
    }
}

fn show_popover(app: &AppHandle, anchor: Option<(i32, i32)>) {
    let Some(window) = app.get_webview_window("popover") else {
        return;
    };
    position::position_popover(&window, anchor);
    let _ = window.show();
    let _ = window.set_focus();
}

fn toggle_popover(app: &AppHandle, anchor: Option<(i32, i32)>) {
    let Some(window) = app.get_webview_window("popover") else {
        return;
    };
    if window.is_visible().unwrap_or(false) {
        let _ = window.hide();
    } else {
        show_popover(app, anchor);
    }
}

fn close_popover_when_unfocused(window: &WebviewWindow) {
    // Clicking the status item focuses the tray, which fires Focused(false) on
    // the popover before the click handler runs. Without a short delay the
    // popover would hide itself on the same click that is meant to open it.
    let window = window.clone();
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(Duration::from_millis(150)).await;
        if !window.is_focused().unwrap_or(false) {
            let _ = window.hide();
        }
    });
}

/// Native event delivery and status-item projection. The webview receives a
/// full JSON snapshot; native presentation reads the same snapshot rather than
/// duplicating report interpretation in command handlers.
struct TauriEventSink {
    app: AppHandle,
}

impl EventSink for TauriEventSink {
    fn emit(&mut self, name: &str, state: &ipc::ViewState) -> Result<()> {
        apply_status_item(&self.app, state);
        self.app
            .emit(name, state.clone())
            .context("emitting the tray view-state snapshot")
    }
}

struct TauriOpener {
    app: AppHandle,
}

impl Opener for TauriOpener {
    fn open(&mut self, url: &str) -> Result<()> {
        // The runtime called `open_view_url` before this adapter, so `url` is
        // an approved route below the exact reported 127.0.0.1 origin.
        self.app
            .opener()
            .open_url(url, None::<&str>)
            .context("opening the KyberDash loopback view")
    }
}

fn template_icon() -> tauri::Result<tauri::image::Image<'static>> {
    tauri::image::Image::from_bytes(include_bytes!("../icons/tray-template.png"))
}

fn apply_status_item(app: &AppHandle, state: &ipc::ViewState) {
    let Some(tray) = app.tray_by_id("tray") else {
        return;
    };
    let item = status_item::status_item(
        state.report.as_ref(),
        &state.settings.thresholds(),
        matches!(state.phase, ipc::Phase::Stale),
        SystemTime::now(),
    );
    let _ = tray.set_title(item.title.as_deref());
    let _ = tray.set_tooltip(Some(&item.tooltip));

    #[cfg(target_os = "windows")]
    {
        let badge = item.title.as_deref().unwrap_or("—");
        let _ = tray.set_icon(Some(tray_badge::render(
            badge,
            tray_badge::small_icon_size(),
            false,
        )));
    }

    #[cfg(not(target_os = "windows"))]
    if let Ok(icon) = template_icon() {
        // macOS switches the monochrome asset between menu-bar appearances.
        // The coloured bundle icon remains the app/package icon.
        let _ = tray.set_icon_with_as_template(Some(icon), true);
    }
}

fn runtime_dependencies(app: AppHandle, leading_args: Vec<String>) -> Result<RuntimeDependencies> {
    Ok(RuntimeDependencies {
        server_spawner: Box::new(SystemServerSpawner::new(leading_args.clone())),
        refresh_runner: Box::new(SystemRefreshRunner::new(leading_args.clone())),
        fetcher: Box::new(SystemReportFetcher::new()?),
        clock: Box::new(ThreadClock),
        receiver_probe: Box::new(SystemHealthProbe::new()?),
        receiver_spawner: Box::new(SystemReceiverSpawner::new(leading_args)),
        event_sink: Box::new(TauriEventSink { app: app.clone() }),
        opener: Box::new(TauriOpener { app }),
    })
}

fn sync_snapshot(state: &RuntimeState, runtime: &Runtime) -> Result<()> {
    let mut snapshot = state
        .snapshot
        .write()
        .map_err(|_| anyhow!("the tray snapshot lock is poisoned"))?;
    *snapshot = runtime.get_view_state();
    Ok(())
}

fn start_runtime_worker(app: AppHandle, state: Arc<RuntimeState>) {
    let startup_state = Arc::clone(&state);
    tauri::async_runtime::spawn(async move {
        let startup = tauri::async_runtime::spawn_blocking(move || {
            let mut runtime = startup_state
                .runtime
                .lock()
                .map_err(|_| anyhow!("the tray runtime lock is poisoned"))?;
            runtime.start()?;
            sync_snapshot(&startup_state, &runtime)
        })
        .await;
        match startup {
            Ok(Ok(())) => {}
            Ok(Err(error)) => eprintln!("kyberdash-tray: runtime startup failed: {error}"),
            Err(error) => eprintln!("kyberdash-tray: runtime startup task failed: {error}"),
        }

        loop {
            tokio::time::sleep(Duration::from_secs(1)).await;
            let is_open = app
                .get_webview_window("popover")
                .and_then(|window| window.is_visible().ok())
                .unwrap_or(false);
            let poll_interval = api::poll_interval(is_open);
            let state_for_tick = Arc::clone(&state);
            let tick = tauri::async_runtime::spawn_blocking(move || {
                let mut runtime = state_for_tick
                    .runtime
                    .lock()
                    .map_err(|_| anyhow!("the tray runtime lock is poisoned"))?;
                runtime.tick(SystemTime::now())?;
                runtime.poll_report(SystemTime::now())?;
                sync_snapshot(&state_for_tick, &runtime)?;
                Ok::<_, anyhow::Error>(())
            });
            match tick.await {
                Ok(Ok(())) => {}
                Ok(Err(error)) => eprintln!("kyberdash-tray: runtime maintenance failed: {error}"),
                Err(error) => eprintln!("kyberdash-tray: runtime maintenance task failed: {error}"),
            }
            tokio::time::sleep(poll_interval.saturating_sub(Duration::from_secs(1))).await;
        }
    });
}

fn stop_runtime(app: &AppHandle) {
    if let Some(state) = app.try_state::<ManagedRuntime>() {
        if let Ok(mut runtime) = state.0.runtime.lock() {
            runtime.quit();
        }
    }
}

pub fn run() {
    let app = tauri::Builder::default()
        // The Rust command is the sole opener path. Disabling the plugin's
        // click injection avoids handing webview links a second opener route.
        .plugin(
            tauri_plugin_opener::Builder::new()
                .open_js_links_on_click(false)
                .build(),
        )
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            if wants_quit(&argv) {
                stop_runtime(app);
                app.exit(0);
                return;
            }
            show_popover(app, None);
        }))
        .setup(|app| {
            if wants_quit(std::env::args()) {
                // No running instance to forward to; installers still issue
                // `--quit` unconditionally (D7).
                app.handle().exit(0);
                return Ok(());
            }

            #[cfg(target_os = "macos")]
            {
                app.set_activation_policy(tauri::ActivationPolicy::Accessory);
                app.set_dock_visibility(false);
            }

            let resolution = cli::KyberdashCli::resolve_detailed();
            let leading_args = resolution
                .cli
                .as_ref()
                .map(|cli| cli.extra_args().to_vec())
                .unwrap_or_default();
            let settings_dir = app.path().app_config_dir()?;
            let runtime = Runtime::from_resolution(
                settings_dir,
                resolution,
                runtime_dependencies(app.handle().clone(), leading_args)?,
            );
            let runtime = Arc::new(RuntimeState {
                snapshot: RwLock::new(runtime.get_view_state()),
                runtime: Mutex::new(runtime),
            });
            app.manage(ManagedRuntime(Arc::clone(&runtime)));

            let popover = app
                .get_webview_window("popover")
                .expect("popover window is declared in tauri.conf.json");
            let popover_for_focus = popover.clone();
            popover.on_window_event(move |event| {
                if matches!(event, WindowEvent::Focused(false)) {
                    close_popover_when_unfocused(&popover_for_focus);
                }
            });

            let icon = template_icon()?;
            TrayIconBuilder::with_id("tray")
                .icon(icon)
                .icon_as_template(true)
                .tooltip("KyberDash")
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        position,
                        ..
                    } = event
                    {
                        toggle_popover(
                            tray.app_handle(),
                            Some((position.x as i32, position.y as i32)),
                        );
                    }
                })
                .build(app)?;

            start_runtime_worker(app.handle().clone(), runtime);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_view_state,
            refresh_now,
            open_view,
            set_settings,
            quit,
            hide_popover
        ])
        .build(tauri::generate_context!())
        .expect("error while building KyberDash tray");

    app.run(|app, event| {
        if matches!(event, tauri::RunEvent::Exit) {
            stop_runtime(app);
        }
    });
}
