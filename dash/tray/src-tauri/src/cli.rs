use std::env;
use std::path::PathBuf;
use std::process::Stdio;

use anyhow::{anyhow, bail, Context, Result};
use serde::Serialize;
use serde_json::Value;
use tauri::AppHandle;
use tokio::io::AsyncReadExt;
use tokio::process::Command;
use tokio::time::{timeout, Duration};

/// Hard bounds mirror the macOS CLI / DataClient design. A malicious or stuck CLI
/// cannot pin the Tauri process: stdout is capped, stderr is bounded, total wall time is
/// 60s. A hostile KYBERDASH_BIN is rejected before any shell-resembling path is taken.
const MAX_PAYLOAD_BYTES: usize = 20 * 1024 * 1024;
const MAX_STDERR_BYTES: usize = 256 * 1024;
const VERSION_TIMEOUT_SECS: u64 = 20;

/// Oldest CLI this app can talk to, by semver. Informational only: the gate
/// Requirement 6.7 describes is [`MIN_API_VERSION`], because a release number
/// says nothing about the REST contract the tray actually reads.
pub const MIN_CLI_VERSION: (u32, u32, u32) = (0, 9, 9);

/// Oldest REST contract the tray can read, matched against the `apiVersion`
/// that `kyberdash web` prints on its listening line and serves from
/// `GET /api/kyber/meta`. The server sends `REPORT_SCHEMA_VERSION`, so this
/// moves only when that document's shape changes in a way the popover feels.
pub const MIN_API_VERSION: u32 = 1;

/// The setup state to show, or `None` when the tray can go on.
///
/// Both halves of Requirement 6.7 land here: a CLI that could not be resolved,
/// and one whose REST contract is older than this tray understands. Taking the
/// observed version as an argument keeps it a pure decision — the supervisor
/// reads it off the listening line, and a test does not need a server.
pub fn setup_state_for(resolution: &Resolution, api_version: Option<u32>) -> Option<SetupState> {
    if resolution.cli.is_none() {
        return Some(SetupState::new(
            SetupReason::NotFound,
            resolution.probed.clone(),
        ));
    }
    match api_version {
        Some(version) if version < MIN_API_VERSION => Some(SetupState::new(
            SetupReason::TooOld,
            resolution.probed.clone(),
        )),
        _ => None,
    }
}

#[cfg(windows)]
const WINDOWS_CLI_NAMES: [&str; 2] = ["kyberdash.cmd", "kyberdash.exe"];

#[cfg(windows)]
const CLAUDE_NAMES: [&str; 2] = ["claude.cmd", "claude.exe"];
#[cfg(not(windows))]
const CLAUDE_NAMES: [&str; 1] = ["claude"];

/// Alphanumerics plus `._/-~` and space, with `\`, `:`, `(`, `)` also allowed on Windows
/// so a user-supplied `KYBERDASH_BIN` path like `C:\Users\...\kyberdash.cmd` is accepted.
///
/// `~` is here because Windows substitutes 8.3 short names for any path component
/// over eight characters, so ordinary paths arrive as `C:\Users\RUNNER~1\...` —
/// and a tilde is a legal directory character on every platform besides. It is
/// shell syntax only to a shell, and this never reaches one: the caller spawns a
/// direct argv (we never invoke `sh -c`), and `parse_env_bin` additionally
/// requires the result to be an absolute path to an existing file.
fn is_safe_arg(value: &str) -> bool {
    !value.is_empty()
        && value.chars().all(|c| {
            c.is_ascii_alphanumeric()
                || matches!(c, '.' | '_' | '/' | '-' | '~' | ' ')
                || (cfg!(windows) && matches!(c, '\\' | ':' | '(' | ')'))
        })
}

#[derive(Clone, Debug)]
pub struct KyberdashCli {
    program: String,
    extra_args: Vec<String>,
}

/// What the setup screen needs to know about the CLI on this machine.
#[derive(Clone, Debug, Serialize)]
pub struct CliStatus {
    pub found: bool,
    pub program: String,
    pub version: Option<String>,
    pub min_version: String,
    pub compatible: bool,
    pub error: Option<String>,
}

/// Why the tray is showing setup instead of a report (R6.7).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum SetupReason {
    NotFound,
    TooOld,
}

/// The `setup` arm of the design's `ViewState`. `probed` names the locations
/// that were looked in, in order, so the user can see which one to fix.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SetupState {
    pub probed: Vec<String>,
    pub remedy: String,
    pub reason: SetupReason,
}

impl SetupState {
    pub fn new(reason: SetupReason, probed: Vec<String>) -> Self {
        // Both commands are the ones the README documents; a remedy that names
        // a command the user does not have is worse than none.
        let remedy = match reason {
            SetupReason::NotFound => {
                "Install kyberdash: curl -fsSL \
                 https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh"
            }
            SetupReason::TooOld => "Update kyberdash: kyber-weave update",
        };
        SetupState {
            probed,
            remedy: remedy.to_string(),
            reason,
        }
    }
}

