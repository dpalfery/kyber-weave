import CoreGraphics
import Foundation

/// Pure motion policy for Capacity Dock. The controller owns AppKit animation;
/// this type only answers durations, timing, and the top-anchored expand/collapse
/// and bubble presentation offsets.
enum CapacityDockMotion {
    struct RailFrameSample: Equatable {
        let frame: CGRect
        let presentationProgress: CGFloat
    }

    struct FloatingRailAnchors: Equatable {
        let top: CGFloat?
        let leading: CGFloat?
        let axisCoordinate: CGFloat?
    }

    enum Transaction: Equatable, Sendable {
        case immediate
        case railExpand
        case railCollapse
        case dockAttach
        case dockDetach
        case detailPresent
        case detailFollow
        case detailDismiss
    }

    static let railExpandDuration: TimeInterval = 0.52
    static let railCollapseDuration: TimeInterval = 0.44
    static let railHoverOpenDelay: TimeInterval = 0.08
    static let railHoverCloseDelay: TimeInterval = 0.18
    static let dockAttachDuration: TimeInterval = 0.28
    static let dockDetachDuration: TimeInterval = 0.24
    static let detailPresentDuration: TimeInterval = 0.20
    static let detailFollowDuration: TimeInterval = 0.22
    static let detailDismissDuration: TimeInterval = 0.14
    static let detailAppearOffset: CGFloat = 10
    static let heightEpsilon: CGFloat = 0.5

    static func duration(
        for transaction: Transaction,
        reduceMotion: Bool = false
    ) -> TimeInterval {
        if reduceMotion { return 0 }
        switch transaction {
        case .immediate: return 0
        case .railExpand: return railExpandDuration
        case .railCollapse: return railCollapseDuration
        case .dockAttach: return dockAttachDuration
        case .dockDetach: return dockDetachDuration
        case .detailPresent: return detailPresentDuration
        case .detailFollow: return detailFollowDuration
        case .detailDismiss: return detailDismissDuration
        }
    }

    static func shouldAnimate(
        _ transaction: Transaction,
        reduceMotion: Bool = false
    ) -> Bool {
        duration(for: transaction, reduceMotion: reduceMotion) > 0
    }

    /// Cubic-bezier control points. Ease-in-out for the rail; ease-out present
    /// and ease-in dismiss for the bubble.
    static func timingControlPoints(
        for transaction: Transaction
    ) -> (Float, Float, Float, Float) {
        switch transaction {
        case .immediate:
            return (0, 0, 1, 1)
        case .railExpand:
            return (0.22, 1, 0.36, 1)
        case .railCollapse:
            return (0.32, 0, 0.2, 1)
        case .dockAttach:
            return (0.16, 1, 0.3, 1)
        case .dockDetach:
            return (0.2, 0.8, 0.2, 1)
        case .detailFollow:
            return (0.25, 0.1, 0.25, 1)
        case .detailPresent:
            return (0.16, 1, 0.3, 1)
        case .detailDismiss:
            return (0.4, 0, 1, 1)
        }
    }

    static func railTransaction(
        fromFrame: CGRect,
        toFrame: CGRect,
        attachmentFrom: CGFloat,
        attachmentTo: CGFloat
    ) -> Transaction {
        if abs(attachmentTo - attachmentFrom) >= 0.01 {
            return attachmentTo > attachmentFrom ? .dockAttach : .dockDetach
        }
        let widthChanged = abs(toFrame.width - fromFrame.width) >= heightEpsilon
        let heightChanged = abs(toFrame.height - fromFrame.height) >= heightEpsilon
        if widthChanged || heightChanged {
            let fromArea = fromFrame.width * fromFrame.height
            let toArea = toFrame.width * toFrame.height
            return toArea > fromArea ? .railExpand : .railCollapse
        }
        return .immediate
    }

