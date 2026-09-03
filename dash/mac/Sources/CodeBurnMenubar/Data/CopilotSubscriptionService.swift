import Foundation

/// Live quota snapshot for a GitHub Copilot plan. The API reports per-feature
/// quota snapshots ("Premium requests", "Chat") as percent REMAINING; windows
/// render percent USED, so `usedPercent` is the inverted value. `details`
/// keeps the endpoint's order (premium first), so `primary` is the premium
/// window when present and chat otherwise.
struct CopilotUsage: Sendable, Equatable {
    struct Window: Sendable, Equatable {
        let label: String
        let usedPercent: Double   // 0.0 ... 100.0
        /// The endpoint reports no reset times — always nil.
        let resetsAt: Date?
    }

    /// Premium-requests window first, then chat; either may be absent.
    let details: [Window]
    var primary: Window? { details.first }
    /// Plan label derived from copilot_plan (Free / Individual / Pro /
    /// Business / Enterprise / Educators, unknown tiers title-cased).
    let plan: String?
    let fetchedAt: Date
}

/// Live GitHub Copilot quota via the editor plugins' internal usage endpoint
/// (derived from observed GitHub Copilot client traffic):
///
/// - GET https://api.github.com/copilot_internal/user → plan + quota snapshots
///
/// This is an INTERNAL, UNDOCUMENTED API that may drift without notice; every
/// failure must degrade to the normal connection states and never crash the
/// panel.
///
/// Credential: the GitHub OAuth token already on disk from a signed-in
/// Copilot plugin — ~/.config/github-copilot/hosts.json (keyed by host,
/// github.com preferred) falling back to apps.json. Read-only; nothing is
/// copied or written, and there is no refresh path: an expired or revoked
/// token stays dead until the user signs in via an editor's Copilot plugin
/// again, which rewrites the file.
enum CopilotSubscriptionService {
    private static let usageURL = URL(string: "https://api.github.com/copilot_internal/user")!
    private static let usageBlockedUntilKey = "codeburn.copilot.usage.blockedUntil"

    enum FetchError: Error, LocalizedError {
        case noCredentials
        /// 401 where re-reading the file yielded the same (or no) token. An
        /// active editor session rotates this token, so this is transient —
        /// the next refresh picks up the rotated token from disk.
        case tokenRejected
        case rateLimited(retryAt: Date)
        case usageHTTPError(Int)
        case usageDecodeFailed
        case network(Error)

        var errorDescription: String? {
            switch self {
            case .noCredentials:
                return "No GitHub Copilot credentials found. Sign in via an editor's Copilot plugin first."
            case .tokenRejected:
                return "GitHub rejected the stored Copilot token. It will be retried once an editor session refreshes it."
            case let .rateLimited(retryAt):
                let f = RelativeDateTimeFormatter()
                f.unitsStyle = .short
                return "GitHub rate-limited the Copilot quota endpoint. Retrying \(f.localizedString(for: retryAt, relativeTo: Date()))."
            case let .usageHTTPError(code):
                return "Copilot quota fetch failed (HTTP \(code)). Sign in via an editor's Copilot plugin, then Reconnect."
            case .usageDecodeFailed:
                return "Copilot quota response was malformed."
            case let .network(err):
                return "Network error: \(err.localizedDescription)"
            }
        }

        var isTerminal: Bool {
            switch self {
            case .noCredentials:
                return true
            case let .usageHTTPError(code):
                return (400..<500).contains(code)
            case .tokenRejected, .rateLimited, .usageDecodeFailed, .network:
                return false
            }
        }

        var rateLimitRetryAt: Date? {
            if case let .rateLimited(retryAt) = self { return retryAt }
            return nil
        }
    }

    // MARK: - Injectable seams (tests drive fixtures through these)

    struct Deps: Sendable {
        var fetch: @Sendable (URLRequest) async throws -> (Data, HTTPURLResponse)
        /// Read-only credential file load. Never writes.
        var readFile: @Sendable (URL) -> Data?
        var hostsURL: URL
        var appsURL: URL
        var now: @Sendable () -> Date

