//! Concrete, bounded system adapters for [`crate::runtime`].
//!
//! These adapters receive only the validated binary and fixed argument lists
//! selected by the runtime.  They intentionally use direct argv spawning, no
//! shell, and are invoked exclusively from Tauri blocking workers.

use std::io::{BufRead, BufReader, Read};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::Duration;

use anyhow::{Context, Result};

use crate::api::{LoopbackFetcher, ReportFetcher, REQUEST_TIMEOUT};
use crate::receiver::{classify_reply, HealthProbe, Probe, ReceiverSpawner};
use crate::scheduler::{RefreshCancellation, RefreshRunner};
use crate::supervisor::{Clock, ServerProcess, Spawner};

/// A real `kyberdash web` process with a bounded stdout listening-line wait.
pub struct SystemServerProcess {
    child: Child,
    lines: Receiver<String>,
}

impl ServerProcess for SystemServerProcess {
    fn next_line(&mut self, within: Duration) -> Option<String> {
        self.lines.recv_timeout(within).ok()
    }

    fn kill_tree(&mut self) {
        // `kyberdash web` is a single direct child.  Killing and waiting here
        // keeps the owned process from becoming a zombie during quit/restart.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }

    fn is_running(&mut self) -> bool {
        self.child
            .try_wait()
            .map(|status| status.is_none())
            .unwrap_or(true)
    }
}

/// Direct process launcher shared by the server and receiver adapters.
fn spawn_command(
    program: &str,
    leading_args: &[String],
    args: &[&str],
    stdout: Stdio,
) -> std::io::Result<Child> {
    let mut command = Command::new(program);
    command
        .args(leading_args)
        .args(args)
        .stdin(Stdio::null())
        .stdout(stdout)
        .stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command.spawn()
}

pub struct SystemServerSpawner {
    leading_args: Vec<String>,
}

impl SystemServerSpawner {
    pub fn new(leading_args: Vec<String>) -> Self {
        Self { leading_args }
    }
}

impl Spawner for SystemServerSpawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> std::io::Result<Box<dyn ServerProcess>> {
        let mut child = spawn_command(program, &self.leading_args, args, Stdio::piped())?;
        let stdout = child.stdout.take().ok_or_else(|| {
            std::io::Error::new(
                std::io::ErrorKind::BrokenPipe,
                "kyberdash server has no stdout",
            )
        })?;
        let (sender, receiver) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                match line {
                    Ok(line) => {
                        if sender.send(line).is_err() {
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
        });
        Ok(Box::new(SystemServerProcess {
            child,
            lines: receiver,
        }))
    }
}

pub struct SystemRefreshRunner {
    leading_args: Vec<String>,
}

impl SystemRefreshRunner {
    pub fn new(leading_args: Vec<String>) -> Self {
        Self { leading_args }
    }
}

impl RefreshRunner for SystemRefreshRunner {
    fn run(&mut self, program: &str, args: &[&str]) -> (Option<i32>, String) {
        match spawn_command(program, &self.leading_args, args, Stdio::piped())
            .and_then(|child| child.wait_with_output())
        {
            Ok(output) => (
                output.status.code(),
                String::from_utf8_lossy(&output.stderr).into_owned(),
            ),
            Err(error) => (None, error.to_string()),
        }
    }

