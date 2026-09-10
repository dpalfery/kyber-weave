import XCTest
@testable import CodeBurnMenubar

/// Fixture-driven tests for the Gemini Code Assist quota flow, mirroring
/// app/electron/quota/gemini.test.ts: decode ordering, the
/// loadCodeAssist → retrieveUserQuota sequence, no-credentials, expired
/// token (with and without the env refresh overrides), and API-error
/// classification. All network and file access goes through the injected
/// Deps seams; nothing touches the real credential file or network.
final class GeminiQuotaTests: XCTestCase {

    /// Records every request the service makes, in order.
    private final class RequestRecorder: @unchecked Sendable {
        private(set) var requests: [URLRequest] = []
        func record(_ request: URLRequest) { requests.append(request) }
    }

    private static let now = Date(timeIntervalSince1970: 1_786_000_000)

    // TODO(codeburn-hotfix): downstream allowlist for synthetic OAuth fixtures — re-apply after `git subtree pull` from codeburn.
    private static var credential: String {
        "{\"access_token\":\"ya29.test-secret\",\"refresh_token\":\"1//refresh-secret\",\"expiry_date\":\(Int(Self.now.timeIntervalSince1970 * 1000) + 3_600_000)}" // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow
    }

    /// A Google Workspace id_token carries the hosted-domain (`hd`) JWT claim.
    private static var workspaceCredential: String {
        // base64url(JSON {"hd":"example.com"})
        "{\"access_token\":\"ya29.workspace-secret\",\"id_token\":\"eyJhbGciOiJub25lIn0.eyJoZCI6ImV4YW1wbGUuY29tIn0.sig\",\"expiry_date\":\(Int(Self.now.timeIntervalSince1970 * 1000) + 3_600_000)}" // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, generic.secrets.security.detected-jwt-token, detected-google-oauth-access-token, detected-jwt-token // gitleaks:allow
    }

    private static let quotaBody = """
    {"buckets":[
      {"modelId":"gemini-2.5-flash","remainingFraction":0.9,"resetTime":"2026-07-13T00:00:00Z"},
      {"modelId":"gemini-2.5-pro","remainingFraction":0.25,"resetTime":"2026-07-12T18:00:00Z"},
      {"modelId":"gemini-2.5-lite","remainingFraction":"garbage"}
    ]}
    """

    private static func httpResponse(_ request: URLRequest, status: Int, headers: [String: String] = [:]) -> HTTPURLResponse {
        HTTPURLResponse(
            url: request.url ?? URL(string: "https://cloudcode-pa.googleapis.com")!,
            statusCode: status,
            httpVersion: nil,
            headerFields: headers
        )!
    }

    private static func okJson(_ request: URLRequest, _ body: String) -> (Data, HTTPURLResponse) {
        (body.data(using: .utf8)!, httpResponse(request, status: 200))
    }

    private static func makeDeps(
        credential: String?,
        recorder: RequestRecorder,
        clientCredentials: (clientId: String, clientSecret: String)? = nil,
        respond: @escaping @Sendable (URLRequest) -> (Data, HTTPURLResponse)
    ) -> GeminiSubscriptionService.Deps {
        GeminiSubscriptionService.Deps(
            fetch: { request in
                recorder.record(request)
                return respond(request)
            },
            readFile: { _ in credential?.data(using: .utf8) },
            credentialURL: URL(fileURLWithPath: "/tmp/codeburn-tests/.gemini/oauth_creds.json"),
            now: { Self.now },
            clientCredentials: { clientCredentials }
        )
    }

    // MARK: - Decode

    func testDecodeSortsMostConstrainedFirstAndDerivesUsedPercent() {
        let usage = GeminiSubscriptionService.decodeUsage(
            data: Self.quotaBody.data(using: .utf8)!, plan: nil, now: Self.now)
        XCTAssertEqual(usage.primary?.label, "gemini-2.5-pro")
        XCTAssertEqual(usage.primary?.usedPercent ?? -1, 75, accuracy: 0.001)
        XCTAssertEqual(usage.details.map(\.label), ["gemini-2.5-pro", "gemini-2.5-flash"])
        XCTAssertEqual(usage.details[0].resetsAt, Date(timeIntervalSince1970: 1_783_879_200))
    }

