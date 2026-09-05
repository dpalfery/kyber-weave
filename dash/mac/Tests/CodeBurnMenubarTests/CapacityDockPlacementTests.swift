import CoreGraphics
import Testing
@testable import CodeBurnMenubar

@Suite("Capacity Dock placement")
struct CapacityDockPlacementTests {
    private let visibleFrame = CGRect(x: 0, y: 0, width: 1440, height: 900)
    private let railSize = CGSize(width: 76, height: 100)

    @Test("default placement uses the notification-safe upper-right lane")
    func defaultPlacement() {
        let frame = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: nil
        )

        #expect(frame == CGRect(x: 1364, y: 644, width: 76, height: 100))
        #expect(frame.maxX == visibleFrame.maxX)
    }

    @Test("normalized endpoints clamp the rail between top and bottom insets")
    func normalizedEndpoints() {
        let top = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0
        )
        let bottom = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 1
        )

        #expect(top.maxY == 888)
        #expect(bottom.minY == 12)
    }

    @Test("normalized position round-trips across display sizes")
    func normalizedRoundTrip() {
        let frame = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42
        )
        let normalized = CapacityDockPlacement.normalizedTopOffset(
            railFrame: frame,
            visibleFrame: visibleFrame
        )
        let external = CGRect(x: 1440, y: -200, width: 2560, height: 1440)
        let moved = CapacityDockPlacement.railFrame(
            screenFrame: external,
            visibleFrame: external,
            size: railSize,
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: normalized
        )

        #expect(abs(normalized - 0.42) < 0.000_001)
        #expect(abs(CapacityDockPlacement.normalizedTopOffset(
            railFrame: moved,
            visibleFrame: external
        ) - 0.42) < 0.000_001)
    }

    @Test("expanding the rail preserves its existing top edge")
    func expansionKeepsTopAnchored() {
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
    }

    @Test("floating placement preserves generous screen margins and round-trips horizontally")
    func floatingPlacement() {
        let frame = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: nil,
            normalizedHorizontalOffset: 0.5,
            normalizedTopOffset: 0.42
        )
        let normalized = CapacityDockPlacement.normalizedHorizontalOffset(
            railFrame: frame,
            visibleFrame: visibleFrame
        )

        #expect(frame.minX > visibleFrame.minX)
        #expect(frame.maxX < visibleFrame.maxX)
        #expect(abs(normalized - 0.5) < 0.000_001)
    }

    @Test("drag geometry follows the absolute pointer instead of a moving window coordinate space")
    func pointerAnchoredDrag() {
        let startFrame = CGRect(x: 1300, y: 500, width: 88, height: 100)
        let startPointer = CGPoint(x: 1340, y: 550)
        let currentPointer = CGPoint(x: 900, y: 700)

        let first = CapacityDockPlacement.pointerAnchoredDragFrame(
            startFrame: startFrame,
            startPointer: startPointer,
            currentPointer: currentPointer,
            size: CGSize(width: 88, height: 100)
        )
        let repeated = CapacityDockPlacement.pointerAnchoredDragFrame(
            startFrame: startFrame,
            startPointer: startPointer,
            currentPointer: currentPointer,
            size: CGSize(width: 88, height: 100)
        )

        #expect(first == CGRect(x: 860, y: 650, width: 88, height: 100))
        #expect(repeated == first)
    }

    @Test("detached dragging stays inside the usable desktop")
    func detachedDragUsesVisibleFrame() {
        let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let usable = CGRect(x: 72, y: 80, width: 1368, height: 795)
        let aboveMenuBar = CGRect(x: 500, y: 850, width: 88, height: 100)
        let behindSideDock = CGRect(x: 50, y: 300, width: 88, height: 100)
        let behindBottomDock = CGRect(x: 500, y: 50, width: 88, height: 100)

        let topClamped = CapacityDockPlacement.clampedDragFrame(
            aboveMenuBar,
            screenFrame: screen,
            visibleFrame: usable
        )
        let sideClamped = CapacityDockPlacement.clampedDragFrame(
            behindSideDock,
            screenFrame: screen,
            visibleFrame: usable
        )
        let bottomClamped = CapacityDockPlacement.clampedDragFrame(
            behindBottomDock,
            screenFrame: screen,
            visibleFrame: usable
        )

        #expect(topClamped.maxY == usable.maxY)
        #expect(sideClamped.minX == usable.minX)
        #expect(bottomClamped.minY == usable.minY)
    }

    @Test("an active edge snap may reach the physical edge while its other axis stays usable")
    func edgeSnapPreservesRecoverability() {
        let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let usable = CGRect(x: 72, y: 48, width: 1368, height: 827)
        let nearBottom = CGRect(x: 500, y: 8, width: 88, height: 100)
        let nearRightAboveMenuBar = CGRect(x: 1360, y: 850, width: 88, height: 100)

        let bottom = CapacityDockPlacement.clampedDragFrame(
            nearBottom,
            screenFrame: screen,
            visibleFrame: usable
        )
        let right = CapacityDockPlacement.clampedDragFrame(
            nearRightAboveMenuBar,
            screenFrame: screen,
            visibleFrame: usable
        )

        #expect(bottom.minY == 8)
        #expect(right.maxX == screen.maxX)
        #expect(right.maxY == usable.maxY)
    }

    @Test("dragging selects the display currently containing the pointer")
    func dragCanCrossDisplays() {
        let screens = [
            CGRect(x: 0, y: 0, width: 1440, height: 900),
            CGRect(x: 1440, y: -200, width: 2560, height: 1440),
        ]

        #expect(CapacityDockPlacement.screenIndex(
            containing: CGPoint(x: 1800, y: 700),
            frames: screens
        ) == 1)
        #expect(CapacityDockPlacement.screenIndex(
            containing: CGPoint(x: 200, y: 700),
            frames: screens
        ) == 0)
    }

    @Test("left and right edge snap lanes resolve symmetrically")
    func dockSnapLane() {
        let nearRight = CGRect(x: 1342, y: 500, width: 76, height: 100)
        let nearLeft = CGRect(x: 18, y: 500, width: 76, height: 100)
        let detached = CGRect(x: 200, y: 500, width: 76, height: 100)

        #expect(CapacityDockPlacement.nearestDockEdge(
            railFrame: nearRight,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        ) == .right)
        #expect(CapacityDockPlacement.nearestDockEdge(
            railFrame: nearLeft,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        ) == .left)
        #expect(CapacityDockPlacement.nearestDockEdge(
            railFrame: detached,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        ) == nil)
    }

    @Test("attachment contact grows continuously while approaching an edge")
    func progressiveAttachment() {
        let far = CGRect(x: 1288, y: 500, width: 88, height: 112)
        let halfway = CGRect(x: 1330, y: 500, width: 88, height: 112)
        let touching = CGRect(x: 1352, y: 500, width: 88, height: 112)

        #expect(CapacityDockPlacement.attachmentCandidate(
            railFrame: far,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        ) == nil)
        let half = CapacityDockPlacement.attachmentCandidate(
            railFrame: halfway,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        )
        #expect(half?.edge == .right)
        #expect(abs((half?.progress ?? 0) - 0.5) < 0.000_001)
        #expect(CapacityDockPlacement.attachmentCandidate(
            railFrame: touching,
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame
        )?.progress == 1)
    }

    @Test("top snap lane follows the usable edge below the menu bar")
    func topDockSnapUsesVisibleFrame() {
        let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let usable = CGRect(x: 0, y: 0, width: 1440, height: 875)
        let nearUsableTop = CGRect(x: 620, y: 773, width: 200, height: 100)

        #expect(CapacityDockPlacement.nearestDockEdge(
            railFrame: nearUsableTop,
            screenFrame: screen,
            visibleFrame: usable
        ) == .top)

        let settled = CapacityDockPlacement.railFrame(
            screenFrame: screen,
            visibleFrame: usable,
            size: nearUsableTop.size,
            dockedEdge: .top,
            normalizedHorizontalOffset: 0.5,
            normalizedTopOffset: nil
        )
        // Top-docked rails sit flush with the physical top edge, not the menu-bar inset.
        #expect(settled.maxY == screen.maxY)
    }

    @Test("left-docked rail is exactly flush with the physical screen edge")
    func leftDockPlacement() {
        let frame = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: railSize,
            dockedEdge: .left,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42
        )

        #expect(frame.minX == visibleFrame.minX)
    }

    @Test("attached rail stays exactly flush at scaled sizes without relying on display clipping")
    func attachmentStaysInsideDisplay() {
        let right = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 110, height: 120),
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42
        )
        let left = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 110, height: 120),
            dockedEdge: .left,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: 0.42
        )

        #expect(right.maxX == visibleFrame.maxX)
        #expect(left.minX == visibleFrame.minX)
    }

    @Test("top and bottom rails are horizontal and flush with their physical edges")
    func horizontalEdgePlacement() {
        let horizontalSize = CGSize(width: 360, height: 100)
        let top = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: horizontalSize,
            dockedEdge: .top,
            normalizedHorizontalOffset: 0.5,
            normalizedTopOffset: nil
        )
        let bottom = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: horizontalSize,
            dockedEdge: .bottom,
            normalizedHorizontalOffset: 0.5,
            normalizedTopOffset: nil
        )

        #expect(top.maxY == visibleFrame.maxY)
        #expect(bottom.minY == visibleFrame.minY)
        #expect(top.width > top.height)
        #expect(bottom.width > bottom.height)
    }

    @Test("edge position chooses the end with room and keeps the resting provider stationary")
    func relativeExpansionAnchor() {
        let nearTop = CGRect(x: 1352, y: 760, width: 88, height: 112)
        let nearBottom = CGRect(x: 1352, y: 28, width: 88, height: 112)
        let nearLeft = CGRect(x: 28, y: 787, width: 112, height: 88)
        let nearRight = CGRect(x: 1300, y: 787, width: 112, height: 88)

        #expect(CapacityDockPlacement.expansionAnchor(
            railFrame: nearTop,
            visibleFrame: visibleFrame,
            edge: .right
        ) == .start)
        #expect(CapacityDockPlacement.expansionAnchor(
            railFrame: nearBottom,
            visibleFrame: visibleFrame,
            edge: .right
        ) == .end)
        #expect(CapacityDockPlacement.expansionAnchor(
            railFrame: nearLeft,
            visibleFrame: visibleFrame,
            edge: .top
        ) == .start)
        #expect(CapacityDockPlacement.expansionAnchor(
            railFrame: nearRight,
            visibleFrame: visibleFrame,
            edge: .top
        ) == .end)
    }

    @Test("expanded edge frames preserve the resting end instead of recentering")
    func relativeExpandedFrame() {
        let compactBottom = CGRect(x: 1352, y: 28, width: 88, height: 112)
        let expandedUp = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 88, height: 500),
            dockedEdge: .right,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: nil,
            anchoredAxisCoordinate: compactBottom.minY,
            expansionAnchor: .end
        )
        #expect(expandedUp.minY == compactBottom.minY)

        let compactRight = CGRect(x: 1300, y: 812, width: 112, height: 88)
        let expandedLeft = CapacityDockPlacement.railFrame(
            screenFrame: visibleFrame,
            visibleFrame: visibleFrame,
            size: CGSize(width: 700, height: 88),
            dockedEdge: .top,
            normalizedHorizontalOffset: nil,
            normalizedTopOffset: nil,
            anchoredAxisCoordinate: compactRight.maxX,
            expansionAnchor: .end
        )
        #expect(expandedLeft.maxX == compactRight.maxX)
    }

    @Test("detail bubble sits left of the rail and stays vertically centered on its row")
    func detailPlacement() {
        let rail = CGRect(x: 1352, y: 500, width: 76, height: 300)
        let detail = CapacityDockPlacement.detailFrame(
            size: CGSize(width: 300, height: 200),
            railFrame: rail,
            providerRowMidY: 700,
            visibleFrame: visibleFrame,
            side: .left
        )

        #expect(detail == CGRect(x: 1042, y: 600, width: 300, height: 200))
    }

    @Test("detail bubble clamps on narrow and offset displays")
    func detailClampsToVisibleFrame() {
        let narrow = CGRect(x: 800, y: 120, width: 320, height: 500)
        let rail = CGRect(x: 1032, y: 500, width: 76, height: 100)
        let detail = CapacityDockPlacement.detailFrame(
            size: CGSize(width: 300, height: 480),
            railFrame: rail,
            providerRowMidY: 590,
            visibleFrame: narrow,
            side: .left
        )

        #expect(detail.minX == 808)
        #expect(detail.minY == 132)
        #expect(detail.maxX <= narrow.maxX - 8)
        #expect(detail.maxY <= narrow.maxY - 8)
    }

    @Test("detail bubble opens inward from either dock edge")
    func detailPlacementMirrors() {
        let leftRail = CGRect(x: 0, y: 500, width: 100, height: 300)
        let rightRail = CGRect(x: 1340, y: 500, width: 100, height: 300)

        #expect(CapacityDockPlacement.preferredDetailSide(
            railFrame: leftRail,
            visibleFrame: visibleFrame,
            dockedEdge: .left
        ) == .right)
        #expect(CapacityDockPlacement.preferredDetailSide(
            railFrame: rightRail,
            visibleFrame: visibleFrame,
            dockedEdge: .right
        ) == .left)
    }

    @Test("detached horizontal rail keeps its last edge orientation when it moves")
    func detachedHorizontalDetailSideIsStable() {
        let movedNearRight = CGRect(x: 980, y: 360, width: 300, height: 88)

        #expect(CapacityDockPlacement.preferredDetailSide(
            railFrame: movedNearRight,
            visibleFrame: visibleFrame,
            dockedEdge: nil,
            preferredEdge: .bottom
        ) == .top)

        let detail = CapacityDockPlacement.detailFrame(
            size: CGSize(width: 350, height: 220),
            railFrame: movedNearRight,
            providerRowMidY: 1130,
            visibleFrame: visibleFrame,
            side: .top
        )
        #expect(detail.midX == 1130)
        #expect(detail.minY == movedNearRight.maxY + CapacityDockPlacement.detailGap)
    }

    @Test("detached detail cards stay on the rail axis and choose the side with room")
    func detachedDetailSideFollowsCurrentPosition() {
        let horizontalNearTop = CGRect(x: 540, y: 780, width: 300, height: 88)
        #expect(CapacityDockPlacement.preferredDetailSide(
            railFrame: horizontalNearTop,
            visibleFrame: visibleFrame,
            dockedEdge: nil,
            preferredEdge: .top
        ) == .bottom)

        let verticalNearLeft = CGRect(x: 20, y: 300, width: 88, height: 300)
        #expect(CapacityDockPlacement.preferredDetailSide(
            railFrame: verticalNearLeft,
            visibleFrame: visibleFrame,
            dockedEdge: nil,
            preferredEdge: .left
        ) == .right)
    }

    @Test("provider row hit test resolves bands, gaps, and both anchors")
    func providerRowHitTest() {
        func index(_ offset: CGFloat, anchor: CapacityDockExpansionAnchor = .start) -> Int? {
            CapacityDockPlacement.providerRowIndex(
                alongOffset: offset,
                rowHeight: 84,
                rowSpacing: 12,
                rowCount: 3,
                expansionAnchor: anchor
            )
        }

        #expect(index(0) == 0)
        #expect(index(84) == 0)
        #expect(index(90) == nil)
        #expect(index(96) == 1)
        #expect(index(240) == 2)
        #expect(index(276) == 2)
        #expect(index(277) == nil)
        #expect(index(-1) == nil)

        #expect(index(0, anchor: .end) == 2)
        #expect(index(96, anchor: .end) == 1)
        #expect(index(240, anchor: .end) == 0)

        #expect(CapacityDockPlacement.providerRowIndex(
            alongOffset: 10,
            rowHeight: 84,
            rowSpacing: 12,
            rowCount: 0,
            expansionAnchor: .start
        ) == nil)
    }
}
