//! Reports on the OTLP receiver, and optionally hosts one.
//!
//! Requirement 10.6 puts the receiver's status in the data-health footer, as
//! one of reachable, not reachable, hosted by the tray, or unknown. 10.7 makes
//! hosting an opt-in that starts `kyberdash otel` with bounded backoff. 10.8 is
//! the sharp one: if port 4318 is already bound by something else, the tray
//! must not start a receiver, must say who has the port, and must not retry in
//! a loop.
//!
//! Telling "our receiver" from "a stranger on 4318" is what makes 10.8
//! possible, and `/healthz` answers it: the receiver identifies itself as
//! `kyberdash-otlp` in its body, so a reply that does not is someone else.

use std::time::Duration;

use serde::Serialize;

/// The OTLP/HTTP port the receiver binds, and the only one probed.
pub const RECEIVER_PORT: u16 = 4318;

/// The receiver's own health endpoint (`OTLP_HEALTHZ_PATH` in
/// src/otel/receiver.ts).
pub const HEALTHZ_PATH: &str = "/healthz";

/// The `service` value `/healthz` answers with. A 200 without it is a different
/// program that happens to serve that path.
pub const RECEIVER_SERVICE: &str = "kyberdash-otlp";

/// Arguments for a hosted receiver.
pub const RECEIVER_ARGS: [&str; 1] = ["otel"];

/// Restart backoff for a hosted receiver that exits (10.7).
pub const FIRST_BACKOFF: Duration = Duration::from_secs(1);
pub const MAX_BACKOFF: Duration = Duration::from_secs(60);

/// The receiver states of Requirement 10.6.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ReceiverStatus {
    /// A kyberdash receiver answered, started by someone else.
    Reachable,
    /// Nothing is listening on the port.
    NotReachable,
    /// A kyberdash receiver answered, and this tray started it.
    Hosted,
    /// Something is listening, but it is not a kyberdash receiver (10.8).
    PortHeldByOther,
    /// The probe could not decide.
    #[default]
    Unknown,
}

/// What a probe of `127.0.0.1:4318/healthz` saw.
///
/// Deliberately not a `Result`: "nothing is listening" is an answer, not a
/// failure, and the footer renders it differently from "the probe broke".
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Probe {
    /// 200, and the body identified a kyberdash receiver.
    KyberdashReceiver,
    /// Something answered, but it is not ours.
    OtherService,
    /// The connection was refused: the port is free.
    Refused,
    /// Timed out, or failed in a way that decides nothing.
    Indeterminate,
}

/// Reads the probe's outcome from an HTTP reply.
///
/// A 200 whose body does not name the receiver is another program on the port,
/// which is the case 10.8 exists for; treating it as "reachable" would have the
/// tray report a receiver that is not collecting anything.
pub fn classify_reply(status: u16, body: &str) -> Probe {
    if status != 200 {
        return Probe::OtherService;
    }
    let names_receiver = serde_json::from_str::<serde_json::Value>(body)
        .ok()
        .and_then(|v| {
            v.get("service")
                .and_then(|s| s.as_str())
                .map(|s| s == RECEIVER_SERVICE)
        })
        .unwrap_or(false);

    if names_receiver {
        Probe::KyberdashReceiver
    } else {
        Probe::OtherService
    }
}

/// The status a probe implies, given whether this tray started the receiver.
pub fn status_for(probe: &Probe, hosted_by_us: bool) -> ReceiverStatus {
    match probe {
        Probe::KyberdashReceiver if hosted_by_us => ReceiverStatus::Hosted,
        Probe::KyberdashReceiver => ReceiverStatus::Reachable,
        Probe::OtherService => ReceiverStatus::PortHeldByOther,
        Probe::Refused => ReceiverStatus::NotReachable,
        Probe::Indeterminate => ReceiverStatus::Unknown,
    }
}

/// Probes the receiver's health endpoint.
pub trait HealthProbe {
    fn probe(&mut self, url: &str) -> Probe;
}

/// Starts a hosted receiver.
pub trait ReceiverSpawner {
    fn spawn(&mut self, program: &str, args: &[&str]) -> std::io::Result<()>;

