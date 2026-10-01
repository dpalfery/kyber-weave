//! Popover placement against the taskbar, panel or menu-bar edge of the monitor that owns the
//! click. Carried from the inherited Windows tray (spec requirement 6.11) because it already
//! handles every taskbar edge and HiDPI scaling; the tray wires it in task 8.1.

/// Places the popover against the taskbar / panel edge of the monitor that owns the click
/// (or the cursor, when the request came from a menu). The work area already excludes the
/// taskbar on Windows and panels on Linux, so we never need to guess their heights: the
/// popover sits `MARGIN` inside the work area, horizontally centred on the anchor and
/// clamped to the screen.
pub fn position_popover(window: &tauri::WebviewWindow, anchor: Option<(i32, i32)>) {
    const POPOVER_WIDTH_LOGICAL: f64 = 360.0;
    const POPOVER_HEIGHT_LOGICAL: f64 = 660.0;
    const MARGIN_LOGICAL: f64 = 8.0;

    let point = select_position_point(anchor, || window.cursor_position().ok().map(|p| (p.x, p.y)));

    let monitor = point
        .and_then(|(x, y)| window.monitor_from_point(x, y).ok().flatten())
        .or_else(|| window.primary_monitor().ok().flatten());
    let Some(monitor) = monitor else {
        return;
    };

    let scale = monitor.scale_factor();
    let pop_w = (POPOVER_WIDTH_LOGICAL * scale).round() as i32;
    let pop_h = (POPOVER_HEIGHT_LOGICAL * scale).round() as i32;
    let margin = (MARGIN_LOGICAL * scale).round() as i32;

    let area = monitor.work_area();
    let area_x = area.position.x;
    let area_y = area.position.y;
    let area_w = area.size.width as i32;
    let area_h = area.size.height as i32;
    let screen = monitor.size();
    let screen_pos = monitor.position();

    let (anchor_x, anchor_y) = point
        .map(|(x, y)| (x as i32, y as i32))
        .unwrap_or((area_x + area_w - pop_w / 2 - margin, area_y + area_h));

    let min_x = area_x + margin;
    let max_x = (area_x + area_w - pop_w - margin).max(min_x);
    let x = (anchor_x - pop_w / 2).clamp(min_x, max_x);

    // Which edge holds the taskbar? Whichever side the work area was trimmed on. If the
    // taskbar is at the top (or the anchor is in the top half with no bottom taskbar) the
    // popover drops down from the top edge; otherwise it rises from the bottom edge.
    let trimmed_top = area_y > screen_pos.y;
    let trimmed_bottom = (area_y + area_h) < (screen_pos.y + screen.height as i32);
    let anchor_in_top_half = anchor_y < screen_pos.y + (screen.height as i32) / 2;
    let open_downward = trimmed_top || (!trimmed_bottom && anchor_in_top_half);

    let y = if open_downward {
        area_y + margin
    } else {
        (area_y + area_h - pop_h - margin).max(area_y + margin)
    };

    let _ = window.set_position(tauri::PhysicalPosition::new(x, y));
}

fn select_position_point(
    anchor: Option<(i32, i32)>,
    cursor_position: impl FnOnce() -> Option<(f64, f64)>,
) -> Option<(f64, f64)> {
    anchor
        .map(|(x, y)| (x as f64, y as f64))
        .or_else(cursor_position)
}

#[cfg(test)]
mod tests {
    use super::select_position_point;
    use std::cell::Cell;

    // Issue #178: virtual desktop coordinates can be negative when a display is left of
    // or above the primary display. The click must still select its own display.
    #[test]
    fn a_negative_tray_anchor_wins_over_a_cursor_on_another_display() {
        let point = select_position_point(Some((-1743, -1080)), || Some((800.0, 600.0)));

        assert_eq!(point, Some((-1743.0, -1080.0)));
    }

    #[test]
    fn an_anchor_at_the_virtual_desktop_origin_remains_valid() {
        let point = select_position_point(Some((0, 0)), || Some((800.0, 600.0)));

        assert_eq!(point, Some((0.0, 0.0)));
    }

    #[test]
    fn an_anchor_above_the_origin_on_the_vertical_axis_remains_valid() {
        let point = select_position_point(Some((0, -100)), || Some((800.0, 600.0)));

        assert_eq!(point, Some((0.0, -100.0)));
    }

    #[test]
    fn an_anchor_left_of_the_origin_on_the_horizontal_axis_remains_valid() {
        let point = select_position_point(Some((-100, 0)), || Some((800.0, 600.0)));

        assert_eq!(point, Some((-100.0, 0.0)));
    }

    #[test]
    fn a_positive_anchor_wins_over_the_cursor() {
        let point = select_position_point(Some((120, 240)), || Some((-1743.0, -1080.0)));

        assert_eq!(point, Some((120.0, 240.0)));
    }

    #[test]
    fn mixed_sign_anchors_remain_valid() {
        for anchor in [(-100, 240), (120, -240)] {
            let point = select_position_point(Some(anchor), || Some((800.0, 600.0)));

            assert_eq!(
                point,
                Some((anchor.0 as f64, anchor.1 as f64)),
                "anchor {anchor:?} must retain both coordinates"
            );
        }
    }

    #[test]
    fn a_supplied_anchor_never_queries_the_cursor() {
        for anchor in [
            (-1743, -1080),
            (0, 0),
            (0, -100),
            (-100, 0),
            (120, 240),
            (-100, 240),
            (120, -240),
        ] {
            let cursor_queries = Cell::new(0);

            select_position_point(Some(anchor), || {
                cursor_queries.set(cursor_queries.get() + 1);
                Some((800.0, 600.0))
            });

            assert_eq!(
                cursor_queries.get(),
                0,
                "anchor {anchor:?} must avoid the cursor fallback"
            );
        }
    }

    #[test]
    fn a_missing_anchor_uses_the_cursor_position() {
        let point = select_position_point(None, || Some((-1743.25, -1080.75)));

        assert_eq!(point, Some((-1743.25, -1080.75)));
    }

    #[test]
    fn unavailable_anchor_and_cursor_leave_the_primary_monitor_fallback_available() {
        let point = select_position_point(None, || None);

        assert_eq!(point, None);
    }
}
