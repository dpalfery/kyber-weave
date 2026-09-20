//! Owns the one `kyberdash web` server the tray reads from.
//!
//! Requirement 7.1 has the tray read `/api/kyber/*` from a server it started,
//! rather than spawning a CLI per poll, and 7.2 has it reuse that server for
//! its lifetime and restart it with bounded backoff when it exits. 7.3 says the
//! URL is whatever the server reports, because the preferred port may be taken.
//!
//! The decisions live in [`Supervisor`], which owns no process and no timer.
//! Spawning and sleeping arrive through [`Spawner`] and [`Clock`] so the
//! restart schedule can be tested in microseconds rather than minutes, and so a
//! test never depends on a port being free.

use std::time::Duration;

use serde::Serialize;

/// How long the server has to announce itself before the attempt is a failure.
pub const LISTENING_TIMEOUT: Duration = Duration::from_secs(5);

/// First restart delay; each consecutive failure doubles it.
pub const FIRST_BACKOFF: Duration = Duration::from_secs(1);

/// Ceiling for the doubling, so a server that never comes back is retried
/// once a minute rather than never.
pub const MAX_BACKOFF: Duration = Duration::from_secs(60);

/// Consecutive failures before the popover is told the data is stale.
pub const FAILURES_BEFORE_STALE: u32 = 5;

/// The server phases of the design's `ViewState`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ServerPhase {
    /// Spawned, no listening line read yet.
    Starting,
    /// Listening line read; the URL is good.
    Ready,
    /// Too many consecutive failures. Retries continue, but nothing on screen
    /// may be presented as current (7.4).
    Stale,
}

/// The machine-readable first line of `kyberdash web` (5.7).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Listening {
    pub url: String,
    pub pid: u32,
    pub version: String,
    pub api_version: u32,
}

/// Reads the listening line, and only that line.
///
/// The human banner follows on later lines, so anything that is not this event
/// is skipped rather than treated as a failure. A line claiming a non-loopback
/// URL is refused outright: Requirement 6.10 opens no connection except to
/// loopback, and this is where a hijacked or confused server would try.
pub fn parse_listening_line(line: &str) -> Option<Listening> {
    let value: serde_json::Value = serde_json::from_str(line.trim()).ok()?;
    if value.get("event")?.as_str()? != "kyberdash.web.listening" {
        return None;
    }

    let url = value.get("url")?.as_str()?.to_string();
    if !is_loopback_url(&url) {
        return None;
    }

    Some(Listening {
        url,
        pid: u32::try_from(value.get("pid")?.as_u64()?).ok()?,
        version: value.get("version")?.as_str()?.to_string(),
        api_version: u32::try_from(value.get("apiVersion")?.as_u64()?).ok()?,
    })
}

/// Only `127.0.0.1` over plain HTTP. Not `localhost`, which resolves through
/// whatever the host's name service says, and not `::1`, which the server does
/// not bind.
fn is_loopback_url(url: &str) -> bool {
    url.strip_prefix("http://127.0.0.1")
        .is_some_and(|rest| rest.is_empty() || rest.starts_with(':') || rest.starts_with('/'))
}

/// The delay before the nth consecutive restart: 1, 2, 4, 8 … capped at 60 s.
pub fn restart_delay(consecutive_failures: u32) -> Duration {
    if consecutive_failures == 0 {
        return Duration::ZERO;
    }
    // `checked_shl`-free: the shift is on u64 seconds and saturates at the cap
    // long before it could overflow, but a large failure count must not panic.
    let doublings = consecutive_failures.saturating_sub(1).min(16);
    let seconds = FIRST_BACKOFF.as_secs().saturating_mul(1u64 << doublings);
    Duration::from_secs(seconds.min(MAX_BACKOFF.as_secs()))
}

/// A running server, as much of one as the supervisor needs.
pub trait ServerProcess: Send {
    /// The next line the server wrote to stdout, or `None` when it exited or
    /// went quiet for longer than `within`.
    fn next_line(&mut self, within: Duration) -> Option<String>;