    func testDecodeSurvivesMalformedBuckets() {
        let json = #"{"buckets":[null,"x",{}],"extra":true}"#.data(using: .utf8)!
        let usage = GeminiSubscriptionService.decodeUsage(data: json, plan: nil, now: Self.now)
        XCTAssertNil(usage.primary)
        XCTAssertEqual(usage.details, [])
    }

    // MARK: - Fetch flow

    func testNoCredentialsNeverFetches() async {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: nil, recorder: recorder) { request in
            Self.okJson(request, "{}")
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: deps)
            XCTFail("expected noCredentials")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case .noCredentials = error else {
                return XCTFail("expected noCredentials, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
        XCTAssertTrue(recorder.requests.isEmpty)
    }

    func testLoadCodeAssistThenRetrieveUserQuotaWithDiscoveredProject() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: Self.credential, recorder: recorder) { request in
            if request.url?.absoluteString.contains("loadCodeAssist") == true {
                return Self.okJson(request, #"{"currentTier":{"name":"free-tier"},"cloudaicompanionProject":"gen-lang-client-1"}"#)
            }
            return Self.okJson(request, Self.quotaBody)
        }
        let usage = try await GeminiSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(usage.plan, "Free")
        XCTAssertEqual(usage.primary?.label, "gemini-2.5-pro")
        XCTAssertEqual(recorder.requests.count, 2)
        for request in recorder.requests {
            XCTAssertTrue(request.url?.absoluteString.hasPrefix("https://cloudcode-pa.googleapis.com/v1internal:") == true)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer ya29.test-secret") // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow

            XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
            XCTAssertEqual(request.value(forHTTPHeaderField: "User-Agent"), "CodeBurn")
        }
        let quotaBody = try JSONSerialization.jsonObject(with: recorder.requests[1].httpBody ?? Data()) as? [String: String]
        XCTAssertEqual(quotaBody, ["project": "gen-lang-client-1"])
    }