    /// Bubble starts slightly toward the rail so it emerges from the dock.
    static func detailPresentationStartFrame(
        from target: CGRect,
        side: CapacityDockEdge
    ) -> CGRect {
        target.offsetBy(
            dx: detailOffset(side: side, amount: detailAppearOffset).width,
            dy: detailOffset(side: side, amount: detailAppearOffset).height
        )
    }

    static func detailDismissalFrame(
        from current: CGRect,
        side: CapacityDockEdge
    ) -> CGRect {
        let offset = detailOffset(side: side, amount: detailAppearOffset * 0.6)
        return current.offsetBy(dx: offset.width, dy: offset.height)
    }

    static func interpolate(from: CGRect, to: CGRect, progress: CGFloat) -> CGRect {
        let t = min(max(progress, 0), 1)
        return CGRect(
            x: from.minX + (to.minX - from.minX) * t,
            y: from.minY + (to.minY - from.minY) * t,
            width: from.width + (to.width - from.width) * t,
            height: from.height + (to.height - from.height) * t
        )
    }

    static func interpolateAlpha(from: CGFloat, to: CGFloat, progress: CGFloat) -> CGFloat {
        let t = min(max(progress, 0), 1)
        return from + (to - from) * t
    }

    /// Linear interpolation with the top edge locked. Height grows or shrinks
    /// downward so the rail never jumps upward.
    static func interpolateTopAnchored(from: CGRect, to: CGRect, progress: CGFloat) -> CGRect {
        let t = min(max(progress, 0), 1)
        let width = from.width + (to.width - from.width) * t
        let height = from.height + (to.height - from.height) * t
        let x = from.minX + (to.minX - from.minX) * t
        let top = from.maxY + (to.maxY - from.maxY) * t
        return CGRect(x: x, y: top - height, width: width, height: height)
    }

    /// A rail always grows away from a stable visual anchor. Floating rails
    /// keep their upper-leading corner fixed; attached rails additionally lock
    /// the physical screen edge they are touching.
    static func interpolateRail(
        from: CGRect,
        to: CGRect,
        dockedEdge: CapacityDockEdge?,
        expansionAnchor: CapacityDockExpansionAnchor = .start,
        progress: CGFloat
    ) -> CGRect {
        if let dockedEdge {
            return interpolateAttachedEdge(
                from: from,
                to: to,
                edge: dockedEdge,
                expansionAnchor: expansionAnchor,
                progress: progress
            )
        }
        return expansionAnchor == .start
            ? interpolateTopAnchored(from: from, to: to, progress: progress)
            : interpolateBottomAnchored(from: from, to: to, progress: progress)
    }

    static func interpolateBottomAnchored(from: CGRect, to: CGRect, progress: CGFloat) -> CGRect {
        let t = min(max(progress, 0), 1)
        let width = from.width + (to.width - from.width) * t
        let height = from.height + (to.height - from.height) * t
        let x = from.minX + (to.minX - from.minX) * t
        let bottom = from.minY + (to.minY - from.minY) * t
        return CGRect(x: x, y: bottom, width: width, height: height)
    }

    static func floatingRailAnchors(
        frame: CGRect,
        preservedTop: CGFloat? = nil,
        isVertical: Bool,
        expansionAnchor: CapacityDockExpansionAnchor
    ) -> FloatingRailAnchors {
        if isVertical {
            return expansionAnchor == .start
                ? FloatingRailAnchors(
                    top: preservedTop ?? frame.maxY,
                    leading: frame.minX,
                    axisCoordinate: nil
                )
                : FloatingRailAnchors(
                    top: nil,
                    leading: frame.minX,
                    axisCoordinate: frame.minY
                )
        }
        return expansionAnchor == .start
            ? FloatingRailAnchors(
                top: preservedTop ?? frame.maxY,
                leading: frame.minX,
                axisCoordinate: nil
            )
            : FloatingRailAnchors(
                top: preservedTop ?? frame.maxY,
                leading: nil,
                axisCoordinate: frame.maxX
            )
    }

