import CoreGraphics
import Testing
@testable import CodeBurnMenubar

@Suite("Capacity Dock motion")
struct CapacityDockMotionTests {
    @Test("rail and bubble durations stay mac-like and vanish under Reduce Motion")
    func durations() {
        #expect(CapacityDockMotion.duration(for: .railExpand) == 0.52)
        #expect(CapacityDockMotion.duration(for: .railCollapse) == 0.44)
        #expect(CapacityDockMotion.duration(for: .dockAttach) == 0.28)
        #expect(CapacityDockMotion.duration(for: .dockDetach) == 0.24)
        #expect(CapacityDockMotion.duration(for: .detailPresent) == 0.20)
        #expect(CapacityDockMotion.duration(for: .detailFollow) == 0.22)
        #expect(CapacityDockMotion.duration(for: .detailDismiss) == 0.14)
        #expect(CapacityDockMotion.duration(for: .immediate) == 0)

        for transaction in [
            CapacityDockMotion.Transaction.railExpand,
            .railCollapse,
            .dockAttach,
            .dockDetach,
            .detailPresent,
            .detailFollow,
            .detailDismiss,
            .immediate,
        ] {
            #expect(CapacityDockMotion.duration(for: transaction, reduceMotion: true) == 0)
            #expect(!CapacityDockMotion.shouldAnimate(transaction, reduceMotion: true))
        }

        #expect(CapacityDockMotion.shouldAnimate(.railExpand))
        #expect(!CapacityDockMotion.shouldAnimate(.immediate))
    }

    @Test("hover intent is quick to open and forgiving to leave")
    func hoverIntentTiming() {
        #expect(CapacityDockMotion.railHoverOpenDelay == 0.08)
        #expect(CapacityDockMotion.railHoverCloseDelay == 0.18)
    }