    /// Stops children this adapter owns.  The policy layer cannot assume that a
    /// receiver is a child of the web server, so shutdown is part of the
    /// adapter contract rather than an incidental process drop.
    fn stop_all(&mut self) {}
}

/// The probe URL. Loopback only, like everything else the tray opens (6.10).
pub fn healthz_url() -> String {
    format!("http://127.0.0.1:{RECEIVER_PORT}{HEALTHZ_PATH}")
}

/// Backoff for a hosted receiver that keeps exiting: 1, 2, 4 … capped.
pub fn restart_delay(consecutive_failures: u32) -> Duration {
    if consecutive_failures == 0 {
        return Duration::ZERO;
    }
    let doublings = consecutive_failures.saturating_sub(1).min(16);
    let seconds = FIRST_BACKOFF.as_secs().saturating_mul(1u64 << doublings);
    Duration::from_secs(seconds.min(MAX_BACKOFF.as_secs()))
}

pub struct Receiver {
    program: String,
    /// Requirement 10.7: off unless the user turns it on.
    hosting_enabled: bool,
    hosted_by_us: bool,
    failures: u32,
    status: ReceiverStatus,
    /// Latched by 10.8. Once another process is seen holding the port, hosting
    /// is not attempted again until something changes — a probe that finds the
    /// port free, or the user toggling the setting.
    port_held_by_other: bool,
}

impl Receiver {
    pub fn new(program: impl Into<String>) -> Self {
        Receiver {
            program: program.into(),
            hosting_enabled: false,
            hosted_by_us: false,
            failures: 0,
            status: ReceiverStatus::Unknown,
            port_held_by_other: false,
        }
    }

    pub fn status(&self) -> ReceiverStatus {
        self.status
    }

    pub fn hosting_enabled(&self) -> bool {
        self.hosting_enabled
    }

    /// Toggling the setting clears the latch: the user may have just stopped
    /// whatever held the port.
    pub fn set_hosting_enabled(&mut self, enabled: bool) {
        if enabled != self.hosting_enabled {
            self.port_held_by_other = false;
            self.failures = 0;
        }
        self.hosting_enabled = enabled;
        if !enabled {
            self.hosted_by_us = false;
        }
    }

