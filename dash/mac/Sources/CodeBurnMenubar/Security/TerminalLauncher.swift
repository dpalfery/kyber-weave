import AppKit
import Foundation

/// Runs commands in the user's preferred terminal (#877). Every string that reaches AppleScript
/// `do script` / `write text` must be whitespace-joined argv where each token passes
/// `CodeburnCLI.isSafe` (regex allowlist that excludes shell metacharacters), OR a hardcoded
/// literal defined here. `runScript` re-validates defensively so a future caller can't bypass
/// the invariant.
///
/// The terminal itself is chosen from the closed `PreferredTerminal` enum, never from a
/// user-supplied string, so the `tell application "..."` target stays a compile-time literal.
///
/// Resolution is a chain, not a single pick, because "installed" does not imply "scriptable":
/// osascript can still fail on a missing Automation (TCC) approval, an app that is present but
/// broken, or terminology it cannot load. So each candidate is actually run and its exit status
/// checked, and only once every candidate has failed do we fall back to a detached headless
/// spawn -- which at least still runs the subcommand on machines with no scriptable terminal
/// (Ghostty/Warp/kitty users). Each step logs, so a user who sees nothing has a trail in
/// Console.app instead of an app that looks dead.
enum TerminalLauncher {
    /// Upper bound on how long we wait for one `osascript` invocation.
    ///
    /// The failure modes we care about are fast: a terminology/compile error returns in ~0.15s
    /// and a denied Automation prompt is comparably quick. The only slow case is a *successful*
    /// cold app launch, which is seconds. So a timeout is not the mechanism that detects
    /// failure -- the exit status is -- and hitting it is treated as "it is still working",
    /// not as a failure. Falling back on timeout would open a second window in a second
    /// terminal, which is worse than waiting. The bound exists purely so a wedged osascript
    /// cannot pin a background worker forever.
    private static let scriptTimeout: TimeInterval = 30

    static func open(subcommand: [String]) {
        guard let command = safeCommand(argv: CodeburnCLI.baseArgv() + subcommand) else {
            NSLog("CodeBurn: refusing to open terminal with unsafe argv")
            return
        }

        let chain = terminalChain()
        // Knowing whether osascript worked means waiting for it, and a cold app launch keeps it
        // busy for a second or two. Callers are SwiftUI button actions on the main thread, so
        // the whole chain runs on a background queue: the popover stays responsive and the
        // fallback decision is made on a real exit status rather than on a guess.
        DispatchQueue.global(qos: .userInitiated).async {
            if runFirstWorking(chain: chain, command: command, attempt: runScript) != nil { return }
            if !chain.isEmpty {
                NSLog("CodeBurn: no terminal accepted the command; running it headless instead")
            }
            let headless = CodeburnCLI.makeProcess(subcommand: subcommand)
            do {
                try headless.run()
            } catch {
                NSLog("CodeBurn: headless fallback also failed: \(error.localizedDescription)")
            }
        }
    }

    /// Launches `claude login` in the preferred terminal so the user can complete the OAuth
    /// flow without leaving CodeBurn. The command is a hardcoded literal -- no user input is
    /// interpolated, so there's no injection surface.
    ///
    /// Returns whether a scriptable terminal exists at all. It cannot report the eventual exit
    /// status without blocking the main thread, so a later failure is logged rather than
    /// returned; there is no headless fallback here because a login flow is interactive by
    /// definition and would be useless without a window.
    @discardableResult
    static func openClaudeLogin() -> Bool {
        let chain = terminalChain()
        guard !chain.isEmpty else {
            NSLog("CodeBurn: no scriptable terminal present; user must run `claude login` manually")
            return false
        }
        DispatchQueue.global(qos: .userInitiated).async {
            if runFirstWorking(chain: chain, command: "claude login", attempt: runScript) == nil {
                NSLog("CodeBurn: no terminal accepted `claude login`; user must run it manually")
            }
        }
        return true
    }

    /// Joins `argv` into the command string, or returns nil if any token fails the allowlist.
    /// Extracted so the invariant is directly testable without launching anything.
    static func safeCommand(argv: [String]) -> String? {
        guard argv.allSatisfy(CodeburnCLI.isSafe) else { return nil }
        return argv.joined(separator: " ")
    }

    /// Terminals to try, most preferred first: the configured one when installed, then
    /// Terminal.app as the always-present backstop. Empty means nothing scriptable is present
    /// and the caller should go headless. `isInstalled` is injectable for tests.
    static func terminalChain(
        preference: PreferredTerminal = PreferredTerminal.saved(),
        isInstalled: (PreferredTerminal) -> Bool = { $0.isInstalled }
    ) -> [PreferredTerminal] {
        var chain: [PreferredTerminal] = []
        if isInstalled(preference) { chain.append(preference) }
        if preference != .terminal, isInstalled(.terminal) { chain.append(.terminal) }
        return chain
    }

    /// The terminal that will be attempted first, or nil when nothing scriptable is installed.
    static func resolvedTerminal(
        preference: PreferredTerminal = PreferredTerminal.saved(),
        isInstalled: (PreferredTerminal) -> Bool = { $0.isInstalled }
    ) -> PreferredTerminal? {
        terminalChain(preference: preference, isInstalled: isInstalled).first
    }

    /// Runs `command` in the first terminal of `chain` that actually succeeds and returns it,
    /// or nil when every candidate failed. `attempt` is injectable so tests can exercise
    /// "primary failed -> fell back" without launching anything.
    @discardableResult
    static func runFirstWorking(
        chain: [PreferredTerminal],
        command: String,
        attempt: (PreferredTerminal, String) -> Bool
    ) -> PreferredTerminal? {
        for terminal in chain {
            if attempt(terminal, command) { return terminal }
            NSLog("CodeBurn: \(terminal.label) did not run the command; trying the next fallback")
        }
        return nil
    }

    /// Drives one terminal via osascript and reports whether it worked.
    private static func runScript(_ terminal: PreferredTerminal, command: String) -> Bool {
        // Defence in depth: every caller validates already, but re-check so a future caller
        // cannot reach osascript with an unvalidated string.
        let tokens = command.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        guard tokens.allSatisfy(CodeburnCLI.isSafe) else {
            NSLog("CodeBurn: refusing to run unvalidated command in \(terminal.label)")
            return false
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", terminal.script(command: command)]
        let errorPipe = Pipe()
        process.standardError = errorPipe

        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }

        do {
            try process.run()
        } catch {
            NSLog("CodeBurn: could not spawn osascript for \(terminal.label): \(error.localizedDescription)")
            return false
        }

        guard finished.wait(timeout: .now() + scriptTimeout) == .success else {
            NSLog("CodeBurn: osascript for \(terminal.label) still running after \(Int(scriptTimeout))s; assuming its window opened")
            return true
        }

        guard process.terminationStatus == 0 else {
            // osascript writes one short line here, so reading after exit cannot deadlock on a
            // full pipe buffer.
            let detail = String(
                data: errorPipe.fileHandleForReading.readDataToEndOfFile(),
                encoding: .utf8
            )?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            NSLog("CodeBurn: osascript for \(terminal.label) exited \(process.terminationStatus): \(detail)")
            return false
        }
        return true
    }
}