    @Test("either rail axis picks expand, collapse, attach, or an atomic update")
    func railTransactionFromHeight() {
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            toFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            attachmentFrom: 0,
            attachmentTo: 0
        ) == .immediate)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            toFrame: CGRect(x: 0, y: 0, width: 88, height: 360),
            attachmentFrom: 0,
            attachmentTo: 0
        ) == .railExpand)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 88, height: 360),
            toFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            attachmentFrom: 0,
            attachmentTo: 0
        ) == .railCollapse)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            toFrame: CGRect(x: 0, y: 0, width: 110, height: 100),
            attachmentFrom: 0,
            attachmentTo: 1
        ) == .dockAttach)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 110, height: 100),
            toFrame: CGRect(x: 0, y: 0, width: 88, height: 100),
            attachmentFrom: 1,
            attachmentTo: 0
        ) == .dockDetach)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 100, height: 88),
            toFrame: CGRect(x: 0, y: 0, width: 360, height: 88),
            attachmentFrom: 1,
            attachmentTo: 1
        ) == .railExpand)
        #expect(CapacityDockMotion.railTransaction(
            fromFrame: CGRect(x: 0, y: 0, width: 360, height: 88),
            toFrame: CGRect(x: 0, y: 0, width: 100, height: 88),
            attachmentFrom: 1,
            attachmentTo: 1
        ) == .railCollapse)
    }

    @Test("bubble presentation slides out from the rail and dismisses back toward it")
    func detailOffsets() {
        let target = CGRect(x: 1042, y: 600, width: 300, height: 200)
        let start = CapacityDockMotion.detailPresentationStartFrame(from: target, side: .left)
        let dismiss = CapacityDockMotion.detailDismissalFrame(from: target, side: .left)

        #expect(start.size == target.size)
        #expect(start.minY == target.minY)
        #expect(start.minX == target.minX + 10)
        #expect(dismiss.minX > target.minX)
        #expect(dismiss.minX < start.minX)
        #expect(dismiss.size == target.size)

        let topStart = CapacityDockMotion.detailPresentationStartFrame(from: target, side: .top)
        let bottomStart = CapacityDockMotion.detailPresentationStartFrame(from: target, side: .bottom)
        #expect(topStart.minY < target.minY)
        #expect(bottomStart.minY > target.minY)
    }

    @Test("timing curves stay in a cubic-bezier range and linearize immediate updates")
    func timingControlPoints() {
        let immediate = CapacityDockMotion.timingControlPoints(for: .immediate)
        #expect(immediate == (0, 0, 1, 1))
        #expect(CapacityDockMotion.timingControlPoints(for: .railExpand) == (0.22, 1, 0.36, 1))
        #expect(CapacityDockMotion.timingControlPoints(for: .railCollapse) == (0.32, 0, 0.2, 1))

        for transaction in [
            CapacityDockMotion.Transaction.railExpand,
            .railCollapse,
            .dockAttach,
            .dockDetach,
            .detailPresent,
            .detailFollow,
            .detailDismiss,
        ] {
            let points = CapacityDockMotion.timingControlPoints(for: transaction)
            for value in [points.0, points.1, points.2, points.3] {
                #expect(value >= 0)
                #expect(value <= 1)
            }
        }
    }

    @Test("expanding geometry keeps the top edge while motion policy chooses a downward grow")
    func expansionStaysTopAnchored() {
        let visibleFrame = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let resting = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 76, height: 100),
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42
        )
        let expanded = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 76, height: 360),
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42,
            anchoredTop: resting.maxY
        )

        #expect(expanded.maxY == resting.maxY)
        #expect(expanded.minY < resting.minY)
        #expect(
            CapacityDockMotion.railTransaction(
                fromFrame: resting,
                toFrame: expanded,
                attachmentFrom: 1,
                attachmentTo: 1
            ) == .railExpand
        )
    }

    @Test("top-anchored interpolation grows downward without moving the top edge")
    func topAnchoredInterpolation() {
        let from = CGRect(x: 1352, y: 644, width: 76, height: 100)
        let to = CGRect(x: 1352, y: 384, width: 76, height: 360)

        let mid = CapacityDockMotion.interpolateTopAnchored(from: from, to: to, progress: 0.5)
        #expect(mid.maxY == from.maxY)
        #expect(mid.maxY == to.maxY)
        #expect(mid.minY == 514)
        #expect(mid.height == 230)

        let start = CapacityDockMotion.interpolateTopAnchored(from: from, to: to, progress: 0)
        let end = CapacityDockMotion.interpolateTopAnchored(from: from, to: to, progress: 1)
        #expect(start == from)
        #expect(end == to)
    }

    @Test("attachment interpolation keeps every physical edge stationary")
    func attachedEdgeInterpolation() {
        let from = CGRect(x: 10, y: 20, width: 88, height: 100)
        let targets: [(CapacityDockEdge, CGRect)] = [
            (.left, CGRect(x: 0, y: 20, width: 110, height: 100)),
            (.right, CGRect(x: 1330, y: 20, width: 110, height: 100)),
            (.top, CGRect(x: 10, y: 790, width: 100, height: 110)),
            (.bottom, CGRect(x: 10, y: 0, width: 100, height: 110)),
        ]

        for (edge, target) in targets {
            let mid = CapacityDockMotion.interpolateAttachedEdge(
                from: from,
                to: target,
                edge: edge,
                progress: 0.5
            )
            switch edge {
            case .left: #expect(mid.minX == 5)
            case .right: #expect(mid.maxX == (from.maxX + target.maxX) / 2)
            case .top: #expect(mid.maxY == (from.maxY + target.maxY) / 2)
            case .bottom: #expect(mid.minY == 10)
            }
        }
    }

    @Test("rail expansion preserves a stable anchor in floating and docked modes")
    func railExpansionAnchorPolicy() {
        let floatingFrom = CGRect(x: 640, y: 500, width: 88, height: 112)
        let floatingTo = CGRect(x: 640, y: 224, width: 88, height: 388)
        let rightFrom = CGRect(x: 1330, y: 500, width: 110, height: 112)
        let rightTo = CGRect(x: 1330, y: 224, width: 110, height: 388)

        for progress: CGFloat in [0.15, 0.5, 0.85] {
            let floating = CapacityDockMotion.interpolateRail(
                from: floatingFrom,
                to: floatingTo,
                dockedEdge: nil,
                progress: progress
            )
            #expect(floating.minX == floatingFrom.minX)
            #expect(floating.maxY == floatingFrom.maxY)

            let attached = CapacityDockMotion.interpolateRail(
                from: rightFrom,
                to: rightTo,
                dockedEdge: .right,
                expansionAnchor: .start,
                progress: progress
            )
            #expect(attached.maxX == rightFrom.maxX)
            #expect(attached.maxY == rightFrom.maxY)
        }


        let bottomFrom = CGRect(x: 1352, y: 24, width: 88, height: 112)
        let bottomTo = CGRect(x: 1352, y: 24, width: 88, height: 388)
        for progress: CGFloat in [0.15, 0.5, 0.85] {
            let attached = CapacityDockMotion.interpolateRail(
                from: bottomFrom,
                to: bottomTo,
                dockedEdge: .right,
                expansionAnchor: .end,
                progress: progress
            )
            #expect(attached.minY == bottomFrom.minY)
            #expect(attached.maxX == bottomFrom.maxX)
        }
    }

    @Test("floating layout anchors preserve the requested expansion direction")
    func floatingLayoutAnchors() {
        let frame = CGRect(x: 500.25, y: 300.25, width: 112, height: 88)

        let verticalStart = CapacityDockMotion.floatingRailAnchors(
            frame: frame,
            preservedTop: 420.5,
            isVertical: true,
            expansionAnchor: .start
        )
        #expect(verticalStart.top == 420.5)
        #expect(verticalStart.leading == frame.minX)
        #expect(verticalStart.axisCoordinate == nil)

        let verticalEnd = CapacityDockMotion.floatingRailAnchors(
            frame: frame,
            preservedTop: 420.5,
            isVertical: true,
            expansionAnchor: .end
        )
        #expect(verticalEnd.top == nil)
        #expect(verticalEnd.leading == frame.minX)
        #expect(verticalEnd.axisCoordinate == frame.minY)

        let horizontalStart = CapacityDockMotion.floatingRailAnchors(
            frame: frame,
            preservedTop: 420.5,
            isVertical: false,
            expansionAnchor: .start
        )
        #expect(horizontalStart.top == 420.5)
        #expect(horizontalStart.leading == frame.minX)
        #expect(horizontalStart.axisCoordinate == nil)

        let horizontalEnd = CapacityDockMotion.floatingRailAnchors(
            frame: frame,
            preservedTop: 420.5,
            isVertical: false,
            expansionAnchor: .end
        )
        #expect(horizontalEnd.top == 420.5)
        #expect(horizontalEnd.leading == nil)
        #expect(horizontalEnd.axisCoordinate == frame.maxX)
    }

    @Test("rail frames land on backing pixels without releasing their visual anchor")
    func railFramesAlignToBackingPixels() {
        let floating = CapacityDockMotion.pixelAlignedRailFrame(
            CGRect(x: 640.13, y: 500.08, width: 88.17, height: 112.19),
            backingScale: 2,
            dockedEdge: nil
        )
        #expect(floating.minX == 640)
        #expect(floating.maxY == 612.5)
        #expect(floating.width == 88)
        #expect(floating.height == 112)

        let attached = CapacityDockMotion.pixelAlignedRailFrame(
            CGRect(x: 1330.08, y: 499.94, width: 109.87, height: 112.19),
            backingScale: 2,
            dockedEdge: .right
        )
        #expect(attached.maxX == 1440)
        #expect(attached.maxY == 612)

        let bottomAnchored = CapacityDockMotion.pixelAlignedRailFrame(
            CGRect(x: 1330.08, y: 28.13, width: 109.87, height: 112.19),
            backingScale: 2,
            dockedEdge: .right,
            expansionAnchor: .end
        )
        #expect(bottomAnchored.minY == 28)
        #expect(bottomAnchored.maxX == 1440)

        let rightAnchored = CapacityDockMotion.pixelAlignedRailFrame(
            CGRect(x: 800.13, y: 790.08, width: 612.19, height: 88.17),
            backingScale: 2,
            dockedEdge: .top,
            expansionAnchor: .end
        )
        #expect(rightAnchored.maxX == 1412.5)
        #expect(rightAnchored.maxY == 878.5)
    }

    @Test("pixel-aligned reveal progress follows the frame instead of the timer")
    func alignedRevealProgressFollowsFrame() {
        let from = CGRect(x: 640.13, y: 500.08, width: 88.17, height: 112.19)
        let to = CGRect(x: 640.13, y: 224.08, width: 88.17, height: 388.19)

        let firstRaw = CapacityDockMotion.interpolateRail(
            from: from,
            to: to,
            dockedEdge: nil,
            progress: 0.4001
        )
        let secondRaw = CapacityDockMotion.interpolateRail(
            from: from,
            to: to,
            dockedEdge: nil,
            progress: 0.4002
        )
        let first = CapacityDockMotion.alignedRailSample(
            firstRaw,
            fromFrame: from,
            toFrame: to,
            fromPresentationProgress: 0,
            toPresentationProgress: 1,
            backingScale: 2,
            dockedEdge: nil,
            expansionAnchor: .start,
            isVertical: true
        )
        let second = CapacityDockMotion.alignedRailSample(
            secondRaw,
            fromFrame: from,
            toFrame: to,
            fromPresentationProgress: 0,
            toPresentationProgress: 1,
            backingScale: 2,
            dockedEdge: nil,
            expansionAnchor: .start,
            isVertical: true
        )

        #expect(first.frame == second.frame)
        #expect(first.presentationProgress == second.presentationProgress)
        #expect(abs(first.frame.height - (112 + 276 * first.presentationProgress)) < 0.000_001)
    }

    @Test("fractional rail motion never releases its snapped anchor")
    func fractionalMotionKeepsSnappedAnchor() {
        let verticalFrom = CGRect(x: 640.13, y: 500.08, width: 88.17, height: 112.19)
        let verticalTo = CGRect(x: 640.13, y: 224.08, width: 88.17, height: 388.19)
        let horizontalFrom = CGRect(x: 500.08, y: 740.13, width: 112.19, height: 88.17)
        let horizontalTo = CGRect(x: 224.08, y: 740.13, width: 388.19, height: 88.17)
        var priorVerticalHeight: CGFloat = 0
        var priorHorizontalWidth: CGFloat = 0

        for step in 0...120 {
            let progress = CGFloat(step) / 120
            let verticalRaw = CapacityDockMotion.interpolateRail(
                from: verticalFrom,
                to: verticalTo,
                dockedEdge: nil,
                expansionAnchor: .start,
                progress: progress
            )
            let vertical = CapacityDockMotion.alignedRailSample(
                verticalRaw,
                fromFrame: verticalFrom,
                toFrame: verticalTo,
                fromPresentationProgress: 0,
                toPresentationProgress: 1,
                backingScale: 2,
                dockedEdge: nil,
                expansionAnchor: .start,
                isVertical: true
            )
            #expect(vertical.frame.maxY == 612.5)
            #expect(vertical.frame.height >= priorVerticalHeight)
            priorVerticalHeight = vertical.frame.height

            let horizontalRaw = CapacityDockMotion.interpolateRail(
                from: horizontalFrom,
                to: horizontalTo,
                dockedEdge: nil,
                expansionAnchor: .end,
                progress: progress
            )
            let horizontal = CapacityDockMotion.alignedRailSample(
                horizontalRaw,
                fromFrame: horizontalFrom,
                toFrame: horizontalTo,
                fromPresentationProgress: 0,
                toPresentationProgress: 1,
                backingScale: 2,
                dockedEdge: nil,
                expansionAnchor: .end,
                isVertical: false
            )
            #expect(horizontal.frame.maxX == 612.5)
            #expect(horizontal.frame.width >= priorHorizontalWidth)
            priorHorizontalWidth = horizontal.frame.width
        }

        priorVerticalHeight = .greatestFiniteMagnitude
        priorHorizontalWidth = .greatestFiniteMagnitude
        for step in 0...120 {
            let progress = CGFloat(step) / 120
            let verticalRaw = CapacityDockMotion.interpolateRail(
                from: verticalTo,
                to: verticalFrom,
                dockedEdge: nil,
                expansionAnchor: .start,
                progress: progress
            )
            let vertical = CapacityDockMotion.alignedRailSample(
                verticalRaw,
                fromFrame: verticalTo,
                toFrame: verticalFrom,
                fromPresentationProgress: 1,
                toPresentationProgress: 0,
                backingScale: 2,
                dockedEdge: nil,
                expansionAnchor: .start,
                isVertical: true
            )
            #expect(vertical.frame.maxY == 612.5)
            #expect(vertical.frame.height <= priorVerticalHeight)
            priorVerticalHeight = vertical.frame.height

            let horizontalRaw = CapacityDockMotion.interpolateRail(
                from: horizontalTo,
                to: horizontalFrom,
                dockedEdge: nil,
                expansionAnchor: .end,
                progress: progress
            )
            let horizontal = CapacityDockMotion.alignedRailSample(
                horizontalRaw,
                fromFrame: horizontalTo,
                toFrame: horizontalFrom,
                fromPresentationProgress: 1,
                toPresentationProgress: 0,
                backingScale: 2,
                dockedEdge: nil,
                expansionAnchor: .end,
                isVertical: false
            )
            #expect(horizontal.frame.maxX == 612.5)
            #expect(horizontal.frame.width <= priorHorizontalWidth)
            priorHorizontalWidth = horizontal.frame.width
        }
    }

    @Test("immediate easing is linear and other curves stay bounded")
    func easedProgressBounds() {
        #expect(CapacityDockMotion.easedProgress(for: .immediate, linear: 0.37) == 0.37)
        #expect(CapacityDockMotion.easedProgress(for: .railExpand, linear: 0) == 0)
        #expect(CapacityDockMotion.easedProgress(for: .railExpand, linear: 1) == 1)

        let mid = CapacityDockMotion.easedProgress(for: .railExpand, linear: 0.5)
        #expect(mid > 0)
        #expect(mid < 1)
        #expect(CapacityDockMotion.easedProgress(for: .railExpand, linear: 0.25) > 0.5)
    }

    @Test("bubble alpha fades in on present and out on dismiss")
    func detailAlpha() {
        #expect(CapacityDockMotion.alpha(for: .detailPresent, progress: 0, fadingOut: false) == 0)
        #expect(CapacityDockMotion.alpha(for: .detailPresent, progress: 1, fadingOut: false) == 1)
        #expect(CapacityDockMotion.alpha(for: .detailDismiss, progress: 0, fadingOut: true) == 1)
        #expect(CapacityDockMotion.alpha(for: .detailDismiss, progress: 1, fadingOut: true) == 0)
    }
}
