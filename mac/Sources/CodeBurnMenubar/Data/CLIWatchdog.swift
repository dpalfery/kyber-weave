import Foundation

/// Swift port of the desktop app's no-output watchdog (`app/electron/cli.ts`, #1096).
///
/// The CLI timeout is a NO-OUTPUT window, not a total-runtime cap: every byte the
/// child writes on stdout or stderr restarts it. Spawns set `CODEBURN_PROGRESS=1`,
/// under which the parser heartbeats every 10s for the whole duration of a parse
/// (`PROGRESS_KEEPALIVE_MS` in `src/parser.ts`), so a legitimately slow cold parse
/// on a large corpus is never killed while a genuinely wedged child still is.
///
/// The pure `verdict` exists so the arithmetic is testable without spawning a
/// child and waiting minutes for a real ceiling.
enum CLIWatchdog {
    /// Silence a live child cannot produce: 4.5x the CLI's 10s keepalive cadence.
    /// Matches `DEFAULT_TIMEOUT_MS` in app/electron/cli.ts.
    static let silenceSeconds: Double = 45
    /// Until this app has seen one successful payload the on-disk cache may be
    /// empty, and a full hydration has genuinely silent stretches before the
    /// first keepalive can be armed. Matches `DESKTOP_COLD_TIMEOUT_MS`. Finite
    /// on purpose: a child that never emits a byte still dies here, it just gets
    /// the cold budget to prove itself first.
    static let coldSilenceSeconds: Double = 10 * 60
    /// Backstop for the watchdog: a livelocked child that chatters forever
    /// without finishing is still reaped. Matches `MAX_RUNTIME_MS`.
    static let ceilingSeconds: Double = 15 * 60
    /// SIGTERM is catchable, so the CLI unlinks its own refresh lock before dying
    /// (`armSignalCleanup` in src/session-cache.ts and src/cache-refresh-lock.ts).
    /// Only a child that ignores it gets SIGKILL. Matches `KILL_GRACE_MS`.
    static let killGraceSeconds: Double = 5

    enum Verdict: Equatable {
        case wait
        case silent
        case ceiling
    }

    /// `now`, `startedAt` and `lastOutputAt` are seconds on one monotonic scale.
    static func verdict(now: Double,
                        startedAt: Double,
                        lastOutputAt: Double,
                        silenceSeconds: Double) -> Verdict {
        if now - lastOutputAt >= silenceSeconds { return .silent }
        if now - startedAt >= ceilingSeconds { return .ceiling }
        return .wait
    }

    /// The silence window for a request: the cold floor applies until this app
    /// has completed one payload, exactly as the electron client floors every
    /// request admitted before its resident child is warm.
    static func silenceWindow(warm: Bool) -> Double {
        warm ? silenceSeconds : max(silenceSeconds, coldSilenceSeconds)
    }

    /// Progress heartbeats share stderr with real diagnostics, and every read
    /// spawn now enables them, so they must never become the error message.
    static func withoutProgressLines(_ stderr: String) -> String {
        stderr
            .split(separator: "\n", omittingEmptySubsequences: false)
            .filter { !$0.hasPrefix(progressLinePrefix) }
            .joined(separator: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Wire marker for CLI scan-progress lines (`PROGRESS_LINE_PREFIX`, src/parser.ts).
    static let progressLinePrefix = "CODEBURN_PROGRESS "

    /// Environment for a spawn that should heartbeat through the watchdog.
    static func withProgressHeartbeat(_ environment: [String: String]?) -> [String: String] {
        var env = environment ?? ProcessInfo.processInfo.environment
        env["CODEBURN_PROGRESS"] = "1"
        return env
    }
}

/// Monotonic last-output marker shared by a process's two drain tasks and its
/// watchdog timer. `uptimeNanoseconds` is unaffected by wall-clock jumps.
final class OutputActivity: @unchecked Sendable {
    private let lock = NSLock()
    private var lastOutput: Double

    init() { lastOutput = OutputActivity.now() }

    static func now() -> Double {
        Double(DispatchTime.now().uptimeNanoseconds) / 1_000_000_000
    }

    func touch() {
        lock.lock()
        lastOutput = OutputActivity.now()
        lock.unlock()
    }

    var lastOutputAt: Double {
        lock.lock()
        defer { lock.unlock() }
        return lastOutput
    }
}
