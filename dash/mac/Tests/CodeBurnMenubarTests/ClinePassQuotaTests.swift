import XCTest
@testable import CodeBurnMenubar

/// Fixture-driven tests for the native ClinePass quota adapter.
/// The public HTTP contract is GET /api/v1/users/me/plan/usage-limits with a
/// bearer API key. All network goes through the injected transport; tests
/// never touch Keychain or the live API.
@MainActor
final class ClinePassQuotaTests: XCTestCase {

    private final class RequestRecorder: @unchecked Sendable {
        private(set) var requests: [URLRequest] = []
        func record(_ request: URLRequest) { requests.append(request) }
    }

    nonisolated private static let syntheticKey = "synthetic-clinepass-test-key"

    nonisolated private static let successBody = """
    {
      "success": true,
      "data": {
        "limits": [
          {"type": "five_hour", "percentUsed": 13.5, "resetsAt": "2026-08-29T05:00:00Z"},
          {"type": "weekly", "percentUsed": 42, "resetsAt": "2026-09-05T00:00:00Z"},
          {"type": "monthly", "percentUsed": 7, "resetsAt": "2026-09-23T08:00:00Z"}
        ]
      }
    }
    """

    nonisolated private static func httpResponse(_ request: URLRequest, status: Int) -> HTTPURLResponse {
        HTTPURLResponse(
            url: request.url ?? URL(string: "https://api.cline.bot/api/v1/users/me/plan/usage-limits")!,
            statusCode: status,
            httpVersion: nil,
            headerFields: nil
        )!
    }

    private static func makeDeps(
        recorder: RequestRecorder,
        respond: @escaping @Sendable (URLRequest) async throws -> (Data, HTTPURLResponse)
    ) -> ClinePassSubscriptionService.Deps {
        ClinePassSubscriptionService.Deps(
            fetch: { request in
                recorder.record(request)
                return try await respond(request)
            }
        )
    }

    func testSuccessfulPayloadMapsFiveHourWeeklyAndMonthlyWindows() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(recorder: recorder) { request in
            (Self.successBody.data(using: .utf8)!, Self.httpResponse(request, status: 200))
        }

        let summary = try await ClinePassSubscriptionService.refresh(
            apiKey: Self.syntheticKey,
            deps: deps
        )

        XCTAssertEqual(summary.connection, .connected)
        XCTAssertEqual(summary.details.map(\.label), ["5-hour", "Weekly", "Monthly"])
        XCTAssertEqual(summary.details.map(\.percent), [0.135, 0.42, 0.07])
        XCTAssertEqual(summary.primary?.label, "Weekly")
        XCTAssertEqual(summary.primary?.percent ?? -1, 0.42, accuracy: 0.0001)
        XCTAssertEqual(summary.details[0].resetsAt, Date(timeIntervalSince1970: 1_787_979_600))
        XCTAssertEqual(summary.details[1].resetsAt, Date(timeIntervalSince1970: 1_788_566_400))
        XCTAssertEqual(summary.details[2].resetsAt, Date(timeIntervalSince1970: 1_790_150_400))
        XCTAssertEqual(recorder.requests.count, 1)
        let request = recorder.requests[0]
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(
            request.url?.absoluteString,
            "https://api.cline.bot/api/v1/users/me/plan/usage-limits")
        XCTAssertEqual(
            request.value(forHTTPHeaderField: "Authorization"),
            "Bearer \(Self.syntheticKey)")
    }

    func testAuthenticationResponsesAreTerminal() async throws {
        for status in [401, 403] {
            let recorder = RequestRecorder()
            let deps = Self.makeDeps(recorder: recorder) { request in
                (Data(), Self.httpResponse(request, status: status))
            }

            do {
                _ = try await ClinePassSubscriptionService.refresh(apiKey: Self.syntheticKey, deps: deps)
                XCTFail("Expected HTTP \(status) to reject authentication")
            } catch let error as ClinePassSubscriptionService.FetchError {
                XCTAssertEqual(error, .authenticationRejected)
                XCTAssertEqual(error.classification, .terminalAuth)
            }
        }
    }

    func testRateLimitIsTransient() async throws {
        let recorder = RequestRecorder()
        let deps = Self.makeDeps(recorder: recorder) { request in
            (Data(), Self.httpResponse(request, status: 429))
        }

        do {
            _ = try await ClinePassSubscriptionService.refresh(apiKey: Self.syntheticKey, deps: deps)
            XCTFail("Expected a rate-limit failure")
        } catch let error as ClinePassSubscriptionService.FetchError {
            XCTAssertEqual(error, .rateLimited)
            XCTAssertEqual(error.classification, .transient)
        }
    }

    func testMalformedSuccessPayloadsAreParseFailures() async throws {
        let malformedBodies = [
            "not json",
            #"{"success":false}"#,
            #"{"success":true,"data":{}}"#,
            #"{"success":true,"data":{"limits":[]}}"#,
            #"{"success":true,"data":{"limits":[{"type":"unknown","percentUsed":12}]}}"#,
            #"{"success":true,"data":{"limits":[{"type":"weekly"}]}}"#,
        ]

        for body in malformedBodies {
            let recorder = RequestRecorder()
            let deps = Self.makeDeps(recorder: recorder) { request in
                (Data(body.utf8), Self.httpResponse(request, status: 200))
            }

            do {
                _ = try await ClinePassSubscriptionService.refresh(apiKey: Self.syntheticKey, deps: deps)
                XCTFail("Expected malformed payload to fail")
            } catch let error as ClinePassSubscriptionService.FetchError {
                XCTAssertEqual(error, .parseFailure)
                XCTAssertEqual(error.classification, .parseFailure)
            }
        }
    }
}
