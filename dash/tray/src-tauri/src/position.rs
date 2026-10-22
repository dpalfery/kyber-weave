//! Popover placement against the taskbar, panel or menu-bar edge of the monitor that owns the
//! click. Carried from the inherited Windows tray (spec requirement 6.11) because it already
//! uses work-area bounds and HiDPI scaling; the tray wires it in task 8.1.

/// Converts the tray event to the coordinate space used by monitor lookup.
/// macOS uses logical desktop points; other platforms use physical pixels.
pub fn tray_click_anchor(
    tray: &tauri::tray::TrayIcon,
    position: tauri::PhysicalPosition<f64>,
) -> Option<(i32, i32)> {
    #[cfg(target_os = "macos")]
    {
        // The status item owns the event's backing scale. The popover may still
        // be on a different monitor, so its scale cannot decode a tray click.
        let scale = tray.with_inner_tray_icon(|inner| {
            let mtm = objc2::MainThreadMarker::new()?;
            let item = inner.ns_status_item()?;
            Some(item.button(mtm)?.window()?.backingScaleFactor())
        });
        match scale {
            Ok(Some(scale)) => {
                let (x, y) = macos_logical_point((position.x, position.y), scale);
                Some((x.round() as i32, y.round() as i32))
            }
            _ => {
                eprintln!("kyberdash-tray: status item scale unavailable; positioning from cursor");
                None
            }
        }
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = tray;
        Some((position.x as i32, position.y as i32))
    }
}

/// Positions the popover on the monitor selected by the click anchor or cursor.
///
/// `anchor` is prepared by `tray_click_anchor`: logical desktop points on macOS,
/// physical desktop pixels elsewhere.
/// Every `Some((x, y))` is used as-is, including negative and zero coordinates.
/// Callers signal an unavailable event anchor with `None`; only then is the cursor read.
/// If the selected point does not identify a monitor, placement tries the primary monitor.
///
/// Horizontal placement starts centred on the selected point, or a fallback anchor when
/// neither event anchor nor cursor is available, then applies a best-effort clamp using
/// work-area bounds. Placement maintains at least a scaled margin from the left and top
/// work-area edges; a popover that does not fit may extend beyond the right or bottom edge.
pub fn position_popover(window: &tauri::WebviewWindow, anchor: Option<(i32, i32)>) {
    const POPOVER_WIDTH_LOGICAL: f64 = 360.0;
    const POPOVER_HEIGHT_LOGICAL: f64 = 660.0;
    const MARGIN_LOGICAL: f64 = 8.0;

    let point = select_position_point(anchor, || {
        let cursor = window.cursor_position().ok()?;
        #[cfg(target_os = "macos")]
        return Some(macos_logical_point(
            (cursor.x, cursor.y),
            // Tao scales global cursor coordinates by the primary display,
            // independently of the display currently containing this window.
            window.primary_monitor().ok()??.scale_factor(),
        ));
        #[cfg(not(target_os = "macos"))]
        Some((cursor.x, cursor.y))
    });

    let monitor = point
        .and_then(|(x, y)| window.monitor_from_point(x, y).ok().flatten())
        .or_else(|| window.primary_monitor().ok().flatten());
    let Some(monitor) = monitor else {
        return;
    };

    #[cfg(not(target_os = "macos"))]
    let scale = monitor.scale_factor();
    #[cfg(target_os = "macos")]
    let scale = 1.0;
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

    // Tauri exposes monitor bounds in backing pixels, but its macOS lookup
    // and LogicalPosition setter use desktop points. Normalize all geometry,
    // including origins, before clamping; do not scale a global origin twice.
    #[cfg(target_os = "macos")]
    let (area_x, area_y, area_w, area_h, screen_pos, screen) = {
        let scale = monitor.scale_factor();
        let (x, y) = macos_logical_point((area_x as f64, area_y as f64), scale);
        let (w, h) = macos_logical_point((area_w as f64, area_h as f64), scale);
        (
            x.round() as i32,
            y.round() as i32,
            w.round() as i32,
            h.round() as i32,
            screen_pos.to_logical::<i32>(scale),
            screen.to_logical::<u32>(scale),
        )
    };

    let (anchor_x, anchor_y) = point
        .map(|(x, y)| (x as i32, y as i32))
        .unwrap_or((area_x + area_w - pop_w / 2 - margin, area_y + area_h));

    let min_x = area_x + margin;
    let max_x = (area_x + area_w - pop_w - margin).max(min_x);
    let x = (anchor_x - pop_w / 2).clamp(min_x, max_x);

    // Comparing the work area with the full screen's vertical bounds identifies reserved
    // top/bottom edges; left/right reservations do not select the opening direction. Using
    // work-area bounds aims to keep the popover clear of reserved space when it fits with
    // margins. It opens downward for a reserved top edge (or an anchor in the top half with
    // no reserved bottom edge); otherwise it opens upward.
    let trimmed_top = area_y > screen_pos.y;
    let trimmed_bottom = (area_y + area_h) < (screen_pos.y + screen.height as i32);
    let anchor_in_top_half = anchor_y < screen_pos.y + (screen.height as i32) / 2;
    let open_downward = trimmed_top || (!trimmed_bottom && anchor_in_top_half);

    let y = if open_downward {
        area_y + margin
    } else {
        (area_y + area_h - pop_h - margin).max(area_y + margin)
    };

    #[cfg(target_os = "macos")]
    let _ = window.set_position(tauri::LogicalPosition::new(x, y));
    #[cfg(not(target_os = "macos"))]
    let _ = window.set_position(tauri::PhysicalPosition::new(x, y));
}

