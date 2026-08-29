import Foundation
import Testing
@testable import CodeBurnMenubar

@Suite("DataClient process")
struct DataClientProcessTests {
    @Test("status argv local omits scope")
    func statusSubcommandLocalOmitsScope() {
        let args = DataClient.statusSubcommand(
            period: .today,
            provider: .claude,
            includeOptimize: false,
            scope: .local
        )

        #expect(!args.contains("--scope"))
        #expect(value(after: "--provider", in: args) == "claude")
        #expect(args.contains("--no-optimize"))
    }

    @Test("status argv combined adds scope and forces provider all")
    func statusSubcommandCombinedAddsScopeAndForcesAllProvider() {
        let args = DataClient.statusSubcommand(
            period: .today,
            provider: .codex,
            includeOptimize: true,
            scope: .combined
        )

        #expect(value(after: "--scope", in: args) == "combined")
        #expect(value(after: "--provider", in: args) == "all")
        #expect(!args.contains("--no-optimize"))
    }

    @Test("status argv combined multi-day coerces to local")
    func statusSubcommandCombinedMultiDayCoercesToLocal() {
        let args = DataClient.statusSubcommand(
            period: .today,
            days: ["2026-06-01", "2026-06-02"],
            provider: .codex,
            includeOptimize: false,
            scope: .combined
        )

        #expect(!args.contains("--scope"))
        #expect(value(after: "--provider", in: args) == "codex")
        #expect(value(after: "--days", in: args) == "2026-06-01,2026-06-02")
    }

    @Test("status argv local includes Claude config source")
    func statusSubcommandLocalIncludesClaudeConfigSource() {
        let args = DataClient.statusSubcommand(
            period: .today,
            provider: .all,
            includeOptimize: false,
            scope: .local,
            claudeConfigSourceId: "claude-config:work"
        )

        #expect(value(after: "--claude-config-source", in: args) == "claude-config:work")
    }

    @Test("status argv combined omits Claude config source")
    func statusSubcommandCombinedOmitsClaudeConfigSource() {
        let args = DataClient.statusSubcommand(
            period: .today,
            provider: .all,
            includeOptimize: false,
            scope: .combined,
            claudeConfigSourceId: "claude-config:work"
        )

        #expect(value(after: "--scope", in: args) == "combined")
        #expect(!args.contains("--claude-config-source"))
    }

    @Test("status argv supports LingTai TUI provider")
    func statusSubcommandSupportsLingTaiTUI() {
        let args = DataClient.statusSubcommand(
            period: .month,
            provider: .lingtaiTui,
            includeOptimize: false,
            scope: .local
        )

        #expect(value(after: "--provider", in: args) == "lingtai-tui")
        #expect(value(after: "--period", in: args) == "month")
    }

