import Foundation
import Testing
@testable import CodeBurnMenubar

@Suite("CodeBurnCacheDirectory")
struct CodeBurnCacheDirectoryTests {
    @Test("honors CODEBURN_CACHE_DIR override")
    func honorsOverride() {
        let resolved = CodeBurnCacheDirectory.resolve(
            environment: ["CODEBURN_CACHE_DIR": "/tmp/codeburn-shared-cache"],
            homeDirectory: URL(fileURLWithPath: "/Users/test")
        )

        #expect(resolved == "/tmp/codeburn-shared-cache")
    }

    @Test("falls back to the user's standard cache directory")
    func fallsBackToStandardDirectory() {
        let resolved = CodeBurnCacheDirectory.resolve(
            environment: [:],
            homeDirectory: URL(fileURLWithPath: "/Users/test", isDirectory: true)
        )

        #expect(resolved == "/Users/test/.cache/codeburn")
    }

    @Test("ignores an empty cache override")
    func ignoresEmptyOverride() {
        let resolved = CodeBurnCacheDirectory.resolve(
            environment: ["CODEBURN_CACHE_DIR": "  \n"],
            homeDirectory: URL(fileURLWithPath: "/Users/test", isDirectory: true)
        )

        #expect(resolved == "/Users/test/.cache/codeburn")
    }
}
