import Foundation
import Testing
@testable import CodeBurnMenubar

/// Copilot tokens are read-only with no refresh path, so `.terminalFailure`
/// means the user must sign in via an editor's Copilot plugin again. These
/// tests pin the display decision: a terminal login with a snapshot on hand
/// must keep showing the bars (with a quiet idle caption), and only the
/// no-data case falls through to the reconnect screen.
@Suite("Copilot quota presentation")
struct CopilotQuotaPresentationTests {
    typealias Presentation = CopilotQuotaPresentation

    @Test("terminal failure with a snapshot keeps the usage bars, flagged idle")
    func terminalWithUsageShowsIdleUsage() {
        let content = Presentation.planContent(loadState: .terminalFailure(reason: "expired"), hasUsage: true)
        #expect(content == .usage(idle: true))
    }

    @Test("terminal failure with no snapshot falls through to reconnect")
    func terminalWithoutUsageShowsReconnect() {
        let content = Presentation.planContent(loadState: .terminalFailure(reason: "expired"), hasUsage: false)
        #expect(content == .reconnect(reason: "expired"))
    }

    @Test("loaded with a snapshot shows usage without the idle caption")
    func loadedShowsPlainUsage() {
        #expect(Presentation.planContent(loadState: .loaded, hasUsage: true) == .usage(idle: false))
    }

    @Test("transient failure keeps the last snapshot, else shows the retry screen")
    func transientFailureFallsBackToUsage() {
        #expect(Presentation.planContent(loadState: .transientFailure(retryAt: nil), hasUsage: true) == .usage(idle: false))
        #expect(Presentation.planContent(loadState: .transientFailure(retryAt: nil), hasUsage: false) == .transientFailed)
    }

    @Test("credential-absent states route to the connect prompt")
    func credentialStatesRouteToNoCredentials() {
        #expect(Presentation.planContent(loadState: .notBootstrapped, hasUsage: false) == .noCredentials)
        #expect(Presentation.planContent(loadState: .noCredentials, hasUsage: false) == .noCredentials)
    }

    @Test("a fresh snapshot is not stamped stale")
    func freshSnapshotIsNotStale() {
        let now = Date()
        let fetchedAt = now.addingTimeInterval(-60) // 1 min old
        #expect(Presentation.isStale(fetchedAt: fetchedAt, now: now) == false)
    }

    @Test("a snapshot older than the threshold is stamped stale")
    func oldSnapshotIsStale() {
        let now = Date()
        let fetchedAt = now.addingTimeInterval(-11 * 60) // 11 min old
        #expect(Presentation.isStale(fetchedAt: fetchedAt, now: now) == true)
    }
}
