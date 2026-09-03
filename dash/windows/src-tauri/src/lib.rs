mod autostart;
mod cli;
mod config;
mod fx;
mod plan;
/// The spend-in-the-tray badge is a second tray icon, which only the Tauri tray backend
/// provides; Linux runs its own SNI tray (`tray_linux`) and has no equivalent, so the
/// whole module is compiled out there rather than sitting unused.
#[cfg(not(target_os = "linux"))]
mod tray_badge;
#[cfg(target_os = "linux")]
mod tray_linux;

use std::sync::Mutex;
use std::sync::atomic::{AtomicI64, Ordering};

static LAST_HIDDEN_MS: AtomicI64 = AtomicI64::new(0);

use tauri::{AppHandle, Emitter, Manager, WindowEvent};
#[cfg(target_os = "linux")]
use tauri::Listener;

#[cfg(not(target_os = "linux"))]
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
};

use crate::cli::CodeburnCli;
use crate::config::CurrencyConfig;
use crate::fx::FxCache;

#[cfg(not(target_os = "linux"))]
const TRAY_ID: &str = "codeburn-tray";
/// Second tray icon that carries today's spend as text, sitting next to the logo. The
/// closest the Windows notification area gets to the macOS menubar title.
#[cfg(not(target_os = "linux"))]
const BADGE_TRAY_ID: &str = "codeburn-badge";
const POPOVER_LABEL: &str = "popover";

/// Shared application state. Wraps the CLI handle + currency config + FX cache so every
/// Tauri command sees the same instances. Interior Mutex keeps things simple; the state is
/// touched from the main thread (UI) and the Tokio runtime (CLI spawn, HTTP), both of
/// which go through `#[tauri::command]` async functions that acquire the lock briefly.
pub struct AppState {
    pub cli: Mutex<CodeburnCli>,
    pub config: Mutex<CurrencyConfig>,
    pub fx: FxCache,
    pub plan: plan::PlanClient,
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            app.manage(AppState {
                cli: Mutex::new(CodeburnCli::resolve()),
                config: Mutex::new(CurrencyConfig::load_or_default()),
                fx: FxCache::new(),
                plan: plan::PlanClient::new(),
            });

            #[cfg(not(target_os = "linux"))]
            build_tray_tauri(app.handle())?;

            #[cfg(target_os = "linux")]
            init_tray_linux(app.handle().clone(), tray_linux::LinuxTrayHandle::empty());

            if let Some(window) = app.get_webview_window(POPOVER_LABEL) {
                let _ = window.hide();
                #[cfg(target_os = "windows")]
                round_window_corners(&window);
            }

            Ok(())
        })
        .on_window_event(|window, event| {
            match event {
                WindowEvent::CloseRequested { api, .. } => {
                    api.prevent_close();
                    let _ = window.hide();
                    mark_hidden(window.app_handle());
                }
                WindowEvent::Focused(false) => {
                    let _ = window.hide();
                    mark_hidden(window.app_handle());
                }
                _ => {}
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::fetch_payload,
            commands::cli_status,
            commands::set_currency,
            commands::open_terminal_command,
            commands::open_claude_login,
            commands::quit_app,
            commands::hide_popover,
            commands::set_tray_tooltip,
            commands::set_tray_badge,
            commands::app_version,
            commands::plan_usage,
            commands::launch_at_login,
            commands::set_launch_at_login,
        ])
        .build(tauri::generate_context!())
        .expect("error while running tauri application")
        .run(|_app, event| {
            if let tauri::RunEvent::ExitRequested { api, .. } = event {
                api.prevent_exit();
            }
        });
}

