pub mod api;
mod args;
pub mod autostart;
pub mod cli;
pub mod position;
pub mod receiver;
pub mod scheduler;
pub mod status_item;
pub mod supervisor;
pub mod tray_badge;

use std::time::Duration;

use tauri::{
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Manager, WebviewWindow, WindowEvent,
};

pub use args::wants_quit;

#[tauri::command]
fn quit(app: AppHandle) {
    app.exit(0);
}

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

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            if wants_quit(&argv) {
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

            let popover = app
                .get_webview_window("popover")
                .expect("popover window is declared in tauri.conf.json");
            let popover_for_focus = popover.clone();
            popover.on_window_event(move |event| {
                if matches!(event, WindowEvent::Focused(false)) {
                    close_popover_when_unfocused(&popover_for_focus);
                }
            });

            let icon = app
                .default_window_icon()
                .cloned()
                .expect("bundled tray icon");
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

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![quit, hide_popover])
        .run(tauri::generate_context!())
        .expect("error while running KyberDash tray");
}
