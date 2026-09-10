import XCTest
@testable import CodeBurnMenubar

/// Fixture-driven tests for the GitHub Copilot quota flow, mirroring
/// app/electron/quota/copilot.test.ts: hosts.json / apps.json credential
/// selection, exact plugin headers, 401 re-read semantics, Retry-After
/// backoff, error classification, and the remaining→used percent decode.
/// All network and file access goes through the injected Deps seams; nothing
/// touches the real credential files or network.
final class CopilotQuotaTests: XCTestCase {

    /// Records every request the service makes, in order.
    private final class RequestRecorder: @unchecked Sendable {
        private(set) var requests: [URLRequest] = []
        func record(_ request: URLRequest) { requests.append(request) }
    }

    private static let now = Date(timeIntervalSince1970: 1_786_000_000)

    private static let hosts = """
    {"github.com":{"user":"octocat","oauth_token":"gho_test-secret"}}
    """

    private static let usageBody = """
    {"copilot_plan":"individual","quota_snapshots":{
      "premium_interactions":{"percent_remaining":70},
      "chat":{"percent_remaining":100}
    }}
    """

    private static func httpResponse(_ request: URLRequest, status: Int, headers: [String: String] = [:]) -> HTTPURLResponse {
        HTTPURLResponse(
            url: request.url ?? URL(string: "https://api.github.com")!,
            statusCode: status,
            httpVersion: nil,
            headerFields: headers
        )!
    }

    private static func okJson(_ request: URLRequest, _ body: String) -> (Data, HTTPURLResponse) {
        (body.data(using: .utf8)!, httpResponse(request, status: 200))
    }

    private static func makeDeps(
        hosts: String?,
        apps: String? = nil,
        recorder: RequestRecorder,
        readFile: (@Sendable (URL) -> Data?)? = nil,
        respond: @escaping @Sendable (URLRequest) -> (Data, HTTPURLResponse)
    ) -> CopilotSubscriptionService.Deps {
        CopilotSubscriptionService.Deps(
            fetch: { request in
                recorder.record(request)
                return respond(request)
            },
            readFile: readFile ?? { url in
                if url.lastPathComponent == "hosts.json" { return hosts?.data(using: .utf8) }
                if url.lastPathComponent == "apps.json" { return apps?.data(using: .utf8) }
                return nil
            },
            hostsURL: URL(fileURLWithPath: "/tmp/codeburn-tests/.config/github-copilot/hosts.json"),
            appsURL: URL(fileURLWithPath: "/tmp/codeburn-tests/.config/github-copilot/apps.json"),
            now: { Self.now }
        )
    }

    // MARK: - Decode

    func testDecodeSnakeCaseSnapshotsIntoUsedWindowsWithPlanLabel() throws {
        let usage = try CopilotSubscriptionService.decodeUsage(
            data: Self.usageBody.data(using: .utf8)!, now: Self.now)
        XCTAssertEqual(usage.plan, "Individual")
        XCTAssertEqual(usage.details.map(\.label), ["Premium requests", "Chat"])
        XCTAssertEqual(usage.details.map(\.usedPercent), [30, 0])
        XCTAssertEqual(usage.primary?.label, "Premium requests")
        XCTAssertEqual(usage.primary?.usedPercent ?? -1, 30, accuracy: 0.001)
        XCTAssertNil(usage.primary?.resetsAt)
    }

    func testDecodeCamelCasePromotesChatWhenPremiumIsAbsent() throws {
        let body = #"{"copilotPlan":"business","quotaSnapshots":{"chat":{"percentRemaining":55}}}"#
        let usage = try CopilotSubscriptionService.decodeUsage(
            data: body.data(using: .utf8)!, now: Self.now)
        XCTAssertEqual(usage.plan, "Business")
        XCTAssertEqual(usage.primary?.label, "Chat")
        XCTAssertEqual(usage.primary?.usedPercent ?? -1, 45, accuracy: 0.001)
    }

    func testDecodeSurvivesMalformedSnapshots() throws {
        let body = #"{"quota_snapshots":{"chat":"garbage"},"extra":true}"#
        let usage = try CopilotSubscriptionService.decodeUsage(
            data: body.data(using: .utf8)!, now: Self.now)
        XCTAssertNil(usage.primary)
        XCTAssertEqual(usage.details, [])
    }