#[cfg(not(target_os = "linux"))]
fn build_tray_tauri(app: &AppHandle) -> tauri::Result<()> {
    let Some(tray) = app.tray_by_id(TRAY_ID) else {
        return Ok(());
    };

    let open = MenuItem::with_id(app, "open", "Open CodeBurn", true, None::<&str>)?;
    let refresh = MenuItem::with_id(app, "refresh", "Refresh", true, None::<&str>)?;
    let theme = MenuItem::with_id(app, "toggle_theme", "Toggle Dark/Light", true, None::<&str>)?;
    let report = MenuItem::with_id(app, "report", "Open Full Report", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit CodeBurn", true, None::<&str>)?;
    let menu = Menu::with_items(
        app,
        &[
            &open,
            &refresh,
            &theme,
            &report,
            &PredefinedMenuItem::separator(app)?,
            &quit,
        ],
    )?;

    tray.set_menu(Some(menu.clone()))?;
    tray.set_show_menu_on_left_click(false)?;
    let _ = tray.set_tooltip(Some("CodeBurn"));
    tray.on_menu_event(on_tray_menu_event);
    tray.on_tray_icon_event(on_tray_icon_event);

    // The badge icon starts fully transparent and hidden; the frontend shows it once it has
    // today's spend. Registering it right after the logo puts it beside the logo in the tray.
    let blank = tauri::image::Image::new_owned(
        vec![0u8; (BLANK_ICON_SIZE * BLANK_ICON_SIZE * 4) as usize],
        BLANK_ICON_SIZE,
        BLANK_ICON_SIZE,
    );
    TrayIconBuilder::with_id(BADGE_TRAY_ID)
        .icon(blank)
        .tooltip("CodeBurn")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(on_tray_menu_event)
        .on_tray_icon_event(on_tray_icon_event)
        .build(app)?
        .set_visible(false)?;

    Ok(())
}

#[cfg(not(target_os = "linux"))]
const BLANK_ICON_SIZE: u32 = 16;

#[cfg(not(target_os = "linux"))]
fn on_tray_menu_event(app: &AppHandle, event: tauri::menu::MenuEvent) {
    match event.id.as_ref() {
        "quit" => app.exit(0),
        "open" => show_popover(app, None),
        "refresh" => {
            if let Some(window) = app.get_webview_window(POPOVER_LABEL) {
                let _ = window.emit("codeburn://refresh", ());
            }
        }
        "toggle_theme" => {
            if let Some(window) = app.get_webview_window(POPOVER_LABEL) {
                let _ = window.emit("codeburn://toggle-theme", ());
            }
        }
        "report" => {
            let _ = cli::spawn_in_terminal(app, &["report"]);
        }
        _ => {}
    }
}

#[cfg(not(target_os = "linux"))]
fn on_tray_icon_event(tray: &tauri::tray::TrayIcon, event: TrayIconEvent) {
    match event {
        TrayIconEvent::Click {
            button: MouseButton::Left,
            button_state: MouseButtonState::Up,
            position,
            ..
        }
        | TrayIconEvent::DoubleClick {
            button: MouseButton::Left,
            position,
            ..
        } => {
            toggle_popover(tray.app_handle(), Some((position.x as i32, position.y as i32)));
        }
        _ => {}
    }
}

#[cfg(target_os = "linux")]
fn init_tray_linux(app: AppHandle, handle: tray_linux::LinuxTrayHandle) {
    // Spawn the SNI tray on the Tokio runtime that Tauri already owns.
    let spawn_app = app.clone();
    let spawn_handle = handle.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(err) = tray_linux::spawn(spawn_app, spawn_handle).await {
            eprintln!("codeburn: failed to spawn Linux tray: {err}");
        }
    });

    // Left-click on the tray: show popover anchored to the click coordinates.
    let activate_app = app.clone();
    app.listen_any("codeburn://tray-activate", move |event| {
        let anchor = parse_click(event.payload());
        toggle_popover(&activate_app, anchor);
    });

    // Right-click / middle-click: same as left for now. Quit lives in the popover footer.
    let secondary_app = app.clone();
    app.listen_any("codeburn://tray-secondary", move |event| {
        let anchor = parse_click(event.payload());
        toggle_popover(&secondary_app, anchor);
    });
}

#[cfg(target_os = "linux")]
fn parse_click(payload: &str) -> Option<(i32, i32)> {
    let value: serde_json::Value = serde_json::from_str(payload).ok()?;
    let x = value.get("x")?.as_i64()? as i32;
    let y = value.get("y")?.as_i64()? as i32;
    Some((x, y))
}

/// Undecorated windows are square by default; ask DWM for the Windows 11 rounded corner so
/// the acrylic backdrop is clipped to the same shape as the popover card. Silently ignored
/// on Windows 10, where the corners stay square.
#[cfg(target_os = "windows")]
fn round_window_corners(window: &tauri::WebviewWindow) {
    use windows_sys::Win32::Graphics::Dwm::{
        DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
    };
    let Ok(hwnd) = window.hwnd() else { return };
    let preference: u32 = DWMWCP_ROUND as u32;
    unsafe {
        DwmSetWindowAttribute(
            hwnd.0 as _,
            DWMWA_WINDOW_CORNER_PREFERENCE as u32,
            &preference as *const u32 as *const std::ffi::c_void,
            std::mem::size_of::<u32>() as u32,
        );
    }
}

/// A blur immediately followed by the tray click that caused it would re-open the popover;
/// ignore show requests inside this window after a hide.
const TOGGLE_DEBOUNCE_MS: i64 = 300;

fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}