/// Selects the event anchor in native global desktop coordinates, with a lazy cursor fallback.
///
/// Every `Some((x, y))` is used as-is, including negative and zero coordinates.
/// Callers report an unavailable event anchor as `None`, the only case that invokes
/// `cursor_position`.
fn select_position_point(
    anchor: Option<(i32, i32)>,
    cursor_position: impl FnOnce() -> Option<(f64, f64)>,
) -> Option<(f64, f64)> {
    anchor
        .map(|(x, y)| (x as f64, y as f64))
        .or_else(cursor_position)
}

#[cfg(any(target_os = "macos", test))]
fn macos_logical_point(point: (f64, f64), source_scale: f64) -> (f64, f64) {
    (point.0 / source_scale, point.1 / source_scale)
}

#[cfg(test)]
mod tests {
    use super::{macos_logical_point, select_position_point};
    use std::cell::Cell;

    #[test]
    fn a_retina_click_does_not_select_the_adjacent_display() {
        let point = macos_logical_point((4000.0, 24.0), 2.0);
        let primary = (0.0..3008.0, 0.0..1692.0);
        let adjacent = (3008.0..6016.0, 0.0..1692.0);

        assert!(primary.0.contains(&point.0) && primary.1.contains(&point.1));
        assert!(!adjacent.0.contains(&point.0));
        assert_eq!(point, (2000.0, 12.0));
    }

    #[test]
    fn the_clicked_display_scale_is_used_instead_of_the_previous_window_scale() {
        assert_eq!(macos_logical_point((8000.0, 24.0), 2.0), (4000.0, 12.0));
        assert_eq!(macos_logical_point((-3000.0, 12.0), 1.0), (-3000.0, 12.0));
    }

    #[test]
    fn scaled_negative_and_zero_coordinates_remain_valid() {
        assert_eq!(
            macos_logical_point((-3486.0, -2160.0), 2.0),
            (-1743.0, -1080.0)
        );
        assert_eq!(macos_logical_point((0.0, 0.0), 2.0), (0.0, 0.0));
        assert_eq!(macos_logical_point((0.0, -200.0), 2.0), (0.0, -100.0));
    }

    #[test]
    fn fractional_logical_cursor_coordinates_are_preserved() {
        assert_eq!(macos_logical_point((101.0, 201.0), 2.0), (50.5, 100.5));
    }

    #[test]
    fn a_primary_scaled_cursor_fallback_preserves_its_non_primary_monitor_point() {
        let point = select_position_point(None, || {
            // The cursor is over a 1x display, but Tao encodes it using the
            // primary display's 2x scale, regardless of the popover's scale.
            Some(macos_logical_point((-6000.0, 24.0), 2.0))
        });

        assert_eq!(point, Some((-3000.0, 12.0)));
    }

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
    fn no_point_is_available_when_anchor_and_cursor_are_missing() {
        let point = select_position_point(None, || None);

        assert_eq!(point, None);
    }
}