    static func pixelAlignedRailFrame(
        _ frame: CGRect,
        backingScale: CGFloat,
        dockedEdge: CapacityDockEdge?,
        expansionAnchor: CapacityDockExpansionAnchor = .start,
        isVertical: Bool = true
    ) -> CGRect {
        let scale = max(backingScale, 1)
        func aligned(_ value: CGFloat) -> CGFloat {
            (value * scale).rounded() / scale
        }

        let width = aligned(frame.width)
        let height = aligned(frame.height)
        let leading: CGFloat
        let bottom: CGFloat
        switch dockedEdge {
        case .right:
            leading = aligned(frame.maxX) - width
            bottom = expansionAnchor == .start
                ? aligned(frame.maxY) - height
                : aligned(frame.minY)
        case .top:
            leading = expansionAnchor == .start
                ? aligned(frame.minX)
                : aligned(frame.maxX) - width
            bottom = aligned(frame.maxY) - height
        case .bottom:
            leading = expansionAnchor == .start
                ? aligned(frame.minX)
                : aligned(frame.maxX) - width
            bottom = aligned(frame.minY)
        case .left:
            leading = aligned(frame.minX)
            bottom = expansionAnchor == .start
                ? aligned(frame.maxY) - height
                : aligned(frame.minY)
        case nil:
            if isVertical {
                leading = aligned(frame.minX)
                bottom = expansionAnchor == .start
                    ? aligned(frame.maxY) - height
                    : aligned(frame.minY)
            } else {
                leading = expansionAnchor == .start
                    ? aligned(frame.minX)
                    : aligned(frame.maxX) - width
                bottom = aligned(frame.minY)
            }
        }
        return CGRect(x: leading, y: bottom, width: width, height: height)
    }

    /// Couples the observable SwiftUI reveal state to the frame AppKit can
    /// actually display. Near the ends of an ease curve several timer samples
    /// can land on the same backing-pixel frame; publishing their raw timer
    /// progress would repeatedly re-layout the rail inside an unchanged panel.
    static func alignedRailSample(
        _ frame: CGRect,
        fromFrame: CGRect,
        toFrame: CGRect,
        fromPresentationProgress: CGFloat,
        toPresentationProgress: CGFloat,
        backingScale: CGFloat,
        dockedEdge: CapacityDockEdge?,
        expansionAnchor: CapacityDockExpansionAnchor = .start,
        isVertical: Bool
    ) -> RailFrameSample {
        let alignedFrame = pixelAlignedRailFrame(
            frame,
            backingScale: backingScale,
            dockedEdge: dockedEdge,
            expansionAnchor: expansionAnchor,
            isVertical: isVertical
        )
        let alignedFrom = pixelAlignedRailFrame(
            fromFrame,
            backingScale: backingScale,
            dockedEdge: dockedEdge,
            expansionAnchor: expansionAnchor,
            isVertical: isVertical
        )
        let alignedTo = pixelAlignedRailFrame(
            toFrame,
            backingScale: backingScale,
            dockedEdge: dockedEdge,
            expansionAnchor: expansionAnchor,
            isVertical: isVertical
        )
        let currentLength = isVertical ? alignedFrame.height : alignedFrame.width
        let fromLength = isVertical ? alignedFrom.height : alignedFrom.width
        let toLength = isVertical ? alignedTo.height : alignedTo.width
        let lengthDelta = toLength - fromLength
        let geometryProgress: CGFloat
        if abs(lengthDelta) < 0.000_001 {
            geometryProgress = 1
        } else {
            geometryProgress = min(max((currentLength - fromLength) / lengthDelta, 0), 1)
        }
        return RailFrameSample(
            frame: alignedFrame,
            presentationProgress: interpolateAlpha(
                from: fromPresentationProgress,
                to: toPresentationProgress,
                progress: geometryProgress
            )
        )
    }

