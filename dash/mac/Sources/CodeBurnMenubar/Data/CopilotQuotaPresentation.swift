import Foundation

/// Pure display-decision helpers for the Copilot quota surfaces.
///
/// Copilot tokens are read straight from the editor plugins' on-disk files
/// with no refresh path, so a revoked token sits in `.terminalFailure` until
/// the user signs in via an editor's Copilot plugin again. Like Kimi and
/// Gemini, the always-visible surfaces keep showing the last good snapshot
/// with a quiet caption instead of flapping to a reconnect screen; the
/// reconnect screen is reserved for the no-data case, where there is
/// genuinely nothing to show.
enum CopilotQuotaPresentation {
    /// Which Plan-tab subview to render, given the load state and whether a
    /// last-known snapshot exists.
    enum PlanContent: Equatable {
        case noCredentials
        case loading
        case failed
        case transientFailed
        case reconnect(reason: String?)
        /// Render the loaded usage bars. `idle` is true when the login has
        /// gone terminal but a snapshot is still on hand — the caller stamps a
        /// quiet "sign in again" caption instead of hiding the data.
        case usage(idle: Bool)
    }

    static func planContent(loadState: SubscriptionLoadState, hasUsage: Bool) -> PlanContent {
        switch loadState {
        case .notBootstrapped, .noCredentials:
            return .noCredentials
        case .dormant, .bootstrapping:
            return .loading
        case .loading, .loaded:
            return hasUsage ? .usage(idle: false) : .loading
        case .failed:
            return .failed
        case .transientFailure:
            return hasUsage ? .usage(idle: false) : .transientFailed
        case .terminalFailure(let reason):
            return hasUsage ? .usage(idle: true) : .reconnect(reason: reason)
        }
    }

    /// Snapshot age past which a loaded view stamps an "as of <time>" caption,
    /// so a bar can never silently masquerade as current.
    static let stalenessThreshold: TimeInterval = 10 * 60

    static func isStale(fetchedAt: Date, now: Date = Date()) -> Bool {
        now.timeIntervalSince(fetchedAt) > stalenessThreshold
    }
}