    /// Stops the server and everything it started (6.9).
    fn kill_tree(&mut self);
}

/// Starts server processes.
pub trait Spawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> std::io::Result<Box<dyn ServerProcess>>;
}

/// Waiting, so a test does not.
pub trait Clock {
    fn sleep(&mut self, duration: Duration);
}

/// What one supervised attempt did.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Attempt {
    /// The server announced itself.
    Listening(Listening),
    /// It did not, and this is how long until the next try.
    Failed { reason: String, retry_in: Duration },
}

/// The arguments the tray always starts the server with.
///
/// `--no-open` because the tray owns when a browser opens (8.9), and the port
/// is left at the CLI's default so a taken port falls back the same way it does
/// for a human — the tray uses whatever URL comes back (7.3).
pub const SERVER_ARGS: [&str; 2] = ["web", "--no-open"];

pub struct Supervisor {
    program: String,
    failures: u32,
    phase: ServerPhase,
    listening: Option<Listening>,
    process: Option<Box<dyn ServerProcess>>,
}

impl Supervisor {
    pub fn new(program: impl Into<String>) -> Self {
        Supervisor {
            program: program.into(),
            failures: 0,
            phase: ServerPhase::Starting,
            listening: None,
            process: None,
        }
    }

    pub fn phase(&self) -> ServerPhase {
        self.phase
    }

    pub fn url(&self) -> Option<&str> {
        self.listening.as_ref().map(|l| l.url.as_str())
    }

    pub fn api_version(&self) -> Option<u32> {
        self.listening.as_ref().map(|l| l.api_version)
    }

    pub fn consecutive_failures(&self) -> u32 {
        self.failures
    }

    /// Spawns the server and waits for its listening line.
    ///
    /// One attempt, so the caller owns the loop and a test can step it. The
    /// backoff for the *next* attempt is returned rather than slept here, but
    /// the delay owed by previous failures is slept first — that ordering is
    /// what makes the schedule observable.
    pub fn attempt<S: Spawner, C: Clock>(&mut self, spawner: &mut S, clock: &mut C) -> Attempt {
        clock.sleep(restart_delay(self.failures));

        // A previous process may still be running if it announced itself and
        // then stopped answering; never leave two servers holding ports.
        self.stop();
        self.phase = self.phase_for_start();

        let mut process = match spawner.spawn(&self.program, &SERVER_ARGS) {
            Ok(process) => process,
            Err(err) => return self.record_failure(format!("spawn failed: {err}")),
        };

        // 5.7 puts the event first, but a runtime warning on stdout would push
        // it down a line, and reading exactly one line would then miss a server
        // that is running perfectly well. Read until it turns up or the process
        // stops talking; `next_line` owns the deadline.
        let listening = loop {
            match process.next_line(LISTENING_TIMEOUT) {
                Some(line) => {
                    if let Some(listening) = parse_listening_line(&line) {
                        break listening;
                    }
                }
                None => {
                    process.kill_tree();
                    return self.record_failure("no listening line within 5s".to_string());
                }
            }
        };

        self.failures = 0;
        self.phase = ServerPhase::Ready;
        self.listening = Some(listening.clone());
        self.process = Some(process);
        Attempt::Listening(listening)
    }

    /// Requirement 6.9: quitting stops every process the tray started.
    pub fn stop(&mut self) {
        if let Some(mut process) = self.process.take() {
            process.kill_tree();
        }
    }

    /// Refresh now clears the stale phase and retries immediately, which is
    /// what makes the stale banner's action mean something.
    pub fn refresh_now(&mut self) {
        self.failures = 0;
        if self.phase == ServerPhase::Stale {
            self.phase = ServerPhase::Starting;
        }
    }

    /// A failed attempt keeps the last known URL: 7.4 keeps showing the last
    /// data, labelled, rather than blanking the popover.
    fn record_failure(&mut self, reason: String) -> Attempt {
        self.failures = self.failures.saturating_add(1);
        self.phase = if self.failures >= FAILURES_BEFORE_STALE {
            ServerPhase::Stale
        } else {
            ServerPhase::Starting
        };
        Attempt::Failed {
            reason,
            retry_in: restart_delay(self.failures),
        }
    }