        static let live = Deps(
            fetch: { request in
                let (data, response) = try await URLSession.shared.data(for: request)
                guard let http = response as? HTTPURLResponse else {
                    throw FetchError.usageHTTPError(-1)
                }
                return (data, http)
            },
            readFile: { url in FileManager.default.contents(atPath: url.path) },
            hostsURL: URL(fileURLWithPath: NSHomeDirectory() + "/.config/github-copilot/hosts.json"),
            appsURL: URL(fileURLWithPath: NSHomeDirectory() + "/.config/github-copilot/apps.json"),
            now: { Date() }
        )
    }

    // MARK: - Credential files

    private static var defaultHostsURL: URL { Deps.live.hostsURL }
    private static var defaultAppsURL: URL { Deps.live.appsURL }

    static var hasCredential: Bool {
        FileManager.default.fileExists(atPath: defaultHostsURL.path)
            || FileManager.default.fileExists(atPath: defaultAppsURL.path)
    }

    /// hosts.json first, apps.json as fallback; a malformed or unreadable
    /// file falls through to the next candidate. Throws noCredentials when
    /// neither yields a token.
    private static func readToken(deps: Deps) throws -> String {
        for url in [deps.hostsURL, deps.appsURL] {
            guard let data = deps.readFile(url),
                  let token = tokenFromMap(data) else { continue }
            return token
        }
        throw FetchError.noCredentials
    }

    /// hosts.json keys by host — prefer github.com; apps.json has no
    /// canonical key, so its first entry wins. Both store the token as
    /// `oauth_token`.
    private static func tokenFromMap(_ data: Data) -> String? {
        guard let map = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let preferred = map["github.com"] ?? map.values.first
        guard let token = (preferred as? [String: Any])?["oauth_token"] as? String, !token.isEmpty else { return nil }
        return token
    }

    // MARK: - Fetch

    static func refresh(deps: Deps = .live) async throws -> CopilotUsage {
        if let until = usageBlockedUntil(), until > deps.now() {
            throw FetchError.rateLimited(retryAt: until)
        }
        var token = try readToken(deps: deps)

        var (data, response) = try await send(request(token: token), deps: deps)
        if response.statusCode == 401 {
            // An active editor session rotates this token; re-read once before
            // giving up so we don't report a failure the disk already fixed.
            guard let reread = try? readToken(deps: deps), reread != token else {
                throw FetchError.tokenRejected
            }
            token = reread
            (data, response) = try await send(request(token: token), deps: deps)
        }
        if response.statusCode == 429 {
            throw FetchError.rateLimited(retryAt: recordUsageRateLimit(
                retryAfterSeconds: parseRetryAfterHeader(response.value(forHTTPHeaderField: "Retry-After"))))
        }
        guard (200..<300).contains(response.statusCode) else {
            throw FetchError.usageHTTPError(response.statusCode)
        }
        return try decodeUsage(data: data, now: deps.now())
    }

    private static func request(token: String) -> URLRequest {
        var request = URLRequest(url: usageURL)
        request.httpMethod = "GET"
        request.timeoutInterval = 30
        // Headers mirror the VS Code Copilot Chat plugin.
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("vscode/1.96.2", forHTTPHeaderField: "Editor-Version")
        request.setValue("copilot-chat/0.26.7", forHTTPHeaderField: "Editor-Plugin-Version")
        request.setValue("GitHubCopilotChat/0.26.7", forHTTPHeaderField: "User-Agent")
        request.setValue("2025-04-01", forHTTPHeaderField: "X-Github-Api-Version")
        request.setValue("token \(token)", forHTTPHeaderField: "Authorization")
        return request
    }

    private static func send(_ request: URLRequest, deps: Deps) async throws -> (Data, HTTPURLResponse) {
        do {
            return try await deps.fetch(request)
        } catch let error as FetchError {
            throw error
        } catch {
            throw FetchError.network(error)
        }
    }

