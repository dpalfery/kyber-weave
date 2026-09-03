import AppKit
import Testing
@testable import CodeBurnMenubar

@Suite("Provider icon fallback")
struct ProviderIconTests {
    @MainActor
    @Test("expanded providers receive deterministic, provider-specific sigils")
    func generatedSigilsAreStable() {
        #expect(ProviderIconCache.monogram(for: "clinepass") == "CL")
        #expect(ProviderIconCache.monogram(for: "open-code") == "OC")

        let first = ProviderIconCache.sigilDescriptor(for: "Open Router")
        let canonical = ProviderIconCache.sigilDescriptor(for: " open router ")
        let different = ProviderIconCache.sigilDescriptor(for: "Open Runtime")
        #expect(first == canonical)
        #expect(first != different)
    }

    @MainActor
    @Test("known providers prefer curated vectors and retain raster fallbacks")
    func bundledArtworkAliases() {
        #expect(ProviderIconCache.resourceCandidates(for: "codex") == [
            ProviderIconCache.ResourceCandidate(name: "provider-codex", fileExtension: "svg"),
            ProviderIconCache.ResourceCandidate(name: "openai", fileExtension: "png"),
        ])
        #expect(ProviderIconCache.resourceCandidates(for: "gemini") == [
            ProviderIconCache.ResourceCandidate(name: "provider-gemini", fileExtension: "svg"),
            ProviderIconCache.ResourceCandidate(name: "googlegemini", fileExtension: "png"),
        ])
        #expect(ProviderIconCache.resourceCandidates(for: "copilot") == [
            ProviderIconCache.ResourceCandidate(name: "provider-copilot", fileExtension: "svg"),
            ProviderIconCache.ResourceCandidate(name: "githubcopilot", fileExtension: "png"),
        ])
        #expect(ProviderIconCache.resourceCandidates(for: "antigravity") == [
            ProviderIconCache.ResourceCandidate(name: "provider-antigravity", fileExtension: "svg"),
            ProviderIconCache.ResourceCandidate(name: "antigravity", fileExtension: "png"),
        ])
        #expect(ProviderIconCache.resourceCandidates(for: "provider-without-artwork").isEmpty)
    }

    @MainActor
    @Test("every catalog provider resolves a bundled vector resource")
    func catalogVectorCoverage() {
        for provider in ProviderConnectionCatalog.providers {
            let candidates = ProviderIconCache.resourceCandidates(for: provider.id)
            #expect(candidates.first?.fileExtension == "svg", "Missing vector candidate for \(provider.id)")
            #expect(
                candidates.contains(where: resourceLoads),
                "No bundled provider artwork resolves for \(provider.id)"
            )
        }
    }

    @MainActor
    @Test("fallback is a template drawing representation with small-size presence")
    func fallbackRenderingQuality() throws {
        let image = ProviderIconCache.generatedSigil(named: "provider-without-artwork")
        #expect(image.isTemplate)
        #expect(image.representations.contains { $0 is NSCustomImageRep })

        let compactCoverage = try alphaCoverage(image, pixels: 18)
        let regularCoverage = try alphaCoverage(image, pixels: 36)
        #expect(compactCoverage > 0.12)
        #expect(compactCoverage < 0.58)
        #expect(regularCoverage > 0.12)
        #expect(regularCoverage < 0.58)
        #expect(abs(compactCoverage - regularCoverage) < 0.12)
    }

    @MainActor
    private func alphaCoverage(_ image: NSImage, pixels: Int) throws -> Double {
        let bitmap = try #require(NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixels,
            pixelsHigh: pixels,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bitmapFormat: [],
            bytesPerRow: 0,
            bitsPerPixel: 0
        ))
        let context = try #require(NSGraphicsContext(bitmapImageRep: bitmap))

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        NSColor.clear.setFill()
        NSRect(x: 0, y: 0, width: pixels, height: pixels).fill()
        image.draw(
            in: NSRect(x: 0, y: 0, width: pixels, height: pixels),
            from: .zero,
            operation: .sourceOver,
            fraction: 1
        )
        context.flushGraphics()
        NSGraphicsContext.restoreGraphicsState()

        var visible = 0
        for y in 0..<pixels {
            for x in 0..<pixels where (bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.18 {
                visible += 1
            }
        }
        return Double(visible) / Double(pixels * pixels)
    }

    private func resourceLoads(_ candidate: ProviderIconCache.ResourceCandidate) -> Bool {
        ["Resources/ProviderIcons", "ProviderIcons", nil].contains { subdirectory in
            guard let url = Bundle.module.url(
                forResource: candidate.name,
                withExtension: candidate.fileExtension,
                subdirectory: subdirectory
            ) else { return false }
            return NSImage(contentsOf: url) != nil
        }
    }
}