    static func interpolateAttachedEdge(
        from: CGRect,
        to: CGRect,
        edge: CapacityDockEdge,
        expansionAnchor: CapacityDockExpansionAnchor = .start,
        progress: CGFloat
    ) -> CGRect {
        let t = min(max(progress, 0), 1)
        let width = from.width + (to.width - from.width) * t
        let height = from.height + (to.height - from.height) * t
        let x: CGFloat
        let y: CGFloat
        switch edge {
        case .left:
            x = from.minX + (to.minX - from.minX) * t
            y = expansionAnchor == .start
                ? (from.maxY + (to.maxY - from.maxY) * t) - height
                : from.minY + (to.minY - from.minY) * t
        case .right:
            let trailing = from.maxX + (to.maxX - from.maxX) * t
            x = trailing - width
            y = expansionAnchor == .start
                ? (from.maxY + (to.maxY - from.maxY) * t) - height
                : from.minY + (to.minY - from.minY) * t
        case .top:
            x = expansionAnchor == .start
                ? from.minX + (to.minX - from.minX) * t
                : (from.maxX + (to.maxX - from.maxX) * t) - width
            let top = from.maxY + (to.maxY - from.maxY) * t
            y = top - height
        case .bottom:
            x = expansionAnchor == .start
                ? from.minX + (to.minX - from.minX) * t
                : (from.maxX + (to.maxX - from.maxX) * t) - width
            y = from.minY + (to.minY - from.minY) * t
        }
        return CGRect(x: x, y: y, width: width, height: height)
    }

    static func easedProgress(for transaction: Transaction, linear: CGFloat) -> CGFloat {
        let t = min(max(linear, 0), 1)
        if transaction == .immediate { return t }
        let points = timingControlPoints(for: transaction)
        return cubicBezier(
            t,
            p1x: CGFloat(points.0),
            p1y: CGFloat(points.1),
            p2x: CGFloat(points.2),
            p2y: CGFloat(points.3)
        )
    }

    static func cubicBezier(
        _ t: CGFloat,
        p1x: CGFloat,
        p1y: CGFloat,
        p2x: CGFloat,
        p2y: CGFloat
    ) -> CGFloat {
        if t <= 0 { return 0 }
        if t >= 1 { return 1 }
        var guess = t
        for _ in 0..<6 {
            let current = sampleCurveX(guess, p1x: p1x, p2x: p2x)
            let delta = current - t
            if abs(delta) < 0.000_001 { break }
            let derivative = sampleCurveDerivativeX(guess, p1x: p1x, p2x: p2x)
            if abs(derivative) < 0.000_001 { break }
            guess -= delta / derivative
        }
        return sampleCurveY(min(max(guess, 0), 1), p1y: p1y, p2y: p2y)
    }

    static func alpha(for transaction: Transaction, progress: CGFloat, fadingOut: Bool) -> CGFloat {
        let t = min(max(progress, 0), 1)
        return fadingOut ? 1 - t : t
    }

    private static func detailOffset(side: CapacityDockEdge, amount: CGFloat) -> CGSize {
        switch side {
        case .left: CGSize(width: amount, height: 0)
        case .right: CGSize(width: -amount, height: 0)
        case .top: CGSize(width: 0, height: -amount)
        case .bottom: CGSize(width: 0, height: amount)
        }
    }

    private static func sampleCurveX(_ t: CGFloat, p1x: CGFloat, p2x: CGFloat) -> CGFloat {
        let a = 1 - 3 * p2x + 3 * p1x
        let b = 3 * p2x - 6 * p1x
        let c = 3 * p1x
        return ((a * t + b) * t + c) * t
    }

    private static func sampleCurveY(_ t: CGFloat, p1y: CGFloat, p2y: CGFloat) -> CGFloat {
        let a = 1 - 3 * p2y + 3 * p1y
        let b = 3 * p2y - 6 * p1y
        let c = 3 * p1y
        return ((a * t + b) * t + c) * t
    }

    private static func sampleCurveDerivativeX(_ t: CGFloat, p1x: CGFloat, p2x: CGFloat) -> CGFloat {
        3 * (1 - 3 * p2x + 3 * p1x) * t * t + 2 * (3 * p2x - 6 * p1x) * t + 3 * p1x
    }
}