/// Every path that hides the popover goes through here, so the debounce stamp and the
/// frontend's visibility signal can never drift apart. The frontend drops to its idle
/// refresh cadence on `codeburn://hidden` and comes back on `codeburn://shown`.
fn mark_hidden(app: &AppHandle) {
    LAST_HIDDEN_MS.store(now_ms(), Ordering::Relaxed);
    let _ = app.emit("codeburn://hidden", ());
}

fn toggle_popover(app: &AppHandle, anchor: Option<(i32, i32)>) {
    let Some(window) = app.get_webview_window(POPOVER_LABEL) else {
        return;
    };
    if window.is_visible().unwrap_or(false) {
        let _ = window.hide();
        mark_hidden(app);
        return;
    }
    let last = LAST_HIDDEN_MS.load(Ordering::Relaxed);
    if now_ms() - last < TOGGLE_DEBOUNCE_MS {
        return;
    }
    show_popover(app, anchor);
}

fn show_popover(app: &AppHandle, anchor: Option<(i32, i32)>) {
    let Some(window) = app.get_webview_window(POPOVER_LABEL) else {
        return;
    };
    // Position before showing so the first frame is already in place (no jump).
    position_popover(&window, anchor);
    let _ = window.show();
    let _ = window.unminimize();
    position_popover(&window, anchor);
    let _ = window.set_focus();
    let _ = window.emit("codeburn://shown", ());
}

/// Places the popover against the taskbar / panel edge of the monitor that owns the click
/// (or the cursor, when the request came from a menu). The work area already excludes the
/// taskbar on Windows and panels on Linux, so we never need to guess their heights: the
/// popover sits `MARGIN` inside the work area, horizontally centred on the anchor and
/// clamped to the screen.
fn position_popover(window: &tauri::WebviewWindow, anchor: Option<(i32, i32)>) {
    const POPOVER_WIDTH_LOGICAL: f64 = 360.0;
    const POPOVER_HEIGHT_LOGICAL: f64 = 660.0;
    const MARGIN_LOGICAL: f64 = 8.0;

    let point = anchor
        .filter(|(x, y)| *x > 0 || *y > 0)
        .map(|(x, y)| (x as f64, y as f64))
        .or_else(|| window.cursor_position().ok().map(|p| (p.x, p.y)));

    let monitor = point
        .and_then(|(x, y)| window.monitor_from_point(x, y).ok().flatten())
        .or_else(|| window.primary_monitor().ok().flatten());
    let Some(monitor) = monitor else {
        return;
    };

    let scale = monitor.scale_factor();
    let pop_w = (POPOVER_WIDTH_LOGICAL * scale).round() as i32;
    let pop_h = (POPOVER_HEIGHT_LOGICAL * scale).round() as i32;
    let margin = (MARGIN_LOGICAL * scale).round() as i32;

    let area = monitor.work_area();
    let area_x = area.position.x;
    let area_y = area.position.y;
    let area_w = area.size.width as i32;
    let area_h = area.size.height as i32;
    let screen = monitor.size();
    let screen_pos = monitor.position();

    let (anchor_x, anchor_y) = point
        .map(|(x, y)| (x as i32, y as i32))
        .unwrap_or((area_x + area_w - pop_w / 2 - margin, area_y + area_h));

    let min_x = area_x + margin;
    let max_x = (area_x + area_w - pop_w - margin).max(min_x);
    let x = (anchor_x - pop_w / 2).clamp(min_x, max_x);

    // Which edge holds the taskbar? Whichever side the work area was trimmed on. If the
    // taskbar is at the top (or the anchor is in the top half with no bottom taskbar) the
    // popover drops down from the top edge; otherwise it rises from the bottom edge.
    let trimmed_top = area_y > screen_pos.y;
    let trimmed_bottom = (area_y + area_h) < (screen_pos.y + screen.height as i32);
    let anchor_in_top_half = anchor_y < screen_pos.y + (screen.height as i32) / 2;
    let open_downward = trimmed_top || (!trimmed_bottom && anchor_in_top_half);

    let y = if open_downward {
        area_y + margin
    } else {
        (area_y + area_h - pop_h - margin).max(area_y + margin)
    };

    let _ = window.set_position(tauri::PhysicalPosition::new(x, y));
}

mod commands {
    use super::{AppState, POPOVER_LABEL};
    use serde_json::Value;
    use tauri::{AppHandle, Manager, State};

    #[tauri::command]
    pub async fn fetch_payload(
        period: String,
        provider: String,
        include_optimize: bool,
        state: State<'_, AppState>,
    ) -> Result<Value, String> {
        let cli = state.cli.lock().map_err(|e| e.to_string())?.clone();
        cli.fetch_menubar_payload(&period, &provider, include_optimize)
            .await
            .map_err(|e| e.to_string())
    }