/// The locations the tray looks in, in the order Requirement 6.6 fixes.
///
/// These are inputs rather than reads of the real environment because the
/// process environment is shared by every test in the binary: a test that set
/// `KYBERDASH_BIN` would race any other test resolving the CLI.
#[derive(Clone, Debug, Default)]
pub struct CliSources {
    /// `KYBERDASH_BIN`, honoured only when it passes the argument allowlist.
    pub env_bin: Option<String>,
    /// `kyberdashPath` as `kyberdash menubar` recorded it in `~/.kyberdash/tray.json`.
    ///
    /// Second because a GUI launched at login inherits neither the shell's
    /// `PATH` nor `KYBER_WEAVE_INSTALL_DIR`, so the installer's own record is
    /// worth more than either.
    pub recorded: Option<PathBuf>,
    /// `install.sh`'s default prefix, `~/.local/bin`.
    pub install_dir: Option<PathBuf>,
    /// `PATH`, plus the package-manager prefixes `extra_search_dirs` knows.
    pub path_dirs: Vec<PathBuf>,
}

/// Where the CLI was found, and everywhere that was tried getting there.
#[derive(Clone, Debug)]
pub struct Resolution {
    pub cli: Option<KyberdashCli>,
    pub probed: Vec<String>,
}

/// Walks the sources in order, recording each one it looked at. The first hit
/// wins; `probed` is what the setup state shows when none does.
pub fn resolve_from(sources: &CliSources) -> Resolution {
    let mut probed: Vec<String> = Vec::new();

    if let Some(raw) = sources.env_bin.as_deref().filter(|v| !v.trim().is_empty()) {
        probed.push(format!("KYBERDASH_BIN={raw}"));
        match parse_env_bin(raw) {
            Some(cli) => {
                return Resolution {
                    cli: Some(cli),
                    probed,
                }
            }
            None => eprintln!(
                "kyberdash-tray: refusing unsafe KYBERDASH_BIN; continuing with the other locations"
            ),
        }
    }

    for candidate in [sources.recorded.as_ref(), sources.install_dir.as_ref()]
        .into_iter()
        .flatten()
    {
        // An install dir is a directory; a recorded path is the binary itself.
        let path = if candidate.is_dir() {
            candidate.join(default_program_name())
        } else {
            candidate.clone()
        };
        probed.push(path.display().to_string());
        if path.is_absolute() && path.is_file() {
            return Resolution {
                cli: Some(KyberdashCli {
                    program: path.to_string_lossy().into_owned(),
                    extra_args: vec![],
                }),
                probed,
            };
        }
    }

    probed.push("PATH".to_string());
    let names = candidate_names();
    if let Some(found) = find_in_dirs(&sources.path_dirs, &names) {
        return Resolution {
            cli: Some(KyberdashCli {
                program: found,
                extra_args: vec![],
            }),
            probed,
        };
    }

    Resolution { cli: None, probed }
}

/// `KYBERDASH_BIN` is either one path (which may hold spaces, as under Program
/// Files) or a program followed by leading arguments. Every token has to pass
/// the allowlist, and the program itself has to be an absolute path to a file
/// that exists: Requirement 6.6 spawns only a validated absolute path, so a
/// bare name resolved by the OS loader is not enough, and neither is a path
/// that is not there yet.
fn parse_env_bin(raw: &str) -> Option<KyberdashCli> {
    let spawnable = |program: &str| {
        let path = std::path::Path::new(program);
        path.is_absolute() && path.is_file()
    };

    if is_safe_arg(raw) && spawnable(raw) {
        return Some(KyberdashCli {
            program: raw.to_string(),
            extra_args: vec![],
        });
    }

    let parts: Vec<String> = raw.split_whitespace().map(String::from).collect();
    if !parts.iter().all(|p| is_safe_arg(p)) {
        return None;
    }
    let (first, rest) = parts.split_first()?;
    if !spawnable(first) {
        return None;
    }
    Some(KyberdashCli {
        program: first.clone(),
        extra_args: rest.to_vec(),
    })
}

impl KyberdashCli {
    /// Resolves against the real environment. The order, and everything that
    /// makes it testable, lives in [`resolve_from`].
    pub fn resolve() -> Self {
        Self::resolve_detailed()
            .cli
            .unwrap_or_else(|| KyberdashCli {
                // Nothing was found, so the setup state is what the user will see.
                // Keeping the bare name here means a later PATH change still works
                // without a restart.
                program: default_program_name(),
                extra_args: vec![],
            })
    }

    /// The same resolution, with the probed locations Requirement 6.7 shows.
    pub fn resolve_detailed() -> Resolution {
        resolve_from(&live_sources())
    }

    pub fn program(&self) -> &str {
        &self.program
    }

    /// Validated leading arguments from `KYBERDASH_BIN`.  The runtime carries
    /// them to every direct process spawn without re-tokenising or routing
    /// through a shell.
    pub fn extra_args(&self) -> &[String] {
        &self.extra_args
    }