    func testEmptyProjectBodyWhenDiscoveryYieldsNone() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: Self.credential, recorder: recorder) { request in
            if request.url?.absoluteString.contains("loadCodeAssist") == true {
                return Self.okJson(request, "{}")
            }
            return Self.okJson(request, Self.quotaBody)
        }
        _ = try await GeminiSubscriptionService.refresh(deps: deps)
        let body = try JSONSerialization.jsonObject(with: recorder.requests[1].httpBody ?? Data()) as? [String: String]
        XCTAssertEqual(body, [:])
    }

    func testExpiredTokenWithoutClientCredentialsIsTerminal() async {
        let stale = "{\"access_token\":\"ya29.test-secret\",\"refresh_token\":\"1//refresh-secret\",\"expiry_date\":\(Int(Self.now.timeIntervalSince1970 * 1000) - 1000)}" // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow


        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: stale, recorder: recorder) { request in
            Self.okJson(request, "{}")
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: deps)
            XCTFail("expected tokenExpired")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case .tokenExpired = error else {
                return XCTFail("expected tokenExpired, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
        XCTAssertTrue(recorder.requests.isEmpty)
    }

    func testExpiredTokenRefreshesInMemoryViaEnvOverrides() async throws {
        let stale = "{\"access_token\":\"ya29.test-secret\",\"refresh_token\":\"1//refresh-secret\",\"expiry_date\":\(Int(Self.now.timeIntervalSince1970 * 1000) - 1000)}" // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow


        let recorder = RequestRecorder()
        let deps = Self.makeDeps(
            credential: stale,
            recorder: recorder,
            clientCredentials: (clientId: "client-id", clientSecret: "client-secret")
        ) { request in
            let url = request.url?.absoluteString ?? ""
            if url.contains("oauth2.googleapis.com") {
                return Self.okJson(request, #"{"access_token":"ya29.refreshed"}"#) // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow


            }
            if url.contains("loadCodeAssist") {
                return Self.okJson(request, #"{"paidTier":{"name":"standard-tier"}}"#)
            }
            return Self.okJson(request, Self.quotaBody)
        }
        let usage = try await GeminiSubscriptionService.refresh(deps: deps)
        XCTAssertEqual(usage.plan, "Paid")
        XCTAssertEqual(recorder.requests.first?.url?.absoluteString, "https://oauth2.googleapis.com/token")
        XCTAssertEqual(recorder.requests.first?.httpMethod, "POST")
        let tokenBody = String(data: recorder.requests.first?.httpBody ?? Data(), encoding: .utf8) ?? ""
        XCTAssertTrue(tokenBody.contains("grant_type=refresh_token"))
        XCTAssertEqual(
            recorder.requests.last?.value(forHTTPHeaderField: "Authorization"),
            "Bearer ya29.refreshed") // nosemgrep: generic.secrets.security.detected-google-oauth-access-token, detected-google-oauth-access-token // gitleaks:allow


    }

    func testRateLimitedUsesRetryAfterHeader() async {
        defer { GeminiSubscriptionService.disconnect() }
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: Self.credential, recorder: recorder) { request in
            (Data(), Self.httpResponse(request, status: 429, headers: ["Retry-After": "90"]))
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: deps)
            XCTFail("expected rateLimited")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case let .rateLimited(retryAt) = error else {
                return XCTFail("expected rateLimited, got \(error)")
            }
            XCTAssertEqual(retryAt.timeIntervalSinceNow, 90, accuracy: 10)
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testServerErrorIsTransientAndClientErrorIsTerminal() async {
        let serverRecorder = RequestRecorder()
        let serverError = Self.makeDeps(credential: Self.credential, recorder: serverRecorder) { request in
            (Data(), Self.httpResponse(request, status: 503))
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: serverError)
            XCTFail("expected usageHTTPError")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case .usageHTTPError(503) = error else {
                return XCTFail("expected 503, got \(error)")
            }
            XCTAssertFalse(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }

        let clientRecorder = RequestRecorder()
        let clientError = Self.makeDeps(credential: Self.credential, recorder: clientRecorder) { request in
            (Data(), Self.httpResponse(request, status: 404))
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: clientError)
            XCTFail("expected usageHTTPError")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case .usageHTTPError(404) = error else {
                return XCTFail("expected 404, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testRetiredTierMapsToTerminalWithMigrationGuidance() async {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(credential: Self.credential, recorder: recorder) { request in
            (
                #"{"error":{"code":400,"status":"UNSUPPORTED_CLIENT","message":"IneligibleTierError: use Antigravity"}}"#.data(using: .utf8)!,
                Self.httpResponse(request, status: 400)
            )
        }
        do {
            _ = try await GeminiSubscriptionService.refresh(deps: deps)
            XCTFail("expected accountTierRetired")
        } catch let error as GeminiSubscriptionService.FetchError {
            guard case .accountTierRetired = error else {
                return XCTFail("expected accountTierRetired, got \(error)")
            }
            XCTAssertTrue(error.isTerminal)
            XCTAssertEqual(
                error.errorDescription,
                "Google retired Gemini CLI OAuth for this account tier. Use Antigravity.")
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testWorkspaceAccountLabelledFromIdTokenHostedDomain() async throws {
        let assist = #"{"currentTier":{"name":"free-tier"}}"#
        let workspaceRecorder = RequestRecorder()
        let workspace = Self.makeDeps(credential: Self.workspaceCredential, recorder: workspaceRecorder) { request in
            if request.url?.absoluteString.contains("loadCodeAssist") == true {
                return Self.okJson(request, assist)
            }
            return Self.okJson(request, Self.quotaBody)
        }
        let workspaceUsage = try await GeminiSubscriptionService.refresh(deps: workspace)
        XCTAssertEqual(workspaceUsage.plan, "Workspace")

        let personalRecorder = RequestRecorder()
        let personal = Self.makeDeps(credential: Self.credential, recorder: personalRecorder) { request in
            if request.url?.absoluteString.contains("loadCodeAssist") == true {
                return Self.okJson(request, assist)
            }
            return Self.okJson(request, Self.quotaBody)
        }
        let freeUsage = try await GeminiSubscriptionService.refresh(deps: personal)
        XCTAssertEqual(freeUsage.plan, "Free")
    }
}
