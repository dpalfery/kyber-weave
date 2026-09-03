import Foundation
import Testing
@testable import CodeBurnMenubar

@Suite("Capacity Dock provider quota registry")
@MainActor
struct CapacityDockProviderQuotaServiceTests {
    @Test("ClinePass dispatches with only its provider-scoped API key")
    func dispatchesClinePass() async throws {
        let capture = SecretCapture()
        let expected = Self.summary(percent: 0.42)
        let service = CapacityDockProviderQuotaService(dependencies: .init(
            refreshClinePass: { apiKey in
                await capture.record(apiKey)
                return expected
            }
        ))
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))
        let credential = CapacityDockProviderCredential(
            sourceMode: "api",
            apiKey: "  synthetic-clinepass-key  "
        )

        let result = try await service.fetch(provider: provider, credential: credential)
        let capturedKeys = await capture.values

        #expect(result == expected)
        #expect(capturedKeys == ["synthetic-clinepass-key"])
    }

    @Test("ClinePass requires its own saved API key")
    func requiresClinePassKey() async throws {
        let service = CapacityDockProviderQuotaService(dependencies: .init(
            refreshClinePass: { _ in
                Issue.record("Adapter must not run without a key")
                return Self.summary()
            }
        ))
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))

        await #expect(throws: CapacityDockProviderFetchFailure.self) {
            try await service.fetch(
                provider: provider,
                credential: CapacityDockProviderCredential(sourceMode: "api", apiKey: "  ")
            )
        }

        do {
            _ = try await service.fetch(
                provider: provider,
                credential: CapacityDockProviderCredential(sourceMode: "api", apiKey: "")
            )
            Issue.record("Expected a terminal missing-key failure")
        } catch let failure as CapacityDockProviderFetchFailure {
            #expect(failure.disposition == .terminal)
            #expect(failure.message == "Enter a ClinePass API key or token, then press Save & Connect.")
        }
    }

    @Test("unsupported catalog providers fail truthfully and terminally")
    func rejectsUnsupportedProvider() async throws {
        let service = CapacityDockProviderQuotaService(dependencies: .init(
            refreshClinePass: { _ in
                Issue.record("Wrong adapter dispatched")
                return Self.summary()
            }
        ))
        let provider = try #require(CapacityDockProvider(rawValue: "openrouter"))

        do {
            _ = try await service.fetch(
                provider: provider,
                credential: CapacityDockProviderCredential(sourceMode: "api", apiKey: "synthetic")
            )
            Issue.record("Expected an unsupported-provider failure")
        } catch let failure as CapacityDockProviderFetchFailure {
            #expect(failure.disposition == .terminal)
            #expect(failure.message ==
                "OpenRouter does not have a CodeBurn live quota adapter yet. Remove it from the dock or choose a supported provider.")
        }
    }

    @Test("adapter authentication failures are terminal")
    func classifiesAuthenticationFailure() async throws {
        let service = CapacityDockProviderQuotaService(dependencies: .init(
            refreshClinePass: { _ in throw ClinePassSubscriptionService.FetchError.authenticationRejected }
        ))
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))

        do {
            _ = try await service.fetch(
                provider: provider,
                credential: CapacityDockProviderCredential(sourceMode: "api", apiKey: "synthetic")
            )
            Issue.record("Expected an authentication failure")
        } catch let failure as CapacityDockProviderFetchFailure {
            #expect(failure.disposition == .terminal)
            #expect(failure.message == "ClinePass rejected this API key.")
        }
    }

    @Test("adapter rate limits and malformed responses remain retryable")
    func classifiesTransientFailures() async throws {
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))
        let errors: [ClinePassSubscriptionService.FetchError] = [.rateLimited, .parseFailure]

        for error in errors {
            let service = CapacityDockProviderQuotaService(dependencies: .init(
                refreshClinePass: { _ in throw error }
            ))
            do {
                _ = try await service.fetch(
                    provider: provider,
                    credential: CapacityDockProviderCredential(sourceMode: "api", apiKey: "synthetic")
                )
                Issue.record("Expected \(error) to fail")
            } catch let failure as CapacityDockProviderFetchFailure {
                #expect(failure.disposition == .transient)
                #expect(failure.message == error.localizedDescription)
            }
        }
        #expect(CapacityDockProviderFetchFailure.disposition(
            for: URLError(.timedOut)
        ) == .transient)
    }

    @Test("disconnect invalidates an in-flight provider refresh")
    func disconnectWinsOverInFlightRefresh() async throws {
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))
        let gate = AdapterGate()
        let store = AppStore()
        store.capacityDockCredentialLoader = { _ in
            CapacityDockProviderCredential(sourceMode: "api", apiKey: "synthetic")
        }
        store.capacityDockCredentialRemover = { _ in }
        store.capacityDockProviderQuotaService = CapacityDockProviderQuotaService(dependencies: .init(
            refreshClinePass: { _ in
                await gate.pause()
                return Self.summary(percent: 0.73)
            }
        ))

        let refresh = Task { await store.refreshCapacityDockProvider(provider) }
        await gate.waitUntilPaused()
        try await store.disconnectCapacityDockProvider(provider)
        await gate.open()
        await refresh.value

        #expect(store.capacityDockProviderSummaries[provider.id] == nil)
        #expect(!store.capacityDockProvidersLoading.contains(provider.id))
    }

    @Test("failed credential deletion preserves connection state and surfaces the error")
    func failedDisconnectDoesNotPretendToSucceed() async throws {
        let provider = try #require(CapacityDockProvider(rawValue: "clinepass"))
        let store = AppStore()
        let known = Self.summary(percent: 0.42)
        store.capacityDockProviderSummaries[provider.id] = known
        store.capacityDockCredentialRemover = { _ in throw SyntheticDeleteFailure() }

        await #expect(throws: SyntheticDeleteFailure.self) {
            try await store.disconnectCapacityDockProvider(provider)
        }

        #expect(store.capacityDockProviderSummaries[provider.id] == known)
        #expect(!store.capacityDockProvidersLoading.contains(provider.id))
    }

    private actor SecretCapture {
        private(set) var values: [String] = []
        func record(_ value: String) { values.append(value) }
    }

    private actor AdapterGate {
        private var isPaused = false
        private var continuation: CheckedContinuation<Void, Never>?

        func pause() async {
            isPaused = true
            await withCheckedContinuation { continuation = $0 }
        }

        func waitUntilPaused() async {
            while !isPaused { await Task.yield() }
        }

        func open() {
            continuation?.resume()
            continuation = nil
        }
    }

    private struct SyntheticDeleteFailure: Error, Equatable {}

    nonisolated private static func summary(percent: Double = 0) -> QuotaSummary {
        let window = QuotaSummary.Window(label: "Weekly", percent: percent, resetsAt: nil)
        return QuotaSummary(
            providerFilter: .all,
            connection: .connected,
            primary: window,
            details: [window],
            planLabel: nil,
            footerLines: []
        )
    }
}