    // MARK: - Decode (internal so tests can drive fixtures)

    static func decodeUsage(data: Data, now: Date = Date()) throws -> CopilotUsage {
        let parsed: Any
        do {
            parsed = try JSONSerialization.jsonObject(with: data)
        } catch {
            // Never log the body — account data readable via `log stream`.
            throw FetchError.usageDecodeFailed
        }
        // Field names have shipped both camelCase and snake_case; read each
        // alias rather than trusting one spelling.
        let root = parsed as? [String: Any] ?? [:]
        let snapshots = (root["quota_snapshots"] ?? root["quotaSnapshots"]) as? [String: Any]
        let premium = window(label: "Premium requests",
                             snapshot: snapshots?["premium_interactions"] ?? snapshots?["premiumInteractions"])
        let chat = window(label: "Chat", snapshot: snapshots?["chat"])
        return CopilotUsage(
            details: [premium, chat].compactMap { $0 },
            plan: planLabel(root["copilot_plan"] ?? root["copilotPlan"]),
            fetchedAt: now
        )
    }

    private static func window(label: String, snapshot: Any?) -> CopilotUsage.Window? {
        guard let row = snapshot as? [String: Any],
              let remaining = fraction(row["percent_remaining"] ?? row["percentRemaining"]) else { return nil }
        // Round away float dust from the 1-remaining subtraction
        // (1-0.7 != 0.3), mirroring the desktop decoder's toFixed(6) before
        // scaling to 0..100.
        let used = ((1 - remaining) * 1_000_000).rounded() / 1_000_000 * 100
        return CopilotUsage.Window(label: label, usedPercent: used, resetsAt: nil)
    }

    /// Percent-remaining (0..100) as a clamped 0..1 fraction. Non-numeric
    /// values (and booleans, which bridge to NSNumber) yield nil.
    private static func fraction(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let fraction = number.doubleValue / 100
        guard fraction.isFinite else { return nil }
        return min(1, max(0, fraction))
    }

    /// nil for missing/blank; known tiers get display names; unknown values
    /// are title-cased on _ and - boundaries (some_future_tier →
    /// "Some Future Tier").
    private static func planLabel(_ value: Any?) -> String? {
        guard let raw = (value as? String)?.trimmingCharacters(in: .whitespaces), !raw.isEmpty else { return nil }
        let lower = raw.lowercased()
        let known = [
            "free": "Free", "individual": "Individual", "pro": "Pro", "business": "Business",
            "enterprise": "Enterprise", "for_educators": "Educators", "for-educators": "Educators",
        ]
        if let label = known[lower] { return label }
        return lower
            .components(separatedBy: CharacterSet(charactersIn: "_-"))
            .filter { !$0.isEmpty }
            .map { $0.prefix(1).uppercased() + $0.dropFirst() }
            .joined(separator: " ")
    }

    // MARK: - 429 backoff

    private static func usageBlockedUntil() -> Date? {
        UserDefaults.standard.object(forKey: usageBlockedUntilKey) as? Date
    }

    private static func clearUsageBlock() {
        UserDefaults.standard.removeObject(forKey: usageBlockedUntilKey)
    }

    private static func parseRetryAfterHeader(_ value: String?) -> Int? {
        guard let value = value?.trimmingCharacters(in: .whitespaces), !value.isEmpty else { return nil }
        if let seconds = Int(value), seconds >= 0 { return seconds }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0)
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        if let date = f.date(from: value) {
            return max(0, Int(date.timeIntervalSinceNow))
        }
        return nil
    }

    private static func recordUsageRateLimit(retryAfterSeconds: Int?) -> Date {
        let seconds = max(retryAfterSeconds ?? 300, 60)
        let until = Date().addingTimeInterval(TimeInterval(seconds))
        UserDefaults.standard.set(until, forKey: usageBlockedUntilKey)
        return until
    }

    static func disconnect() {
        clearUsageBlock()
    }
}