    /// Runs `kyberdash --version` and reports whether the CLI is present and new enough.
    pub async fn status(&self) -> CliStatus {
        let min_version = format!(
            "{}.{}.{}",
            MIN_CLI_VERSION.0, MIN_CLI_VERSION.1, MIN_CLI_VERSION.2
        );
        let mut status = CliStatus {
            found: false,
            program: self.program.clone(),
            version: None,
            min_version,
            compatible: false,
            error: None,
        };
        match self.run_capture(&["--version"], VERSION_TIMEOUT_SECS).await {
            Ok(out) => {
                let version = out.trim().to_string();
                status.found = true;
                status.compatible = parse_version(&version)
                    .map(|v| v >= MIN_CLI_VERSION)
                    .unwrap_or(false);
                status.version = Some(version);
            }
            Err(err) => {
                status.error = Some(err.to_string());
            }
        }
        status
    }

    async fn run_capture(&self, args: &[&str], timeout_secs: u64) -> Result<String> {
        let mut full_args = self.extra_args.clone();
        full_args.extend(args.iter().map(|s| s.to_string()));

        let mut cmd = Command::new(&self.program);
        cmd.args(&full_args)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        #[cfg(windows)]
        {
            const CREATE_NO_WINDOW: u32 = 0x08000000;
            cmd.creation_flags(CREATE_NO_WINDOW);
        }
        let mut child = cmd.spawn().map_err(|err| {
            anyhow!(
                "KyberDash CLI not found ({}). Install it with the kyber-weave installer.",
                spawn_error_summary(&self.program, &err)
            )
        })?;

        let mut stdout = child.stdout.take().ok_or_else(|| anyhow!("no stdout"))?;
        let mut stderr = child.stderr.take().ok_or_else(|| anyhow!("no stderr"))?;

        let stdout_task = tokio::spawn(async move {
            let mut buf = Vec::with_capacity(64 * 1024);
            let mut limited = (&mut stdout).take(MAX_PAYLOAD_BYTES as u64);
            limited.read_to_end(&mut buf).await.ok();
            buf
        });
        let stderr_task = tokio::spawn(async move {
            let mut buf = Vec::with_capacity(4 * 1024);
            let mut limited = (&mut stderr).take(MAX_STDERR_BYTES as u64);
            limited.read_to_end(&mut buf).await.ok();
            buf
        });

        let status = timeout(Duration::from_secs(timeout_secs), child.wait())
            .await
            .map_err(|_| anyhow!("kyberdash CLI timed out after {}s", timeout_secs))??;

        let stdout_bytes = stdout_task.await.unwrap_or_default();
        let stderr_bytes = stderr_task.await.unwrap_or_default();

        if !status.success() {
            let msg = String::from_utf8_lossy(&stderr_bytes);
            bail!("kyberdash CLI exited {}: {}", status, msg.trim());
        }
        Ok(String::from_utf8_lossy(&stdout_bytes).into_owned())
    }
}

fn spawn_error_summary(program: &str, err: &std::io::Error) -> String {
    match err.kind() {
        std::io::ErrorKind::NotFound => format!("{} is not on PATH", program),
        _ => format!("{}: {}", program, err),
    }
}

fn default_program_name() -> String {
    #[cfg(windows)]
    {
        "kyberdash.cmd".to_string()
    }
    #[cfg(not(windows))]
    {
        "kyberdash".to_string()
    }
}

/// Parses "0.7.3" or "kyberdash 0.7.3" into a comparable tuple.
pub fn parse_version(text: &str) -> Option<(u32, u32, u32)> {
    let token = text.split_whitespace().find(|t| {
        t.chars()
            .next()
            .map(|c| c.is_ascii_digit())
            .unwrap_or(false)
    })?;
    let mut parts = token.split('.').map(|p| {
        p.chars()
            .take_while(|c| c.is_ascii_digit())
            .collect::<String>()
            .parse::<u32>()
            .ok()
    });
    Some((
        parts.next()??,
        parts.next()??,
        parts.next().flatten().unwrap_or(0),
    ))
}

/// The real environment's answer to each of [`CliSources`].
///
/// A tray app is often launched from Explorer or at login, before (or long
/// after) installing kyberdash changed the user's PATH, so the PATH list also
/// carries the live registry PATH on Windows and the standard npm / node
/// prefixes.
fn live_sources() -> CliSources {
    let home = dirs::home_dir();
    let mut path_dirs: Vec<PathBuf> = Vec::new();
    if let Some(path) = env::var_os("PATH") {
        path_dirs.extend(env::split_paths(&path));
    }
    path_dirs.extend(extra_search_dirs());

    CliSources {
        env_bin: env::var("KYBERDASH_BIN").ok(),
        recorded: home
            .as_ref()
            .and_then(|h| recorded_cli_path(&tray_config_path(h))),
        install_dir: home.as_ref().map(|h| h.join(".local").join("bin")),
        path_dirs,
    }
}

/// Where `kyberdash menubar` records what it installed.
fn tray_config_path(home: &std::path::Path) -> PathBuf {
    home.join(".kyberdash").join("tray.json")
}

