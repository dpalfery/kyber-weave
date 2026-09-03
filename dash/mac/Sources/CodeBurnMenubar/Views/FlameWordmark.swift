import SwiftUI

/// The "CodeBurn" wordmark filled with the website's animated flame gradient.
struct FlameWordmark: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var sweeping = false

    // CSS parity: the site's `.flame-text` uses background-size: 300% with
    // `flameShift 3s ease infinite` (keyframes 0%/100% at position 0%, 50% at
    // 100%). Here that's a 3x-wide gradient sweeping from its left edge aligned
    // with the text to its right edge aligned with the text (a 2x-width shift)
    // and back — a 1.5s ease-in-out each way, autoreversing.
    private static let flameColors: [Color] = [
        Color(red: 0xFF / 255.0, green: 0x6A / 255.0, blue: 0x00 / 255.0), // #ff6a00
        Color(red: 0xFF / 255.0, green: 0xDA / 255.0, blue: 0x44 / 255.0), // #ffda44
        Color(red: 0xE8 / 255.0, green: 0x55 / 255.0, blue: 0x3A / 255.0), // #e8553a
        Color(red: 0xFF / 255.0, green: 0x8C / 255.0, blue: 0x00 / 255.0), // #ff8c00
        Color(red: 0xFF / 255.0, green: 0xDA / 255.0, blue: 0x44 / 255.0), // #ffda44
        Color(red: 0xFF / 255.0, green: 0x6A / 255.0, blue: 0x00 / 255.0)  // #ff6a00
    ]

    private var wordmark: some View {
        Text("CodeBurn")
            .font(.system(size: 13, weight: .semibold))
            .tracking(-0.15)
    }

    var body: some View {
        wordmark
            .hidden()
            .overlay {
                GeometryReader { geo in
                    LinearGradient(
                        colors: Self.flameColors,
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                    .frame(width: geo.size.width * 3, height: geo.size.height)
                    .offset(x: sweeping ? -geo.size.width * 2 : 0)
                    .animation(
                        reduceMotion ? nil : .easeInOut(duration: 1.5).repeatForever(autoreverses: true),
                        value: sweeping
                    )
                }
                .mask(wordmark)
            }
            .onAppear { sweeping = true }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("CodeBurn")
    }
}