    func testDecodeTitleCasesUnknownPlanTiers() throws {
        let educators = try CopilotSubscriptionService.decodeUsage(
            data: #"{"copilot_plan":"for_educators"}"#.data(using: .utf8)!, now: Self.now)
        XCTAssertEqual(educators.plan, "Educators")
        let future = try CopilotSubscriptionService.decodeUsage(
            data: #"{"copilot_plan":"some_future_tier"}"#.data(using: .utf8)!, now: Self.now)
        XCTAssertEqual(future.plan, "Some Future Tier")
        let missing = try CopilotSubscriptionService.decodeUsage(
            data: #"{}"#.data(using: .utf8)!, now: Self.now)
        XCTAssertNil(missing.plan)
    }

    // MARK: - Credential files

    func testNoCredentialsNeverFetches() async {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(hosts: nil, recorder: recorder) { request in
            Self.okJson(request, Self.usageBody)
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: deps)
            XCTFail("expected noCredentials")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case .noCredentials = error else {
                return XCTFail("expected noCredentials, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
        XCTAssertTrue(recorder.requests.isEmpty)
    }

    func testHostsJsonPreferredOverAppsJsonWithExactPluginHeaders() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(
            hosts: Self.hosts,
            apps: #"{"Some App":{"oauth_token":"gho_wrong"}}"#,
            recorder: recorder
        ) { request in
            Self.okJson(request, Self.usageBody)
        }
        let usage = try await CopilotSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(usage.plan, "Individual")
        XCTAssertEqual(recorder.requests.count, 1)
        let request = recorder.requests[0]
        XCTAssertEqual(request.url?.absoluteString, "https://api.github.com/copilot_internal/user")
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "token gho_test-secret")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "application/json")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Editor-Version"), "vscode/1.96.2")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Editor-Plugin-Version"), "copilot-chat/0.26.7")
        XCTAssertEqual(request.value(forHTTPHeaderField: "User-Agent"), "GitHubCopilotChat/0.26.7")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-Github-Api-Version"), "2025-04-01")
    }

    func testFallsBackToAppsJsonWhenHostsJsonHasNoToken() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(
            hosts: "{}",
            apps: #"{"Visual Studio Code":{"oauth_token":"ghu_apps-token"}}"#,
            recorder: recorder
        ) { request in
            Self.okJson(request, Self.usageBody)
        }
        _ = try await CopilotSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(recorder.requests.count, 1)
        XCTAssertEqual(
            recorder.requests[0].value(forHTTPHeaderField: "Authorization"),
            "token ghu_apps-token")
    }

    func testMalformedHostsJsonFallsThroughToAppsJson() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(
            hosts: "not json {",
            apps: #"{"Visual Studio Code":{"oauth_token":"ghu_apps-token"}}"#,
            recorder: recorder
        ) { request in
            Self.okJson(request, Self.usageBody)
        }
        _ = try await CopilotSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(
            recorder.requests[0].value(forHTTPHeaderField: "Authorization"),
            "token ghu_apps-token")
    }

    // MARK: - 401 re-read

    func testUnauthorizedRereadsOnceAndAdoptsRotatedToken() async throws {
        final class ReadCounter: @unchecked Sendable {
            var reads = 0
        }
        let counter = ReadCounter()
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(hosts: nil, recorder: recorder, readFile: { _ in
            counter.reads += 1
            let token = counter.reads == 1 ? "gho_stale" : "gho_rotated"
            return #"{"github.com":{"oauth_token":"\#(token)"}}"#.data(using: .utf8)
        }) { request in
            if request.value(forHTTPHeaderField: "Authorization") == "token gho_rotated" {
                return Self.okJson(request, Self.usageBody)
            }
            return (Data(), Self.httpResponse(request, status: 401))
        }
        let usage = try await CopilotSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(usage.plan, "Individual")
        XCTAssertEqual(recorder.requests.count, 2)
    }

    func testUnauthorizedWithUnchangedTokenIsTransientWithoutRetry() async {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(hosts: Self.hosts, recorder: recorder) { request in
            (Data(), Self.httpResponse(request, status: 401))
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: deps)
            XCTFail("expected tokenRejected")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case .tokenRejected = error else {
                return XCTFail("expected tokenRejected, got \(error)")
            }
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
        // One probe only: re-reading found the same token, so a retry is pointless.
        XCTAssertEqual(recorder.requests.count, 1)
    }

    // MARK: - Error classification

    func testRateLimitedUsesRetryAfterHeader() async {
        defer { CopilotSubscriptionService.disconnect() }
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(hosts: Self.hosts, recorder: recorder) { request in
            (Data(), Self.httpResponse(request, status: 429, headers: ["Retry-After": "75"]))
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: deps)
            XCTFail("expected rateLimited")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case let .rateLimited(retryAt) = error else {
                return XCTFail("expected rateLimited, got \(error)")
            }
            XCTAssertEqual(retryAt.timeIntervalSinceNow, 75, accuracy: 10)
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testServerErrorIsTransientAndClientErrorIsTerminal() async {
        let serverRecorder = RequestRecorder()
        let serverError = Self.makeDeps(hosts: Self.hosts, recorder: serverRecorder) { request in
            (Data(), Self.httpResponse(request, status: 503))
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: serverError)
            XCTFail("expected usageHTTPError")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case .usageHTTPError(503) = error else {
                return XCTFail("expected 503, got \(error)")
            }
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }

        let clientRecorder = RequestRecorder()
        let clientError = Self.makeDeps(hosts: Self.hosts, recorder: clientRecorder) { request in
            (Data(), Self.httpResponse(request, status: 404))
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: clientError)
            XCTFail("expected usageHTTPError")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case .usageHTTPError(404) = error else {
                return XCTFail("expected 404, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testMalformedSuccessBodyDegradesInsteadOfCrashing() async {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(hosts: Self.hosts, recorder: recorder) { request in
            ("not json {".data(using: .utf8)!, Self.httpResponse(request, status: 200))
        }
        do {
            _ = try await CopilotSubscriptionService.refresh(deps: deps)
            XCTFail("expected usageDecodeFailed")
        } catch let error as CopilotSubscriptionService.FetchError {
            guard case .usageDecodeFailed = error else {
                return XCTFail("expected usageDecodeFailed, got \(error)")
            }
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }
}
