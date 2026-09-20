//! Launch at login. Windows: a value under HKCU\...\CurrentVersion\Run pointing at this
//! executable. Linux: an XDG autostart .desktop file. No extra crates; both are a few
//! lines of `reg` / plain file IO.

use anyhow::{anyhow, Context, Result};

#[cfg(any(target_os = "windows", target_os = "linux"))]
const APP_NAME: &str = "KyberDash";

#[cfg(target_os = "windows")]
const RUN_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

/// Absolute `reg.exe` out of System32 -- see `cli::system_command`.
#[cfg(target_os = "windows")]
fn reg(args: &[&str]) -> Result<std::process::Output> {
    crate::cli::system_command("reg.exe")
        .args(args)
        .output()
        .with_context(|| "failed to run reg.exe")
}

#[cfg(target_os = "windows")]
pub fn is_enabled() -> bool {
    reg(&["query", RUN_KEY, "/v", APP_NAME])
        .map(|out| out.status.success())
        .unwrap_or(false)
}

#[cfg(target_os = "windows")]
pub fn set_enabled(enabled: bool) -> Result<()> {
    if enabled {
        let exe = std::env::current_exe().with_context(|| "cannot resolve current exe")?;
        let value = format!("\"{}\"", exe.display());
        let out = reg(&[
            "add", RUN_KEY, "/v", APP_NAME, "/t", "REG_SZ", "/d", &value, "/f",
        ])?;
        if !out.status.success() {
            return Err(anyhow!(String::from_utf8_lossy(&out.stderr)
                .trim()
                .to_string()));
        }
    } else {
        let out = reg(&["delete", RUN_KEY, "/v", APP_NAME, "/f"])?;
        // Deleting a value that does not exist is the state we want anyway.
        if !out.status.success() && is_enabled() {
            return Err(anyhow!(String::from_utf8_lossy(&out.stderr)
                .trim()
                .to_string()));
        }
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn desktop_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| {
        d.join("autostart")
            .join("io.github.dpalfery.kyberdash.desktop")
    })
}

#[cfg(target_os = "linux")]
pub fn is_enabled() -> bool {
    desktop_file().map(|p| p.is_file()).unwrap_or(false)
}

#[cfg(target_os = "linux")]
pub fn set_enabled(enabled: bool) -> Result<()> {
    let path = desktop_file().ok_or_else(|| anyhow!("no config dir"))?;
    if enabled {
        let exe = std::env::current_exe().with_context(|| "cannot resolve current exe")?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(
            &path,
            format!(
                "[Desktop Entry]\nType=Application\nName={APP_NAME}\nExec=\"{}\"\nX-GNOME-Autostart-enabled=true\n",
                exe.display()
            ),
        )?;
    } else if path.exists() {
        std::fs::remove_file(&path)?;
    }
    Ok(())
}

/// macOS registers a LaunchAgent. The writer lives in `login_item_macos`,
/// which takes its directory as an argument so it can be tested; this is the
/// thin binding to the real home directory and the running executable.
#[cfg(target_os = "macos")]
pub fn is_enabled() -> bool {
    match dirs::home_dir() {
        Some(home) => {
            crate::login_item_macos::is_enabled(&crate::login_item_macos::agent_dir(&home))
        }
        None => false,
    }
}

#[cfg(target_os = "macos")]
pub fn set_enabled(enabled: bool) -> Result<()> {
    let home = dirs::home_dir().ok_or_else(|| anyhow!("no home directory"))?;
    let program = std::env::current_exe().context("locating this executable")?;
    crate::login_item_macos::set_enabled(
        &crate::login_item_macos::agent_dir(&home),
        enabled,
        &program,
    )
}
