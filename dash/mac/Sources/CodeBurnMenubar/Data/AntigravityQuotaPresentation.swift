import Foundation

/// Pure display-decision helpers for the Antigravity quota surfaces.
///
/// Antigravity quota comes from probing the local language server — there is
/// no credential file and no refresh path, so a vanished server maps to
/// `.noCredentials` ("start the Antigravity app") rather than a terminal
/// login state. Like the other file-free providers, the always-visible
/// surfaces keep showing the last good snapshot with a quiet caption instead
/// of flapping to a reconnect screen; the reconnect screen is reserved for
/// the no-data case, where there is genuinely nothing to show.
enum AntigravityQuotaPresentation {
    /// Which Plan-tab subview to render, given the load state and whether a
    /// last-known snapshot exists.
    enum PlanContent: Equatable {
        case noCredentials
        case loading
        case failed
        case transientFailed
        case reconnect(reason: String?)
        /// Render the loaded usage bars. `idle` is true when the connection
        /// has gone terminal but a snapshot is still on hand — the caller
        /// stamps a quiet "start the app" caption instead of hiding the data.
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
