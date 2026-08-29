import Foundation

/// Resolves the on-disk directory shared by the CLI, desktop app and menubar.
enum CodeBurnCacheDirectory {
    static func resolve(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> String {
        if let override = environment["CODEBURN_CACHE_DIR"],
           !override.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return override
        }
        return homeDirectory
            .appendingPathComponent(".cache", isDirectory: true)
            .appendingPathComponent("codeburn", isDirectory: true)
            .path
    }
}