    /// Re-resolves the CLI each call so a freshly installed `codeburn` is picked up
    /// without restarting the tray app.
    #[tauri::command]
    pub async fn cli_status(state: State<'_, AppState>) -> Result<crate::cli::CliStatus, String> {
        let fresh = crate::cli::CodeburnCli::resolve();
        let status = fresh.status().await;
        if status.found {
            if let Ok(mut guard) = state.cli.lock() {
                *guard = fresh;
            }
        }
        Ok(status)
    }

    #[tauri::command]
    pub async fn set_currency(
        code: String,
        state: State<'_, AppState>,
    ) -> Result<crate::fx::CurrencyApplied, String> {
        let symbol = crate::fx::symbol_for(&code);
        let rate = state
            .fx
            .rate_for(&code)
            .await
            .ok_or_else(|| format!("Exchange rate for {code} is unavailable right now"))?;
        state
            .config
            .lock()
            .map_err(|e| e.to_string())?
            .set_currency(&code, &symbol)
            .map_err(|e| e.to_string())?;
        Ok(crate::fx::CurrencyApplied { code, symbol, rate })
    }

    #[tauri::command]
    pub fn open_terminal_command(app: AppHandle, args: Vec<String>) -> Result<(), String> {
        let args: Vec<&str> = args.iter().map(String::as_str).collect();
        crate::cli::spawn_in_terminal(&app, &args).map_err(|e| e.to_string())
    }

    #[tauri::command]
    pub fn open_claude_login(app: AppHandle) -> Result<(), String> {
        crate::cli::spawn_claude_login(&app).map_err(|e| e.to_string())
    }

    #[tauri::command]
    pub fn quit_app(app: AppHandle) {
        app.exit(0);
    }

    #[tauri::command]
    pub fn hide_popover(app: AppHandle) {
        if let Some(window) = app.get_webview_window(POPOVER_LABEL) {
            let _ = window.hide();
            super::mark_hidden(&app);
        }
    }

    /// The tray cannot render text on Windows, so today's spend lives in the tooltip.
    #[tauri::command]
    pub fn set_tray_tooltip(app: AppHandle, text: String) {
        #[cfg(not(target_os = "linux"))]
        for id in [super::TRAY_ID, super::BADGE_TRAY_ID] {
            if let Some(tray) = app.tray_by_id(id) {
                let _ = tray.set_tooltip(Some(text.as_str()));
            }
        }
        #[cfg(target_os = "linux")]
        {
            let _ = (app, text);
        }
    }

    /// `text` is a short spend string ("$87", "142", "1.2K"); `None` hides the badge icon.
    #[tauri::command]
    pub fn set_tray_badge(app: AppHandle, text: Option<String>) -> Result<(), String> {
        #[cfg(target_os = "linux")]
        {
            // Unreachable from the UI: the frontend hides the control wherever the badge is
            // unsupported (lib/platform.ts). Saying so beats reporting a success that never
            // happened.
            let _ = (app, text);
            Err("the tray spend badge needs a second tray icon, which the Linux SNI tray does not provide".to_string())
        }
        #[cfg(not(target_os = "linux"))]
        {
            let Some(badge) = app.tray_by_id(super::BADGE_TRAY_ID) else {
                return Ok(());
            };
            match text.as_deref().map(str::trim).filter(|t| !t.is_empty()) {
                Some(t) => {
                    let icon = crate::tray_badge::render(
                        t,
                        crate::tray_badge::small_icon_size(),
                        crate::tray_badge::taskbar_is_dark(),
                    );
                    // Windows can only modify an icon that is currently shown, so show
                    // first (re-adds the previous bitmap) and then swap the bitmap.
                    badge.set_visible(true).map_err(|e| e.to_string())?;
                    badge.set_icon(Some(icon)).map_err(|e| e.to_string())?;
                }
                None => {
                    badge.set_visible(false).map_err(|e| e.to_string())?;
                }
            }
            Ok(())
        }
    }

    #[tauri::command]
    pub fn app_version(app: AppHandle) -> String {
        app.package_info().version.to_string()
    }

    #[tauri::command]
    pub fn launch_at_login() -> bool {
        crate::autostart::is_enabled()
    }

    #[tauri::command]
    pub fn set_launch_at_login(enabled: bool) -> Result<bool, String> {
        crate::autostart::set_enabled(enabled).map_err(|e| e.to_string())?;
        Ok(crate::autostart::is_enabled())
    }

    #[tauri::command]
    pub async fn plan_usage(state: State<'_, AppState>) -> Result<crate::plan::PlanUsage, String> {
        state.plan.fetch().await.map_err(|e| e.to_string())
    }
}