/// Reads `kyberdashPath` out of `tray.json`.
///
/// A missing, unreadable or malformed file is simply "not recorded": this runs
/// at startup on every machine, and a hand-edited config must not stop the tray
/// from falling through to the locations that still work.
fn recorded_cli_path(config: &std::path::Path) -> Option<PathBuf> {
    let text = std::fs::read_to_string(config).ok()?;
    let value: Value = serde_json::from_str(&text).ok()?;
    let recorded = value.get("kyberdashPath")?.as_str()?;
    if recorded.trim().is_empty() {
        return None;
    }
    Some(PathBuf::from(recorded))
}

/// Second lookup for the Windows terminal spawn, which runs with whatever
/// `cli.program` was resolved to at startup and re-checks the default name
/// against the live PATH. `resolve_from` is the startup path; this is not.
#[cfg(target_os = "windows")]
fn locate_cli() -> Option<String> {
    find_in_search_dirs(&candidate_names())
}

/// Same search for Claude Code's own binary, so "Connect Claude" spawns an absolute path
/// instead of letting the console shell resolve a bare `claude`.
fn locate_claude() -> Option<String> {
    find_in_search_dirs(&CLAUDE_NAMES)
}

fn find_in_search_dirs(names: &[&str]) -> Option<String> {
    let mut dirs: Vec<PathBuf> = Vec::new();
    if let Some(path) = env::var_os("PATH") {
        dirs.extend(env::split_paths(&path));
    }
    dirs.extend(extra_search_dirs());
    find_in_dirs(&dirs, names)
}

/// The absolute-only filter is the security boundary, so it lives here where every search
/// goes through it. `env::split_paths` yields an empty `PathBuf` for `;;` or a trailing `;`,
/// and the registry PATH can hold relative entries too; `PathBuf::from("").join("kyberdash.cmd")`
/// resolves against the current directory, which for a tray app launched at login is
/// whatever Explorer handed it. A binary planted there must never win.
fn find_in_dirs(dirs: &[PathBuf], names: &[&str]) -> Option<String> {
    for dir in dirs.iter().filter(|d| d.is_absolute()) {
        for name in names {
            let candidate = dir.join(name);
            if candidate.is_file() {
                return Some(candidate.to_string_lossy().into_owned());
            }
        }
    }
    None
}

fn candidate_names() -> Vec<&'static str> {
    #[cfg(windows)]
    {
        WINDOWS_CLI_NAMES.to_vec()
    }
    #[cfg(not(windows))]
    {
        vec!["kyberdash"]
    }
}

#[cfg(windows)]
fn extra_search_dirs() -> Vec<PathBuf> {
    let mut out = Vec::new();
    for var in [
        "APPDATA",
        "LOCALAPPDATA",
        "ProgramFiles",
        "ProgramFiles(x86)",
    ] {
        if let Some(base) = env::var_os(var).map(PathBuf::from) {
            match var {
                "APPDATA" => out.push(base.join("npm")),
                "LOCALAPPDATA" => {
                    out.push(base.join("Programs").join("nodejs"));
                    out.push(base.join("pnpm"));
                    out.push(base.join("Volta").join("bin"));
                    out.push(base.join("fnm_multishells"));
                }
                _ => out.push(base.join("nodejs")),
            }
        }
    }
    if let Some(home) = dirs::home_dir() {
        out.push(home.join("scoop").join("shims"));
        out.push(home.join(".bun").join("bin"));
    }
    out.extend(registry_path_dirs());
    out
}

#[cfg(not(windows))]
fn extra_search_dirs() -> Vec<PathBuf> {
    let mut out = vec![
        PathBuf::from("/opt/homebrew/bin"),
        PathBuf::from("/usr/local/bin"),
    ];
    if let Some(home) = dirs::home_dir() {
        out.push(home.join(".npm-global").join("bin"));
        out.push(home.join(".local").join("bin"));
    }
    out
}

/// Windows' `CreateProcess` searches the current directory before `PATH`, so spawning
/// `reg` or `cmd` by bare name lets anything dropped next to the app impersonate a system
/// tool -- and the tray badge re-runs `reg query` every refresh. Always spawn the real one
/// out of `%SystemRoot%\System32`, falling back to the documented default when the
/// environment variable is missing or relative.
#[cfg(windows)]
pub fn system32_path(exe: &str) -> PathBuf {
    let root = env::var_os("SystemRoot")
        .map(PathBuf::from)
        .filter(|p| p.is_absolute())
        .unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
    root.join("System32").join(exe)
}

