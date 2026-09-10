import Foundation

/// Immediate interaction truth for Capacity Dock. Hover/collapse delays are
/// scheduled by the controller; this value only decides whether a delayed
/// action is still valid when it fires.
struct CapacityDockInteractionState: Equatable, Sendable {
    private(set) var isRailHovered = false
    private(set) var isDetailHovered = false
    private(set) var isPinned = false
    private(set) var isCollapseGraceActive = false
    private(set) var isDragging = false

    init(
        isRailHovered: Bool = false,
        isDetailHovered: Bool = false,
        isPinned: Bool = false
    ) {
        self.isRailHovered = isRailHovered
        self.isDetailHovered = isDetailHovered
        self.isPinned = isPinned
    }

    var isExpanded: Bool {
        isPinned || isRailHovered || isDetailHovered || isCollapseGraceActive
    }
    var canCollapse: Bool {
        !isPinned && !isRailHovered && !isDetailHovered && !isCollapseGraceActive && !isDragging
    }
    var acceptsHoverTransitions: Bool { !isDragging }

    mutating func setRailHovered(_ hovered: Bool) {
        isRailHovered = hovered
        if hovered { isCollapseGraceActive = false }
    }

    mutating func beginRailExitGrace() {
        isRailHovered = false
        isCollapseGraceActive = true
    }

    mutating func completeCollapseGrace() {
        isCollapseGraceActive = false
    }

    mutating func beginDragging() {
        isDragging = true
    }

    mutating func endDragging() {
        isDragging = false
    }

    mutating func setDetailHovered(_ hovered: Bool) {
        isDetailHovered = hovered
    }

    mutating func togglePinned() {
        isPinned.toggle()
    }

    mutating func dismiss() {
        self = CapacityDockInteractionState()
    }

    @discardableResult
    mutating func handleEscape() -> Bool {
        guard isExpanded else { return false }
        dismiss()
        return true
    }
}