    fn run_cancellable(
        &mut self,
        program: &str,
        args: &[&str],
        cancellation: &RefreshCancellation,
    ) -> (Option<i32>, String) {
        let mut child = match spawn_command(program, &self.leading_args, args, Stdio::piped()) {
            Ok(child) => child,
            Err(error) => return (None, error.to_string()),
        };
        // Drain both pipes while polling.  `try_wait` reaps an exited child,
        // so calling `wait_with_output` afterwards would lose a successful
        // exit code; reader threads also prevent a verbose refresh from
        // deadlocking on a full pipe.
        let stderr_reader = child.stderr.take().map(|mut stderr| {
            thread::spawn(move || {
                let mut bytes = Vec::new();
                let _ = stderr.read_to_end(&mut bytes);
                bytes
            })
        });
        let stdout_reader = child.stdout.take().map(|mut stdout| {
            thread::spawn(move || {
                let mut bytes = Vec::new();
                let _ = stdout.read_to_end(&mut bytes);
                bytes
            })
        });

        loop {
            if cancellation.is_cancelled() {
                let _ = child.kill();
                let wait_error = child.wait().err();
                let stderr = stderr_reader
                    .and_then(|reader| reader.join().ok())
                    .unwrap_or_default();
                let _ = stdout_reader.and_then(|reader| reader.join().ok());
                let detail = String::from_utf8_lossy(&stderr);
                return (
                    None,
                    match wait_error {
                        Some(error) => format!("refresh cancelled: {error}; {detail}"),
                        None => format!("refresh cancelled: {detail}"),
                    },
                );
            }
            match child.try_wait() {
                Ok(Some(status)) => {
                    let stderr = stderr_reader
                        .and_then(|reader| reader.join().ok())
                        .unwrap_or_default();
                    let _ = stdout_reader
                        .and_then(|reader| reader.join().ok())
                        .unwrap_or_default();
                    return (status.code(), String::from_utf8_lossy(&stderr).into_owned());
                }
                Ok(None) => thread::sleep(Duration::from_millis(10)),
                Err(error) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    let _ = stderr_reader.and_then(|reader| reader.join().ok());
                    let _ = stdout_reader.and_then(|reader| reader.join().ok());
                    return (None, error.to_string());
                }
            }
        }
    }
}

pub struct SystemReportFetcher {
    inner: LoopbackFetcher,
}

impl SystemReportFetcher {
    pub fn new() -> Result<Self> {
        Ok(Self {
            inner: LoopbackFetcher::new().context("creating the loopback report client")?,
        })
    }
}

impl ReportFetcher for SystemReportFetcher {
    fn fetch(&mut self, url: &str) -> Result<serde_json::Value> {
        self.inner.fetch(url)
    }
}

pub struct SystemHealthProbe {
    client: reqwest::blocking::Client,
}

impl SystemHealthProbe {
    pub fn new() -> Result<Self> {
        Ok(Self {
            client: reqwest::blocking::Client::builder()
                .timeout(REQUEST_TIMEOUT)
                .redirect(reqwest::redirect::Policy::none())
                .no_proxy()
                .build()
                .context("creating the loopback receiver client")?,
        })
    }
}

impl HealthProbe for SystemHealthProbe {
    fn probe(&mut self, url: &str) -> Probe {
        let response = match self.client.get(url).send() {
            Ok(response) => response,
            Err(error) if error.is_connect() => return Probe::Refused,
            Err(_) => return Probe::Indeterminate,
        };
        let status = response.status().as_u16();
        let mut body = String::new();
        let mut limited = response.take(64 * 1024);
        if limited.read_to_string(&mut body).is_err() {
            return Probe::Indeterminate;
        }
        classify_reply(status, &body)
    }
}

pub struct SystemReceiverSpawner {
    leading_args: Vec<String>,
    children: Vec<Child>,
}

impl SystemReceiverSpawner {
    pub fn new(leading_args: Vec<String>) -> Self {
        Self {
            leading_args,
            children: Vec::new(),
        }
    }
}

impl ReceiverSpawner for SystemReceiverSpawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> std::io::Result<()> {
        self.children
            .retain_mut(|child| child.try_wait().ok().flatten().is_none());
        let child = spawn_command(program, &self.leading_args, args, Stdio::null())?;
        self.children.push(child);
        Ok(())
    }

    fn stop_all(&mut self) {
        for child in &mut self.children {
            let _ = child.kill();
            let _ = child.wait();
        }
        self.children.clear();
    }
}

#[derive(Default)]
pub struct ThreadClock;

impl Clock for ThreadClock {
    fn sleep(&mut self, duration: Duration) {
        if !duration.is_zero() {
            std::thread::sleep(duration);
        }
    }
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;

    #[test]
    fn cancellable_refresh_terminates_the_owned_child() {
        let mut runner = SystemRefreshRunner::new(Vec::new());
        let cancellation = RefreshCancellation::default();
        let cancel = cancellation.clone();
        let thread = thread::spawn(move || {
            thread::sleep(Duration::from_millis(30));
            cancel.cancel();
        });

        let (code, stderr) = runner.run_cancellable("/bin/sh", &["-c", "sleep 5"], &cancellation);
        thread.join().expect("cancellation thread");
        assert_eq!(code, None);
        assert!(stderr.contains("refresh cancelled"));
    }
}