/// `system32_path` plus the CREATE_NO_WINDOW flag every one of these callers wants.
#[cfg(windows)]
pub fn system_command(exe: &str) -> std::process::Command {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x08000000;
    let mut cmd = std::process::Command::new(system32_path(exe));
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

/// Reads the user and machine PATH values from the registry via `reg.exe` so a PATH edit
/// made after this process started (npm install adds `%APPDATA%\npm`) is still honoured.
#[cfg(windows)]
fn registry_path_dirs() -> Vec<PathBuf> {
    let mut out = Vec::new();
    let keys = [
        r"HKCU\Environment",
        r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
    ];
    for key in keys {
        let output = system_command("reg.exe")
            .args(["query", key, "/v", "Path"])
            .output();
        let Ok(output) = output else { continue };
        let text = String::from_utf8_lossy(&output.stdout);
        for line in text.lines() {
            let trimmed = line.trim();
            if !trimmed.starts_with("Path") {
                continue;
            }
            let Some(idx) = trimmed.find("REG_") else {
                continue;
            };
            let rest = &trimmed[idx..];
            let Some(space) = rest.find(char::is_whitespace) else {
                continue;
            };
            let value = rest[space..].trim();
            for part in value.split(';') {
                let expanded = expand_env(part.trim());
                if !expanded.is_empty() {
                    out.push(PathBuf::from(expanded));
                }
            }
        }
    }
    out
}

#[cfg(windows)]
fn expand_env(value: &str) -> String {
    let mut result = String::with_capacity(value.len());
    let mut rest = value;
    while let Some(start) = rest.find('%') {
        result.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        match after.find('%') {
            Some(end) => {
                let name = &after[..end];
                match env::var(name) {
                    Ok(v) => result.push_str(&v),
                    Err(_) => {
                        result.push('%');
                        result.push_str(name);
                        result.push('%');
                    }
                }
                rest = &after[end + 1..];
            }
            None => {
                result.push_str(&rest[start..]);
                rest = "";
            }
        }
    }
    result.push_str(rest);
    result
}

/// Runs a kyberdash subcommand in the user's terminal emulator so they can see the output.
/// Linux: tries `x-terminal-emulator`, `gnome-terminal`, `konsole`, then falls back to a
/// detached headless spawn. Windows: opens a console via `cmd /C start`. Never
/// interpolates through a shell -- argv throughout.
pub fn spawn_in_terminal(app: &AppHandle, subcommand: &[&str]) -> Result<()> {
    let cli = KyberdashCli::resolve();
    spawn_program_in_terminal(app, &cli, subcommand)
}

/// The Plan view's "Connect Claude" runs Claude Code's own login flow, not kyberdash. The
/// binary is located up front rather than handed to the console shell as a bare name, so
/// the same absolute-directory rule that protects the kyberdash lookup applies here too.
pub fn spawn_claude_login(app: &AppHandle) -> Result<()> {
    let program = locate_claude().ok_or_else(|| {
        anyhow!("Claude Code was not found on this machine. Install it, then try again.")
    })?;
    let cli = KyberdashCli {
        program,
        extra_args: vec![],
    };
    spawn_program_in_terminal(app, &cli, &["login"])
}

fn spawn_program_in_terminal(
    _app: &AppHandle,
    cli: &KyberdashCli,
    subcommand: &[&str],
) -> Result<()> {
    if !subcommand.iter().all(|s| is_safe_arg(s)) {
        bail!("unsafe subcommand argument");
    }

    #[cfg(target_os = "linux")]
    {
        let mut command_parts: Vec<String> = vec![cli.program.clone()];
        command_parts.extend(cli.extra_args.clone());
        command_parts.extend(subcommand.iter().map(|s| s.to_string()));
        // Terminal emulators take the command as one string that a shell then parses
        // (gnome-terminal explicitly hands it to `bash -lc`). `cli.program` reaches here
        // from PATH resolution, not only from the allowlisted KYBERDASH_BIN, so re-check
        // every part before joining; anything a shell could reinterpret skips the terminal
        // and goes through the argv-only detached spawn below.
        if command_parts.iter().all(|p| is_safe_arg(p)) {
            let composite = command_parts.join(" ");
            let terminals: [&[&str]; 4] = [
                &["x-terminal-emulator", "-e"],
                &["gnome-terminal", "--", "bash", "-lc"],
                &["konsole", "-e"],
                &["xterm", "-e"],
            ];
            for term in &terminals {
                let program = term[0];
                let extras = &term[1..];
                if which::which(program).is_ok() {
                    let mut cmd = std::process::Command::new(program);
                    cmd.args(extras);
                    cmd.arg(&composite);
                    cmd.spawn()
                        .with_context(|| format!("failed to launch {}", program))?;
                    return Ok(());
                }
            }
        }
        // Fallback: run detached, output lost -- better than silently doing nothing.
        std::process::Command::new(&cli.program)
            .args(&cli.extra_args)
            .args(subcommand)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .with_context(|| "no terminal emulator found, detached spawn also failed")?;
    }

    #[cfg(target_os = "windows")]
    {
        // `start` treats the first quoted argument as the window title, so we pass an
        // explicit empty title. `/K` keeps the console open for non-interactive commands
        // (export) so the user can read where the file went; the TUI (report/optimize)
        // owns the window until the user quits it either way.
        // Only the unresolved default name is worth a second lookup; anything else is
        // either already absolute or a KYBERDASH_BIN the user chose.
        let program = if cli.program == default_program_name() {
            locate_cli().unwrap_or_else(|| cli.program.clone())
        } else {
            cli.program.clone()
        };
        let cmd_exe = system32_path("cmd.exe");
        let mut cmd = system_command("cmd.exe");
        cmd.arg("/C")
            .arg("start")
            .arg("")
            .arg(&cmd_exe)
            .arg("/K")
            .arg(&program);
        for a in &cli.extra_args {
            cmd.arg(a);
        }
        for a in subcommand {
            cmd.arg(a);
        }
        cmd.spawn().with_context(|| "failed to open cmd.exe")?;
    }

    #[cfg(target_os = "macos")]
    {
        // The tray is an accessory app, so there is no console to attach; spawn the CLI directly.
        std::process::Command::new(&cli.program)
            .args(&cli.extra_args)
            .args(subcommand)
            .spawn()
            .with_context(|| format!("failed to spawn {}", cli.program))?;
    }

    Ok(())
}

/// Minimal dependency: we only use `which` inside spawn_in_terminal on Linux. Vendored here
/// so the crate graph stays tiny. Gated so the unused-function warning doesn't fire on Mac
/// or Windows builds.
#[cfg(target_os = "linux")]
mod which {
    use std::env;
    use std::path::PathBuf;

    pub fn which(program: &str) -> Result<PathBuf, ()> {
        let path = env::var_os("PATH").ok_or(())?;
        let dirs: Vec<PathBuf> = env::split_paths(&path).collect();
        super::find_in_dirs(&dirs, &[program])
            .map(PathBuf::from)
            .ok_or(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A scratch directory holding a file that looks like an installed CLI.
    /// Dropped on scope exit, so a failing assertion cannot leak it.
    struct Scratch {
        root: PathBuf,
    }

    impl Scratch {
        fn new(tag: &str) -> Self {
            let root = std::env::temp_dir().join(format!(
                "kyberdash-tray-{tag}-{}-{:?}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            std::fs::create_dir_all(&root).unwrap();
            Scratch { root }
        }

        /// Writes an executable-looking file and returns its path.
        fn binary(&self, name: &str) -> PathBuf {
            let path = self.root.join(name);
            std::fs::write(&path, b"#!/bin/sh\n").unwrap();
            path
        }

        fn dir(&self, name: &str) -> PathBuf {
            let path = self.root.join(name);
            std::fs::create_dir_all(&path).unwrap();
            path
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            std::fs::remove_dir_all(&self.root).ok();
        }
    }

    /// Requirement 6.6 fixes the order. Each case removes the winner above it,
    /// so the next source has to take over.
    #[test]
    fn resolves_env_bin_then_recorded_then_install_dir_then_path() {
        let scratch = Scratch::new("order");
        let env_bin = scratch.binary("from-env");
        let recorded = scratch.binary("from-tray-json");
        let install_dir = scratch.dir("local-bin");
        let in_install = install_dir.join(default_program_name());
        std::fs::write(&in_install, b"#!/bin/sh\n").unwrap();
        let path_dir = scratch.dir("path-bin");
        std::fs::write(path_dir.join(default_program_name()), b"#!/bin/sh\n").unwrap();

        let all = CliSources {
            env_bin: Some(env_bin.to_string_lossy().into_owned()),
            recorded: Some(recorded.clone()),
            install_dir: Some(install_dir.clone()),
            path_dirs: vec![path_dir.clone()],
        };

        let winner = |sources: &CliSources| {
            resolve_from(sources)
                .cli
                .expect("a source should match")
                .program()
                .to_string()
        };

        assert_eq!(winner(&all), env_bin.to_string_lossy());

        let without_env = CliSources {
            env_bin: None,
            ..all.clone()
        };
        assert_eq!(winner(&without_env), recorded.to_string_lossy());

        let without_recorded = CliSources {
            recorded: None,
            ..without_env.clone()
        };
        assert_eq!(winner(&without_recorded), in_install.to_string_lossy());

        let path_only = CliSources {
            install_dir: None,
            ..without_recorded.clone()
        };
        assert_eq!(
            winner(&path_only),
            path_dir.join(default_program_name()).to_string_lossy()
        );
    }

    /// Requirement 6.7: the setup state names where it looked. The order is
    /// the order it looked in, so the user reads it as a trail.
    #[test]
    fn records_every_probed_location_when_nothing_is_found() {
        let scratch = Scratch::new("probed");
        let missing_recorded = scratch.root.join("recorded").join("kyberdash");
        let empty_install = scratch.dir("empty-local-bin");

        let resolution = resolve_from(&CliSources {
            env_bin: Some("/nonexistent/kyberdash".to_string()),
            recorded: Some(missing_recorded.clone()),
            install_dir: Some(empty_install.clone()),
            path_dirs: vec![scratch.dir("empty-path")],
        });

        assert!(resolution.cli.is_none());
        assert_eq!(
            resolution.probed,
            vec![
                "KYBERDASH_BIN=/nonexistent/kyberdash".to_string(),
                missing_recorded.display().to_string(),
                empty_install
                    .join(default_program_name())
                    .display()
                    .to_string(),
                "PATH".to_string(),
            ]
        );
    }

    /// A hostile `KYBERDASH_BIN` must not be spawned, and must not take the
    /// tray down with it: the remaining locations still get their turn.
    #[test]
    fn unsafe_env_bin_is_refused_but_falls_through() {
        let scratch = Scratch::new("unsafe");
        let install_dir = scratch.dir("local-bin");
        let good = install_dir.join(default_program_name());
        std::fs::write(&good, b"#!/bin/sh\n").unwrap();

        let resolution = resolve_from(&CliSources {
            env_bin: Some("kyberdash; rm -rf ~".to_string()),
            recorded: None,
            install_dir: Some(install_dir),
            path_dirs: vec![],
        });

        let program = resolution
            .cli
            .expect("install dir should win")
            .program()
            .to_string();
        assert_eq!(program, good.to_string_lossy());
        assert!(resolution.probed[0].starts_with("KYBERDASH_BIN="));
    }

    /// Requirement 6.6 spawns only a validated absolute path. A bare name would
    /// be resolved by the OS loader against a search order the tray does not
    /// control, and a path that is not there yet cannot be validated at all.
    #[test]
    fn env_bin_must_be_an_absolute_path_that_exists() {
        let scratch = Scratch::new("env-validation");
        let real = scratch.binary("kyberdash");

        assert!(parse_env_bin(&real.to_string_lossy()).is_some());
        assert!(parse_env_bin("kyberdash").is_none(), "bare name");
        assert!(parse_env_bin("./kyberdash").is_none(), "relative path");
        assert!(
            parse_env_bin("/nonexistent/kyberdash").is_none(),
            "absent file"
        );
        assert!(
            parse_env_bin(&scratch.root.to_string_lossy()).is_none(),
            "a directory is not a program"
        );
    }

    /// Windows hands out 8.3 short names for any path component over eight
    /// characters, so a real `KYBERDASH_BIN` routinely looks like
    /// `C:\Users\RUNNER~1\AppData\Local\Temp\kyberdash.exe`. The tilde is
    /// part of the filesystem path, not shell syntax — nothing here reaches a
    /// shell — and rejecting it told those users the CLI was missing when it was
    /// sitting at the path they gave. Reproduced on every platform, because a
    /// tilde is a legal directory character everywhere.
    #[test]
    fn env_bin_accepts_a_short_name_path() {
        let scratch = Scratch::new("short-name");
        let short = scratch.dir("RUNNER~1");
        let real = short.join("kyberdash");
        std::fs::write(&real, b"#!/bin/sh\n").unwrap();

        let cli = parse_env_bin(&real.to_string_lossy())
            .expect("an 8.3 short-name path is a path, not shell syntax");
        assert_eq!(cli.program(), real.to_string_lossy());

        let with_args = parse_env_bin(&format!("{} --no-color", real.to_string_lossy()))
            .expect("the program-plus-arguments form too");
        assert_eq!(with_args.extra_args, vec!["--no-color".to_string()]);
    }

    /// The program-plus-arguments form is still honoured, under the same rule.
    #[test]
    fn env_bin_keeps_leading_arguments() {
        let scratch = Scratch::new("env-args");
        let real = scratch.binary("kyberdash");

        let cli = parse_env_bin(&format!("{} --no-color", real.to_string_lossy()))
            .expect("an absolute program with arguments is spawnable");
        assert_eq!(cli.program(), real.to_string_lossy());
        assert_eq!(cli.extra_args, vec!["--no-color".to_string()]);
    }

    /// An empty or whitespace-only value is "unset", not a path to probe.
    #[test]
    fn blank_env_bin_is_not_probed() {
        let resolution = resolve_from(&CliSources {
            env_bin: Some("   ".to_string()),
            ..CliSources::default()
        });
        assert_eq!(resolution.probed, vec!["PATH".to_string()]);
    }

    /// The recorded path is the binary, not the directory holding it.
    #[test]
    fn recorded_path_is_read_from_tray_json() {
        let scratch = Scratch::new("tray-json");
        let config = scratch.root.join("tray.json");

        std::fs::write(
            &config,
            br#"{"kyberdashPath":"/opt/kyberdash/bin/kyberdash"}"#,
        )
        .unwrap();
        assert_eq!(
            recorded_cli_path(&config),
            Some(PathBuf::from("/opt/kyberdash/bin/kyberdash"))
        );

        // A hand-edited or half-written file must not stop the walk.
        for bad in [r#"{"kyberdashPath":""}"#, r#"{"other":1}"#, "{ not json"] {
            std::fs::write(&config, bad).unwrap();
            assert_eq!(recorded_cli_path(&config), None, "input: {bad}");
        }

        assert_eq!(recorded_cli_path(&scratch.root.join("absent.json")), None);
    }

    /// Requirement 6.7's two triggers, and the case that must not fire: a
    /// resolved CLI serving a contract the tray understands.
    #[test]
    fn setup_state_covers_missing_and_too_old_but_not_healthy() {
        let scratch = Scratch::new("gate");
        let install_dir = scratch.dir("local-bin");
        std::fs::write(install_dir.join(default_program_name()), b"#!/bin/sh\n").unwrap();

        let found = resolve_from(&CliSources {
            install_dir: Some(install_dir),
            ..CliSources::default()
        });
        let missing = resolve_from(&CliSources::default());

        assert_eq!(
            setup_state_for(&missing, None).map(|s| s.reason),
            Some(SetupReason::NotFound)
        );
        // Not found outranks the version: there is nothing to ask for a version.
        assert_eq!(
            setup_state_for(&missing, Some(MIN_API_VERSION)).map(|s| s.reason),
            Some(SetupReason::NotFound)
        );
        assert_eq!(
            setup_state_for(&found, Some(MIN_API_VERSION - 1)).map(|s| s.reason),
            Some(SetupReason::TooOld)
        );
        assert_eq!(setup_state_for(&found, Some(MIN_API_VERSION)), None);
        assert_eq!(setup_state_for(&found, Some(MIN_API_VERSION + 1)), None);
        // Nothing observed yet — the supervisor has not read a listening line.
        assert_eq!(setup_state_for(&found, None), None);
    }

    /// The probed trail survives into the setup state; it is the whole point of
    /// collecting it.
    #[test]
    fn setup_state_carries_the_probed_trail() {
        let missing = resolve_from(&CliSources {
            env_bin: Some("/nonexistent/kyberdash".to_string()),
            ..CliSources::default()
        });
        let state = setup_state_for(&missing, None).expect("nothing was found");
        assert_eq!(state.probed, missing.probed);
        assert!(state.probed.iter().any(|p| p.starts_with("KYBERDASH_BIN=")));
    }

    /// Requirement 6.7 asks for the command that installs or updates, and the
    /// two reasons are answered differently.
    #[test]
    fn setup_state_names_a_remedy_for_each_reason() {
        let probed = vec!["PATH".to_string()];

        let missing = SetupState::new(SetupReason::NotFound, probed.clone());
        assert_eq!(missing.reason, SetupReason::NotFound);
        assert!(missing.remedy.contains("install.sh"));
        assert_eq!(missing.probed, probed);

        let old = SetupState::new(SetupReason::TooOld, probed.clone());
        assert_eq!(old.reason, SetupReason::TooOld);
        assert!(old.remedy.contains("kyber-weave update"));
    }

    /// The UI reads these as the design's `ViewState.setup`, so the wire names
    /// are part of the contract.
    #[test]
    fn setup_state_serializes_with_the_design_s_names() {
        let json = serde_json::to_value(SetupState::new(
            SetupReason::TooOld,
            vec!["PATH".to_string()],
        ))
        .unwrap();

        assert_eq!(json["reason"], "too-old");
        assert_eq!(json["probed"][0], "PATH");
        assert!(json["remedy"].is_string());

        let not_found =
            serde_json::to_value(SetupState::new(SetupReason::NotFound, vec![])).unwrap();
        assert_eq!(not_found["reason"], "not-found");
    }

    /// The `;;` / trailing-`;` case: an empty PATH entry must not turn into a
    /// current-directory lookup, which is how a planted binary would win at login.
    #[test]
    fn find_in_dirs_skips_empty_and_relative_entries() {
        let dir = std::env::temp_dir();
        let name = "kyberdash-tray-locate-probe";
        let planted = dir.join(name);
        std::fs::write(&planted, b"probe").unwrap();

        // Empty and relative entries are ignored even though the file is reachable
        // through them once the process CWD is the temp dir.
        let unsafe_dirs = vec![PathBuf::from(""), PathBuf::from("."), PathBuf::from("..")];
        assert_eq!(find_in_dirs(&unsafe_dirs, &[name]), None);

        // The same name behind an absolute entry is found.
        let found =
            find_in_dirs(std::slice::from_ref(&dir), &[name]).expect("absolute entry should match");
        assert!(PathBuf::from(&found).is_absolute());
        assert!(found.ends_with(name));

        // An unsafe entry ahead of a good one cannot shadow it.
        let mixed = vec![PathBuf::from(""), dir.clone()];
        assert_eq!(find_in_dirs(&mixed, &[name]), Some(found));

        std::fs::remove_file(&planted).ok();
    }

    #[test]
    fn parse_version_reads_bare_and_prefixed_output() {
        assert_eq!(parse_version("0.9.9"), Some((0, 9, 9)));
        assert_eq!(parse_version("kyberdash 0.9.20\n"), Some((0, 9, 20)));
        assert_eq!(parse_version("1.0"), Some((1, 0, 0)));
        assert_eq!(parse_version("0.10.0-beta.1"), Some((0, 10, 0)));
        assert_eq!(parse_version("no version here"), None);
    }

    /// The gate is a plain tuple compare, so the only thing worth pinning is that the
    /// versions on either side of MIN_CLI_VERSION land on the right side of it.
    #[test]
    fn version_gate_rejects_only_older_clis() {
        assert_eq!(MIN_CLI_VERSION, (0, 9, 9));
        assert!(parse_version("0.9.8").unwrap() < MIN_CLI_VERSION);
        assert!(parse_version("0.9.9").unwrap() >= MIN_CLI_VERSION);
        assert!(parse_version("0.9.20").unwrap() >= MIN_CLI_VERSION);
        assert!(parse_version("0.10.0").unwrap() >= MIN_CLI_VERSION);
    }
}