    /// Probes, updates the status, and starts a receiver when that is both
    /// wanted and allowed.
    ///
    /// Returns the delay owed before the next hosting attempt, or `None` when
    /// no attempt is pending — which is the shape 10.8 needs, because "not
    /// retrying in a loop" has to be observable.
    pub fn poll<P: HealthProbe + ?Sized, S: ReceiverSpawner + ?Sized>(
        &mut self,
        prober: &mut P,
        spawner: &mut S,
    ) -> Option<Duration> {
        let probe = prober.probe(&healthz_url());

        if probe == Probe::OtherService {
            // 10.8: latch, so the next poll does not try again.
            self.port_held_by_other = true;
            self.hosted_by_us = false;
        } else if probe == Probe::Refused {
            // The port is free again; a previous holder has gone.
            self.port_held_by_other = false;
        }

        self.status = status_for(&probe, self.hosted_by_us);

        if !self.hosting_enabled || self.port_held_by_other {
            return None;
        }
        match probe {
            // Already answering: nothing to start.
            Probe::KyberdashReceiver => {
                self.failures = 0;
                None
            }
            Probe::Refused => {
                let delay = restart_delay(self.failures);
                match spawner.spawn(&self.program, &RECEIVER_ARGS) {
                    Ok(()) => {
                        self.hosted_by_us = true;
                        self.failures = 0;
                        Some(Duration::ZERO)
                    }
                    Err(_) => {
                        self.failures = self.failures.saturating_add(1);
                        Some(restart_delay(self.failures).max(delay))
                    }
                }
            }
            // Nothing decided, so nothing started: spawning against an
            // indeterminate probe is how two receivers end up on one port.
            Probe::Indeterminate | Probe::OtherService => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct FakeProbe {
        replies: Vec<Probe>,
        urls: Vec<String>,
    }

    impl FakeProbe {
        fn of(replies: Vec<Probe>) -> Self {
            FakeProbe {
                replies,
                urls: Vec::new(),
            }
        }
    }

    impl HealthProbe for FakeProbe {
        fn probe(&mut self, url: &str) -> Probe {
            self.urls.push(url.to_string());
            if self.replies.is_empty() {
                Probe::Indeterminate
            } else {
                self.replies.remove(0)
            }
        }
    }

    #[derive(Default)]
    struct FakeSpawner {
        calls: Vec<(String, Vec<String>)>,
        fail: bool,
    }

    impl ReceiverSpawner for FakeSpawner {
        fn spawn(&mut self, program: &str, args: &[&str]) -> std::io::Result<()> {
            self.calls.push((
                program.to_string(),
                args.iter().map(|a| a.to_string()).collect(),
            ));
            if self.fail {
                Err(std::io::Error::other("spawn refused"))
            } else {
                Ok(())
            }
        }
    }

    /// Requirement 10.6's four states, from what the probe saw.
    #[test]
    fn maps_each_probe_to_its_status() {
        assert_eq!(
            status_for(&Probe::KyberdashReceiver, false),
            ReceiverStatus::Reachable
        );
        assert_eq!(
            status_for(&Probe::KyberdashReceiver, true),
            ReceiverStatus::Hosted
        );
        assert_eq!(
            status_for(&Probe::Refused, false),
            ReceiverStatus::NotReachable
        );
        assert_eq!(
            status_for(&Probe::OtherService, false),
            ReceiverStatus::PortHeldByOther
        );
        assert_eq!(
            status_for(&Probe::Indeterminate, false),
            ReceiverStatus::Unknown
        );
    }

    /// The receiver names itself; a 200 that does not is a different program.
    #[test]
    fn only_a_reply_naming_the_receiver_counts_as_ours() {
        assert_eq!(
            classify_reply(200, r#"{"service":"kyberdash-otlp","version":"0.9.23"}"#),
            Probe::KyberdashReceiver
        );

        for stranger in [
            r#"{"service":"jaeger"}"#,
            r#"{"status":"ok"}"#,
            "OK",
            "",
            "<html>collector</html>",
        ] {
            assert_eq!(
                classify_reply(200, stranger),
                Probe::OtherService,
                "body: {stranger}"
            );
        }

        // A kyberdash-looking body behind an error status is still not a
        // healthy receiver.
        assert_eq!(
            classify_reply(503, r#"{"service":"kyberdash-otlp"}"#),
            Probe::OtherService
        );
        assert_eq!(classify_reply(404, ""), Probe::OtherService);
    }

    #[test]
    fn probes_loopback_on_the_otlp_port() {
        assert_eq!(healthz_url(), "http://127.0.0.1:4318/healthz");
    }

    /// Requirement 10.7: hosting is off until the user asks for it.
    #[test]
    fn hosting_is_off_by_default() {
        let mut prober = FakeProbe::of(vec![Probe::Refused]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");

        assert!(!receiver.hosting_enabled());
        receiver.poll(&mut prober, &mut spawner);

        assert!(spawner.calls.is_empty(), "nothing may be started");
        assert_eq!(receiver.status(), ReceiverStatus::NotReachable);
    }

    #[test]
    fn hosting_starts_kyberdash_otel_when_the_port_is_free() {
        let mut prober = FakeProbe::of(vec![Probe::Refused]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        receiver.poll(&mut prober, &mut spawner);

        assert_eq!(
            spawner.calls,
            vec![("kyberdash".to_string(), vec!["otel".to_string()])]
        );
    }

    /// Once hosted, a later probe reports it as ours rather than as a stranger's.
    #[test]
    fn a_hosted_receiver_reports_as_hosted() {
        let mut prober = FakeProbe::of(vec![Probe::Refused, Probe::KyberdashReceiver]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        receiver.poll(&mut prober, &mut spawner);
        receiver.poll(&mut prober, &mut spawner);

        assert_eq!(receiver.status(), ReceiverStatus::Hosted);
        assert_eq!(spawner.calls.len(), 1, "no second receiver");
    }

    /// Requirement 10.8: another process on 4318 means no attempt, and no
    /// retry loop.
    #[test]
    fn a_held_port_is_never_hosted_and_never_retried() {
        let mut prober = FakeProbe::of(vec![
            Probe::OtherService,
            Probe::OtherService,
            Probe::OtherService,
        ]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        for _ in 0..3 {
            assert_eq!(
                receiver.poll(&mut prober, &mut spawner),
                None,
                "no attempt may be pending while another process holds the port"
            );
        }

        assert!(spawner.calls.is_empty(), "nothing may be started");
        assert_eq!(receiver.status(), ReceiverStatus::PortHeldByOther);
    }

    /// The latch is not permanent: if the other process goes away, hosting
    /// resumes. Otherwise a stray collector at login would disable the feature
    /// until the tray restarted.
    #[test]
    fn the_port_held_latch_clears_when_the_port_frees_up() {
        let mut prober = FakeProbe::of(vec![Probe::OtherService, Probe::Refused]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        receiver.poll(&mut prober, &mut spawner);
        assert!(spawner.calls.is_empty());

        receiver.poll(&mut prober, &mut spawner);
        assert_eq!(spawner.calls.len(), 1, "the port is free now");
    }

    /// Turning the setting off and on again is the user's way of saying "try
    /// again", so it clears the latch too.
    #[test]
    fn toggling_the_setting_clears_the_latch() {
        let mut prober = FakeProbe::of(vec![Probe::OtherService, Probe::Refused]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);
        receiver.poll(&mut prober, &mut spawner);

        receiver.set_hosting_enabled(false);
        receiver.set_hosting_enabled(true);
        receiver.poll(&mut prober, &mut spawner);

        assert_eq!(spawner.calls.len(), 1);
    }

    /// An indeterminate probe decides nothing, so nothing is started: spawning
    /// on a timeout is how two receivers end up fighting over one port.
    #[test]
    fn an_indeterminate_probe_starts_nothing() {
        let mut prober = FakeProbe::of(vec![Probe::Indeterminate]);
        let mut spawner = FakeSpawner::default();
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        assert_eq!(receiver.poll(&mut prober, &mut spawner), None);
        assert!(spawner.calls.is_empty());
        assert_eq!(receiver.status(), ReceiverStatus::Unknown);
    }

    /// Requirement 10.7's bounded backoff, for a receiver that will not start.
    #[test]
    fn a_receiver_that_will_not_start_backs_off() {
        let mut prober = FakeProbe::of(vec![Probe::Refused, Probe::Refused, Probe::Refused]);
        let mut spawner = FakeSpawner {
            fail: true,
            ..Default::default()
        };
        let mut receiver = Receiver::new("kyberdash");
        receiver.set_hosting_enabled(true);

        assert_eq!(
            receiver.poll(&mut prober, &mut spawner),
            Some(Duration::from_secs(1))
        );
        assert_eq!(
            receiver.poll(&mut prober, &mut spawner),
            Some(Duration::from_secs(2))
        );
        assert_eq!(
            receiver.poll(&mut prober, &mut spawner),
            Some(Duration::from_secs(4))
        );
    }

    #[test]
    fn restart_backoff_doubles_and_caps() {
        assert_eq!(restart_delay(0), Duration::ZERO);
        assert_eq!(restart_delay(1), Duration::from_secs(1));
        assert_eq!(restart_delay(4), Duration::from_secs(8));
        assert_eq!(restart_delay(1_000_000), MAX_BACKOFF);
    }

    /// The footer reads this, so the wire names are part of the contract.
    #[test]
    fn serializes_with_the_design_s_names() {
        let names: Vec<String> = [
            ReceiverStatus::Reachable,
            ReceiverStatus::NotReachable,
            ReceiverStatus::Hosted,
            ReceiverStatus::PortHeldByOther,
            ReceiverStatus::Unknown,
        ]
        .iter()
        .map(|s| {
            serde_json::to_value(s)
                .unwrap()
                .as_str()
                .unwrap()
                .to_string()
        })
        .collect();

        assert_eq!(
            names,
            vec![
                "reachable",
                "not-reachable",
                "hosted",
                "port-held-by-other",
                "unknown"
            ]
        );
    }
}