    /// Concurrency + timeout smoke test: launch more hung subprocesses than
    /// there are cooperative threads, all at once, with a short timeout, and
    /// assert every call returns because the timeout killed its sleep.
    ///
    /// NOTE: this does NOT reproduce the production permanent deadlock (16/16
    /// cooperative threads parked in waitUntilExit). The real deadlock built up
    /// over ~2 days under the @MainActor refresh loop and is confirmed by the
    /// live `sample`, not by this test. Kept as a guard that the off-pool wait
    /// + timeout path stays correct under concurrency.
    ///
    /// The body must stay `async` and await the group directly. It used to
    /// block on a `DispatchSemaphore` with a 15s deadline, on the claim that a
    /// test body runs on a real thread. It does not: Swift Testing invokes even
    /// synchronous test bodies from a task on the cooperative pool, so the wait
    /// parked one of the pool's `activeProcessorCount` workers on the very work
    /// it was waiting for. A 16-core dev box has slack, a 3-core CI runner does
    /// not, and the wait expired with the group making no progress at all.
    /// Keeping it `async` also lets the compiler reject the blocking wait,
    /// which is unavailable from asynchronous contexts.
    @Test("concurrent timed-out processes all complete", .timeLimit(.minutes(1)))
    func concurrentTimedOutProcessesAllComplete() async {
        let count = ProcessInfo.processInfo.activeProcessorCount * 2 + 4
        let codes = await withTaskGroup(of: Int32?.self) { group -> [Int32?] in
            for _ in 0..<count {
                group.addTask {
                    let process = Process()
                    process.executableURL = URL(fileURLWithPath: "/bin/sleep")
                    process.arguments = ["30"]
                    return try? await DataClient.runProcess(process, timeoutSeconds: 1, label: "sleep 30").exitCode
                }
            }
            var out: [Int32?] = []
            for await code in group { out.append(code) }
            return out
        }

        #expect(codes.count == count)
        // A `sleep 30` cannot end within a 1s timeout on its own, so a signal
        // status is proof the timeout fired and killed it rather than the child
        // racing the deadline and exiting normally.
        #expect(codes.allSatisfy { $0 == SIGTERM || $0 == SIGKILL },
                "every hung process should be killed by its own timeout, got \(codes)")
    }

    /// A decode failure surfaces the CLI's actual stdout/stderr so a stray banner
    /// on stdout (see #515) is self-diagnosing instead of an opaque "not valid JSON".
    @Test("decode failure surfaces output")
    func decodeFailureSurfacesOutput() {
        struct Boom: Error {}
        let failure = CLIDecodeFailure(
            underlying: Boom(),
            stdoutByteCount: 13,
            stdoutSnippet: "(node) banner",
            stderr: "warn: x"
        )
        let text = String(describing: failure)
        #expect(text.contains("(node) banner"), "should include the stdout snippet")
        #expect(text.contains("13 bytes"), "should include the stdout byte count")
        #expect(text.contains("warn: x"), "should include stderr")
    }

    /// Empty stdout is reported distinctly (the JSONDecoder-on-empty-Data case).
    @Test("decode failure with empty stdout")
    func decodeFailureWithEmptyStdout() {
        struct Boom: Error {}
        let failure = CLIDecodeFailure(underlying: Boom(), stdoutByteCount: 0, stdoutSnippet: "", stderr: "")
        let text = String(describing: failure)
        #expect(text.contains("0 bytes"))
        #expect(text.contains("<empty>"))
    }

    /// A normally-exiting process returns its real output and exit code through
    /// the off-pool wait path.
    @Test("process returns output and exit code")
    func processReturnsOutputAndExitCode() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/echo")
        process.arguments = ["hello"]
        let result = try await DataClient.runProcess(process, timeoutSeconds: 5, label: "echo hello")
        #expect(result.exitCode == 0)
        #expect(String(data: result.stdout, encoding: .utf8) == "hello\n")
    }

    /// Many NORMALLY-exiting processes, all at once, must every one complete
    /// through the terminationHandler wait path. Guards against the wait path
    /// leaking or wedging under concurrency (the production bug was the wait and
    /// its timeout sharing one queue that saturated under sustained load).
    @Test("many normal processes all complete")
    func manyNormalProcessesAllComplete() async {
        let count = 50
        let codes = await withTaskGroup(of: Int32?.self) { group -> [Int32?] in
            for _ in 0..<count {
                group.addTask {
                    let process = Process()
                    process.executableURL = URL(fileURLWithPath: "/bin/echo")
                    process.arguments = ["ok"]
                    return try? await DataClient.runProcess(process, timeoutSeconds: 5, label: "echo ok").exitCode
                }
            }
            var out: [Int32?] = []
            for await code in group { out.append(code) }
            return out
        }
        #expect(codes.count == count)
        #expect(codes.allSatisfy { $0 == 0 },
                "every concurrent process should exit 0 via the terminationHandler wait path")
    }

    /// #1117: the window bounds SILENCE, not runtime. A child that keeps talking
    /// past its window runs to completion; the old fixed timeout killed it.
    @Test("a chattering child outlives its window", .timeLimit(.minutes(1)))
    func chatteringChildOutlivesItsWindow() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", "for i in 1 2 3 4 5 6; do echo tick; sleep 0.4; done"]
        let result = try await DataClient.runProcess(process, timeoutSeconds: 1, label: "chatty")
        #expect(result.exitCode == 0, "a child emitting output every 0.4s must survive a 1s silence window")
    }

    /// The CLI's keepalive goes to STDERR, so stderr has to re-arm the watchdog
    /// too - and must not then turn up as the error message.
    @Test("stderr keepalives keep a stdout-silent child alive and stay out of the error text",
          .timeLimit(.minutes(1)))
    func stderrKeepalivesCountAsOutput() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [
            "-c",
            #"for i in 1 2 3 4 5 6; do printf 'CODEBURN_PROGRESS {"kind":"keepalive"}\n' >&2; sleep 0.4; done; exit 3"#,
        ]
        let result = try await DataClient.runProcess(process, timeoutSeconds: 1, label: "keepalive")
        #expect(result.exitCode == 3, "stderr keepalives must restart the silence window")
        #expect(result.stderr == "", "progress lines must never become the error message")
    }

    /// The async semaphore never lets more than its count run concurrently.
    @Test("async semaphore caps concurrency")
    func asyncSemaphoreCapsConcurrency() async {
        let sem = AsyncSemaphore(2)
        let peak = PeakCounter()
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<12 {
                group.addTask {
                    await sem.acquire()
                    await peak.enter()
                    try? await Task.sleep(nanoseconds: 8_000_000)
                    await peak.leave()
                    await sem.release()
                }
            }
        }
        let observed = await peak.peak
        #expect(observed <= 2, "semaphore should cap concurrency at 2, saw \(observed)")
        #expect(observed > 0)
    }
}

private func value(after flag: String, in args: [String]) -> String? {
    guard let index = args.firstIndex(of: flag), args.indices.contains(index + 1) else {
        return nil
    }
    return args[index + 1]
}

private actor PeakCounter {
    private var current = 0
    private(set) var peak = 0
    func enter() { current += 1; peak = max(peak, current) }
    func leave() { current -= 1 }
}