    /// Stale survives a restart attempt; it clears only on a listening line or
    /// an explicit Refresh now.
    fn phase_for_start(&self) -> ServerPhase {
        match self.phase {
            ServerPhase::Stale => ServerPhase::Stale,
            _ => ServerPhase::Starting,
        }
    }
}

impl Drop for Supervisor {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Mutex};

    fn listening_line(port: u16) -> String {
        format!(
            r#"{{"event":"kyberdash.web.listening","url":"http://127.0.0.1:{port}","pid":4242,"version":"0.9.23","apiVersion":1}}"#
        )
    }

    #[derive(Default)]
    struct FakeProcess {
        lines: Vec<String>,
        killed: Arc<Mutex<bool>>,
    }

    impl ServerProcess for FakeProcess {
        fn next_line(&mut self, _within: Duration) -> Option<String> {
            if self.lines.is_empty() {
                None
            } else {
                Some(self.lines.remove(0))
            }
        }

        fn kill_tree(&mut self) {
            *self.killed.lock().unwrap() = true;
        }
    }

    /// Hands out a scripted process per spawn and records what it was asked for.
    #[derive(Default)]
    struct FakeSpawner {
        scripted: Vec<Vec<String>>,
        calls: Vec<(String, Vec<String>)>,
        killed: Arc<Mutex<bool>>,
        fail_spawn: bool,
    }

    impl Spawner for FakeSpawner {
        fn spawn(
            &mut self,
            program: &str,
            args: &[&str],
        ) -> std::io::Result<Box<dyn ServerProcess>> {
            self.calls.push((
                program.to_string(),
                args.iter().map(|a| a.to_string()).collect(),
            ));
            if self.fail_spawn {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::NotFound,
                    "no such file",
                ));
            }
            let lines = if self.scripted.is_empty() {
                Vec::new()
            } else {
                self.scripted.remove(0)
            };
            Ok(Box::new(FakeProcess {
                lines,
                killed: Arc::clone(&self.killed),
            }))
        }
    }

    #[derive(Default)]
    struct FakeClock {
        slept: Vec<Duration>,
    }

    impl Clock for FakeClock {
        fn sleep(&mut self, duration: Duration) {
            self.slept.push(duration);
        }
    }

    #[test]
    fn spawns_kyberdash_web_with_no_open() {
        let mut spawner = FakeSpawner {
            scripted: vec![vec![listening_line(4747)]],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("/usr/local/bin/kyberdash");

        supervisor.attempt(&mut spawner, &mut clock);

        assert_eq!(
            spawner.calls,
            vec![(
                "/usr/local/bin/kyberdash".to_string(),
                vec!["web".to_string(), "--no-open".to_string()]
            )]
        );
    }

    /// Requirement 7.3: the port may not be the preferred one, so the URL the
    /// server reports is the one that gets used.
    #[test]
    fn uses_the_url_the_server_reports() {
        let mut spawner = FakeSpawner {
            // Not 4747: the preferred port was taken.
            scripted: vec![vec![listening_line(51234)]],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        let attempt = supervisor.attempt(&mut spawner, &mut clock);

        assert!(matches!(attempt, Attempt::Listening(_)));
        assert_eq!(supervisor.url(), Some("http://127.0.0.1:51234"));
        assert_eq!(supervisor.api_version(), Some(1));
        assert_eq!(supervisor.phase(), ServerPhase::Ready);
    }

    /// A server that says nothing is a failed attempt, and its process is not
    /// left running.
    #[test]
    fn a_silent_server_fails_the_attempt_and_is_killed() {
        let killed = Arc::new(Mutex::new(false));
        let mut spawner = FakeSpawner {
            scripted: vec![vec![]],
            killed: Arc::clone(&killed),
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        let attempt = supervisor.attempt(&mut spawner, &mut clock);

        match attempt {
            Attempt::Failed { reason, retry_in } => {
                assert!(reason.contains("5s"), "reason was {reason}");
                assert_eq!(retry_in, Duration::from_secs(1));
            }
            other => panic!("expected a failure, got {other:?}"),
        }
        assert!(*killed.lock().unwrap(), "the silent process must be killed");
        assert_eq!(supervisor.url(), None);
    }

    /// The schedule of Requirement 7.2, read off the clock the supervisor slept on.
    #[test]
    fn restart_backoff_doubles_and_caps_at_a_minute() {
        assert_eq!(restart_delay(0), Duration::ZERO);
        assert_eq!(restart_delay(1), Duration::from_secs(1));
        assert_eq!(restart_delay(2), Duration::from_secs(2));
        assert_eq!(restart_delay(3), Duration::from_secs(4));
        assert_eq!(restart_delay(4), Duration::from_secs(8));
        assert_eq!(restart_delay(5), Duration::from_secs(16));
        assert_eq!(restart_delay(6), Duration::from_secs(32));
        // 64 would exceed the ceiling.
        assert_eq!(restart_delay(7), MAX_BACKOFF);
        assert_eq!(restart_delay(1_000_000), MAX_BACKOFF);
    }

    /// The delays are actually waited, in order, before each retry.
    #[test]
    fn each_retry_waits_its_turn_of_the_schedule() {
        let mut spawner = FakeSpawner {
            scripted: vec![vec![], vec![], vec![]],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        for _ in 0..3 {
            supervisor.attempt(&mut spawner, &mut clock);
        }

        // First attempt owes nothing; then 1s and 2s for the two failures behind it.
        assert_eq!(
            clock.slept,
            vec![
                Duration::ZERO,
                Duration::from_secs(1),
                Duration::from_secs(2)
            ]
        );
    }

    #[test]
    fn five_consecutive_failures_enter_the_stale_phase() {
        let mut spawner = FakeSpawner {
            scripted: vec![vec![]; FAILURES_BEFORE_STALE as usize],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        for attempt in 1..FAILURES_BEFORE_STALE {
            supervisor.attempt(&mut spawner, &mut clock);
            assert_eq!(
                supervisor.phase(),
                ServerPhase::Starting,
                "still starting after {attempt} failures"
            );
        }

        supervisor.attempt(&mut spawner, &mut clock);
        assert_eq!(supervisor.phase(), ServerPhase::Stale);
        assert_eq!(supervisor.consecutive_failures(), FAILURES_BEFORE_STALE);
    }

    /// Stale is not terminal: the server coming back clears it, which is why
    /// retries continue past the fifth failure.
    #[test]
    fn a_listening_line_clears_stale() {
        let mut scripted = vec![vec![]; FAILURES_BEFORE_STALE as usize];
        scripted.push(vec![listening_line(4747)]);
        let mut spawner = FakeSpawner {
            scripted,
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        for _ in 0..FAILURES_BEFORE_STALE {
            supervisor.attempt(&mut spawner, &mut clock);
        }
        assert_eq!(supervisor.phase(), ServerPhase::Stale);

        supervisor.attempt(&mut spawner, &mut clock);
        assert_eq!(supervisor.phase(), ServerPhase::Ready);
        assert_eq!(supervisor.consecutive_failures(), 0);
    }

    /// Refresh now is the stale banner's action: it drops the accumulated
    /// backoff so the next attempt is immediate.
    #[test]
    fn refresh_now_resets_the_backoff_and_the_phase() {
        let mut spawner = FakeSpawner {
            scripted: vec![vec![]; FAILURES_BEFORE_STALE as usize],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        for _ in 0..FAILURES_BEFORE_STALE {
            supervisor.attempt(&mut spawner, &mut clock);
        }

        supervisor.refresh_now();
        assert_eq!(supervisor.phase(), ServerPhase::Starting);
        assert_eq!(supervisor.consecutive_failures(), 0);
        assert_eq!(
            restart_delay(supervisor.consecutive_failures()),
            Duration::ZERO
        );
    }

    /// Requirement 6.9: quit stops the server the tray started.
    #[test]
    fn stop_kills_the_running_server() {
        let killed = Arc::new(Mutex::new(false));
        let mut spawner = FakeSpawner {
            scripted: vec![vec![listening_line(4747)]],
            killed: Arc::clone(&killed),
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        supervisor.attempt(&mut spawner, &mut clock);
        assert!(!*killed.lock().unwrap());

        supervisor.stop();
        assert!(*killed.lock().unwrap());
    }

    /// Restarting must not leave the previous server holding its port.
    #[test]
    fn a_restart_kills_the_previous_server_first() {
        let killed = Arc::new(Mutex::new(false));
        let mut spawner = FakeSpawner {
            scripted: vec![vec![listening_line(4747)], vec![listening_line(4748)]],
            killed: Arc::clone(&killed),
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        supervisor.attempt(&mut spawner, &mut clock);
        supervisor.attempt(&mut spawner, &mut clock);

        assert!(*killed.lock().unwrap());
        assert_eq!(supervisor.url(), Some("http://127.0.0.1:4748"));
    }

    #[test]
    fn a_spawn_error_is_a_failure_not_a_panic() {
        let mut spawner = FakeSpawner {
            fail_spawn: true,
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("/nowhere/kyberdash");

        match supervisor.attempt(&mut spawner, &mut clock) {
            Attempt::Failed { reason, .. } => assert!(reason.contains("spawn failed")),
            other => panic!("expected a failure, got {other:?}"),
        }
        assert_eq!(supervisor.consecutive_failures(), 1);
    }

    /// A warning ahead of the event line must not look like a dead server.
    #[test]
    fn finds_the_listening_line_behind_a_noisy_first_line() {
        let mut spawner = FakeSpawner {
            scripted: vec![vec![
                "(node:123) ExperimentalWarning: something".to_string(),
                listening_line(4747),
            ]],
            ..Default::default()
        };
        let mut clock = FakeClock::default();
        let mut supervisor = Supervisor::new("kyberdash");

        assert!(matches!(
            supervisor.attempt(&mut spawner, &mut clock),
            Attempt::Listening(_)
        ));
        assert_eq!(supervisor.url(), Some("http://127.0.0.1:4747"));
    }

    #[test]
    fn parses_the_listening_line_and_only_that_line() {
        let parsed = parse_listening_line(&listening_line(4747)).expect("the event line parses");
        assert_eq!(parsed.url, "http://127.0.0.1:4747");
        assert_eq!(parsed.pid, 4242);
        assert_eq!(parsed.version, "0.9.23");
        assert_eq!(parsed.api_version, 1);

        assert_eq!(parse_listening_line("  KyberDash dashboard at ..."), None);
        assert_eq!(parse_listening_line(r#"{"event":"something.else"}"#), None);
        assert_eq!(parse_listening_line(""), None);
    }

    /// Requirement 6.10 allows loopback and nothing else, and the URL arrives
    /// from another process's stdout.
    #[test]
    fn refuses_a_listening_line_pointing_off_loopback() {
        let off_box = r#"{"event":"kyberdash.web.listening","url":"http://10.0.0.5:4747","pid":1,"version":"0.9.23","apiVersion":1}"#;
        assert_eq!(parse_listening_line(off_box), None);

        // `localhost` resolves through the host's name service, so it is not
        // the same guarantee as the literal address.
        let by_name = r#"{"event":"kyberdash.web.listening","url":"http://localhost:4747","pid":1,"version":"0.9.23","apiVersion":1}"#;
        assert_eq!(parse_listening_line(by_name), None);

        // A prefix match is not enough: this host is not loopback.
        let lookalike = r#"{"event":"kyberdash.web.listening","url":"http://127.0.0.1.evil.test/","pid":1,"version":"0.9.23","apiVersion":1}"#;
        assert_eq!(parse_listening_line(lookalike), None);
    }
}
