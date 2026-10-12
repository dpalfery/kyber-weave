//! Native status icon rendering, plus reusable text-badge helpers.
//!
//! Both desktop platforms display collector state as a coloured KyberDash lightsaber
//! drawn at runtime ([`render_lightsaber`]). The text renderer remains available for badges.
//! Neither renderer derives status: both are handed the verdict
//! `status_item::status_item_with_coverage` already read from the shared coverage API.

use tauri::image::Image;

const GLYPH_HEIGHT: usize = 7;
const BASE_ICON_SIZE: u32 = 16;
const GLYPH_GAP: usize = 1;
/// Brand accent on a light taskbar, the lighter ember on a dark one.
const ACCENT_LIGHT_TASKBAR: [u8; 3] = [0xC9, 0x52, 0x1D];
const ACCENT_DARK_TASKBAR: [u8; 3] = [0xF0, 0x8A, 0x55];

struct Glyph {
    width: usize,
    rows: [&'static str; GLYPH_HEIGHT],
}

fn glyph(c: char) -> Option<Glyph> {
    let g = |width: usize, rows: [&'static str; GLYPH_HEIGHT]| Some(Glyph { width, rows });
    match c {
        '0' => g(4, [".##.", "#..#", "#..#", "#..#", "#..#", "#..#", ".##."]),
        '1' => g(3, [".#.", "##.", ".#.", ".#.", ".#.", ".#.", "###"]),
        '2' => g(4, [".##.", "#..#", "...#", "..#.", ".#..", "#...", "####"]),
        '3' => g(4, ["###.", "...#", "...#", ".##.", "...#", "...#", "###."]),
        '4' => g(4, ["#..#", "#..#", "#..#", "####", "...#", "...#", "...#"]),
        '5' => g(4, ["####", "#...", "#...", "###.", "...#", "...#", "###."]),
        '6' => g(4, [".##.", "#...", "#...", "###.", "#..#", "#..#", ".##."]),
        '7' => g(4, ["####", "...#", "..#.", "..#.", ".#..", ".#..", ".#.."]),
        '8' => g(4, [".##.", "#..#", "#..#", ".##.", "#..#", "#..#", ".##."]),
        '9' => g(4, [".##.", "#..#", "#..#", ".###", "...#", "...#", ".##."]),
        '$' => g(4, ["..#.", ".###", "#.#.", ".##.", ".#.#", "###.", "..#."]),
        'K' => g(4, ["#..#", "#.#.", "##..", "#...", "##..", "#.#.", "#..#"]),
        'M' => g(4, ["#..#", "####", "####", "#..#", "#..#", "#..#", "#..#"]),
        '.' => g(1, [".", ".", ".", ".", ".", ".", "#"]),
        _ => None,
    }
}

fn text_width(text: &str) -> usize {
    let glyphs: Vec<Glyph> = text.chars().filter_map(glyph).collect();
    if glyphs.is_empty() {
        return 0;
    }
    glyphs.iter().map(|g| g.width).sum::<usize>() + GLYPH_GAP * (glyphs.len() - 1)
}

/// Draws `text` centred in a `size` x `size` RGBA icon. Prefers anti-aliased bold system
/// text (far more legible at 16px than 1px pixel strokes); falls back to the pixel font
/// when no usable font file is present.
pub fn render(text: &str, size: u32, dark_taskbar: bool) -> Image<'static> {
    let color = if dark_taskbar {
        ACCENT_DARK_TASKBAR
    } else {
        ACCENT_LIGHT_TASKBAR
    };
    if let Some(image) = render_with_font(text, size, color) {
        return image;
    }
    // The pixel font cannot shrink, so trim from the right until the string fits.
    let mut trimmed: String = text.to_string();
    while !fits(&trimmed) && trimmed.pop().is_some() {}
    render_pixel_font(&trimmed, size, dark_taskbar)
}

/// Bold sans faces shipped with Windows, best first. Bahnschrift's condensed numerals fit
/// four glyphs into 16px at a larger size than Segoe UI Bold does.
#[cfg(target_os = "windows")]
const FONT_CANDIDATES: [&str; 3] = [
    r"C:\Windows\Fonts\bahnschrift.ttf",
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\arialbd.ttf",
];
#[cfg(not(target_os = "windows"))]
const FONT_CANDIDATES: [&str; 3] = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
];

/// Largest and smallest text sizes tried, as a fraction of the icon size.
const FONT_MAX_FRACTION: f32 = 0.95;
const FONT_MIN_FRACTION: f32 = 0.5;
const FONT_STEP_PX: f32 = 0.5;

/// Read and parsed once: the badge re-renders on every refresh and the font file does not
/// change under a running session.
fn font() -> Option<&'static fontdue::Font> {
    static FONT: std::sync::OnceLock<Option<fontdue::Font>> = std::sync::OnceLock::new();
    FONT.get_or_init(load_font).as_ref()
}

fn load_font() -> Option<fontdue::Font> {
    for path in FONT_CANDIDATES {
        if let Ok(bytes) = std::fs::read(path) {
            if let Ok(font) = fontdue::Font::from_bytes(bytes, fontdue::FontSettings::default()) {
                return Some(font);
            }
        }
    }
    None
}

struct Raster {
    metrics: fontdue::Metrics,
    bitmap: Vec<u8>,
}

fn layout(font: &fontdue::Font, text: &str, px: f32) -> (Vec<Raster>, f32, i32, i32) {
    let glyphs: Vec<Raster> = text
        .chars()
        .map(|c| {
            let (metrics, bitmap) = font.rasterize(c, px);
            Raster { metrics, bitmap }
        })
        .collect();
    let width: f32 = glyphs.iter().map(|g| g.metrics.advance_width).sum();
    let top = glyphs
        .iter()
        .map(|g| g.metrics.height as i32 + g.metrics.ymin)
        .max()
        .unwrap_or(0);
    let bottom = glyphs.iter().map(|g| g.metrics.ymin).min().unwrap_or(0);
    (glyphs, width, top, bottom)
}

fn render_with_font(text: &str, size: u32, color: [u8; 3]) -> Option<Image<'static>> {
    let font = font()?;
    let limit = size as f32;
    let mut px = limit * FONT_MAX_FRACTION;
    let mut chosen = None;
    while px >= limit * FONT_MIN_FRACTION {
        let (glyphs, width, top, bottom) = layout(font, text, px);
        let height = (top - bottom) as f32;
        if width <= limit && height <= limit {
            chosen = Some((glyphs, width, top, bottom));
            break;
        }
        px -= FONT_STEP_PX;
    }
    let (glyphs, width, top, bottom) = chosen?;

    let mut rgba = vec![0u8; (size * size * 4) as usize];
    let height = top - bottom;
    let x0 = ((limit - width) / 2.0).round();
    let baseline = ((size as i32 - height) / 2) + top;
    let mut pen = x0;
    for g in &glyphs {
        let gx = pen.round() as i32 + g.metrics.xmin;
        let gy = baseline - g.metrics.height as i32 - g.metrics.ymin;
        for row in 0..g.metrics.height {
            for col in 0..g.metrics.width {
                let alpha = g.bitmap[row * g.metrics.width + col];
                if alpha == 0 {
                    continue;
                }
                let px_x = gx + col as i32;
                let px_y = gy + row as i32;
                if px_x < 0 || px_y < 0 || px_x >= size as i32 || px_y >= size as i32 {
                    continue;
                }
                let i = ((px_y as u32 * size + px_x as u32) * 4) as usize;
                let existing = rgba[i + 3];
                let merged = existing.max(alpha);
                rgba[i] = color[0];
                rgba[i + 1] = color[1];
                rgba[i + 2] = color[2];
                rgba[i + 3] = merged;
            }
        }
        pen += g.metrics.advance_width;
    }
    Some(Image::new_owned(rgba, size, size))
}

fn render_pixel_font(text: &str, size: u32, dark_taskbar: bool) -> Image<'static> {
    let size = size.max(BASE_ICON_SIZE);
    let scale = (size / BASE_ICON_SIZE).max(1) as usize;
    let mut rgba = vec![0u8; (size * size * 4) as usize];
    let color = if dark_taskbar {
        ACCENT_DARK_TASKBAR
    } else {
        ACCENT_LIGHT_TASKBAR
    };

    let width = text_width(text) * scale;
    let height = GLYPH_HEIGHT * scale;
    let mut x = (size as usize).saturating_sub(width) / 2;
    let y0 = (size as usize).saturating_sub(height) / 2;

    for g in text.chars().filter_map(glyph) {
        for (row, bits) in g.rows.iter().enumerate() {
            for (col, ch) in bits.chars().enumerate() {
                if ch != '#' {
                    continue;
                }
                for dy in 0..scale {
                    for dx in 0..scale {
                        let px = x + col * scale + dx;
                        let py = y0 + row * scale + dy;
                        if px < size as usize && py < size as usize {
                            let i = (py * size as usize + px) * 4;
                            rgba[i] = color[0];
                            rgba[i + 1] = color[1];
                            rgba[i + 2] = color[2];
                            rgba[i + 3] = 0xFF;
                        }
                    }
                }
            }
        }
        x += (g.width + GLYPH_GAP) * scale;
    }

    Image::new_owned(rgba, size, size)
}

/// Whether the text fits the 16px pixel-font grid (the font path scales itself to fit).
fn fits(text: &str) -> bool {
    text_width(text) <= BASE_ICON_SIZE as usize
}

/// The panel's small-icon size in physical pixels (16 at 100%, 20 at 125%, 24 at 150%).
#[cfg(target_os = "windows")]
pub fn small_icon_size() -> u32 {
    use windows_sys::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSMICON};
    let px = unsafe { GetSystemMetrics(SM_CXSMICON) };
    if px > 0 {
        px as u32
    } else {
        BASE_ICON_SIZE
    }
}

#[cfg(not(target_os = "windows"))]
pub fn small_icon_size() -> u32 {
    22
}

/// Windows keeps the taskbar theme separate from the app theme; the number must contrast
/// with the taskbar, not the popover.
#[cfg(target_os = "windows")]
pub fn taskbar_is_dark() -> bool {
    // Absolute `reg.exe` out of System32 -- this runs on every badge refresh, so a bare
    // name here would be the single most reliably triggered planted-binary path.
    let output = crate::supervisor::system_command("reg.exe")
        .args([
            "query",
            r"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "/v",
            "SystemUsesLightTheme",
        ])
        .output();
    match output {
        Ok(out) => {
            let text = String::from_utf8_lossy(&out.stdout);
            // "0x0" means the system (taskbar) uses the dark theme.
            text.lines()
                .any(|l| l.contains("SystemUsesLightTheme") && l.trim_end().ends_with("0x0"))
        }
        Err(_) => true,
    }
}

#[cfg(not(target_os = "windows"))]
pub fn taskbar_is_dark() -> bool {
    true
}

// ---------------------------------------------------------------------------------------
// The menu-bar lightsaber
// ---------------------------------------------------------------------------------------

/// A compact diagonal status saber for the macOS menu bar and Windows tray.
/// The handle is bottom-left and the blade top-right. Only the blade colour changes:
/// green for a live collector, red otherwise, as reported by the shared coverage API.
/// Flat fills and two grip marks stay readable at 16px; bloom and a white core would
/// wash out the status colour at that size. Present with template treatment OFF.
pub fn render_lightsaber(live: bool, size: u32) -> Image<'static> {
    let size = size.max(MIN_ICON_SIZE) as usize;
    // One viewBox unit is this many pixels; every length below is in viewBox units, so the
    // icon is identical in proportion at any size and only its resolution changes.
    let scale = size as f32 / SABER_VIEW;
    let blade = if live { BLADE_LIVE } else { BLADE_DOWN };
    let frame = SaberFrame::new(size, scale);
    let mut canvas = Canvas::new(size);

    let hilt = capsule(
        &frame,
        (HILT_FROM_U, AXIS_V),
        (EMITTER_U, AXIS_V),
        HILT_HALF,
        scale,
    );
    paint(&mut canvas, &hilt, &frame.u, |_| HILT_METAL, |_| 1.0);
    for band in GRIP_BANDS_U {
        let tick = capsule(
            &frame,
            (band, AXIS_V - BAND_V),
            (band, AXIS_V + BAND_V),
            BAND_HALF_U,
            scale,
        );
        paint(&mut canvas, &tick, &frame.u, |_| HILT_BAND, |_| 1.0);
    }
    let collar = capsule(
        &frame,
        (EMITTER_U, AXIS_V - COLLAR_V),
        (EMITTER_U, AXIS_V + COLLAR_V),
        COLLAR_HALF,
        scale,
    );
    paint(&mut canvas, &collar, &frame.v, |_| HILT_METAL, |_| 1.0);

    let body = capsule(
        &frame,
        (EMITTER_U, AXIS_V),
        (TIP_U, AXIS_V),
        BLADE_HALF,
        scale,
    );
    paint(&mut canvas, &body, &frame.u, |_| blade, |_| 1.0);
    canvas.into_image()
}

/// Coordinate grid for the diagonal saber, independent of physical icon resolution.
const SABER_VIEW: f32 = 36.0;
/// Below this the strokes collide and the grip bands merge into the hilt.
const MIN_ICON_SIZE: u32 = 16;

/// `u` runs along the saber from pommel to tip and `v` runs across it, in viewBox units.
///
/// Every stroke in the tray asset lies on one 45-degree diagonal through the box, so
/// `(x - y, x + y) * 1/sqrt(2)` is the saber's own axis and the whole silhouette becomes
/// axis-aligned capsules. That is what makes it cheap to draw without an SVG rasteriser,
/// and it gives the tests a frame in which "blade" and "hilt" are numbers.
struct SaberFrame {
    u: Vec<f32>,
    v: Vec<f32>,
}

impl SaberFrame {
    /// One sample per pixel centre, inverse of `(u, v) -> (x, y)` for a `scale`-sized viewBox.
    fn new(size: usize, scale: f32) -> Self {
        let k = std::f32::consts::FRAC_1_SQRT_2;
        let mut frame = SaberFrame {
            u: Vec::with_capacity(size * size),
            v: Vec::with_capacity(size * size),
        };
        for y in 0..size {
            for x in 0..size {
                let px = (x as f32 + 0.5) / scale;
                let py = (y as f32 + 0.5) / scale;
                frame.u.push((px - py) * k);
                frame.v.push((px + py) * k);
            }
        }
        frame
    }
}

/// The diagonal every stroke sits on: `x + y == SABER_VIEW` in the asset, so
/// `v == SABER_VIEW / sqrt(2)`.
const AXIS_V: f32 = SABER_VIEW * std::f32::consts::FRAC_1_SQRT_2;
/// Pommel cap centre, (7.2, 28.8) in the asset.
const HILT_FROM_U: f32 = -15.274;
/// The junction where blade meets hilt, (14.4, 21.6).
const EMITTER_U: f32 = -5.091;
/// Blade tip, (29.4, 6.6).
const TIP_U: f32 = 16.122;

/// Bold strokes keep the blade and handle legible in a small status slot.
const BLADE_HALF: f32 = 2.2;
const HILT_HALF: f32 = 2.4;
const COLLAR_HALF: f32 = 2.2;
const BAND_HALF_U: f32 = 0.625;
/// How far the collar and a grip band reach across the axis.
const COLLAR_V: f32 = 2.264;
const BAND_V: f32 = 1.697;
/// The two grip ticks, centred on the hilt's own axis as they are on the asset's.
const GRIP_BANDS_U: [f32; 2] = [-11.738, -8.909];

/// Saturated flat colours survive both light and dark status bars.
const BLADE_LIVE: [u8; 3] = [0x16, 0xD9, 0x32];
const BLADE_DOWN: [u8; 3] = [0xF0, 0x30, 0x40];
/// Cool silver handle and dark grip marks provide contrast on either appearance.
const HILT_METAL: [u8; 3] = [0xB8, 0xC1, 0xCF];
const HILT_BAND: [u8; 3] = [0x33, 0x41, 0x55];

/// Anti-aliased coverage of the capsule from `from` to `to` with the given radius, one
/// value per pixel. The distance to a segment is exact, so the whole anti-aliasing rule is
/// the half-pixel window on either side of the outline.
fn capsule(
    frame: &SaberFrame,
    from: (f32, f32),
    to: (f32, f32),
    radius: f32,
    scale: f32,
) -> Vec<f32> {
    let (from_u, from_v) = from;
    let (du, dv) = (to.0 - from_u, to.1 - from_v);
    let length_sq = du * du + dv * dv;
    frame
        .u
        .iter()
        .zip(&frame.v)
        .map(|(&u, &v)| {
            let along = if length_sq <= f32::EPSILON {
                0.0
            } else {
                (((u - from_u) * du + (v - from_v) * dv) / length_sq).clamp(0.0, 1.0)
            };
            let (near_u, near_v) = (u - (from_u + du * along), v - (from_v + dv * along));
            let distance = (near_u * near_u + near_v * near_v).sqrt();
            ((radius - distance) * scale + 0.5).clamp(0.0, 1.0)
        })
        .collect()
}

#[cfg(test)]
fn mix_f32(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

/// A `size` x `size` straight-alpha RGBA buffer in 0..=1 channels.
struct Canvas {
    size: usize,
    px: Vec<f32>,
}

impl Canvas {
    fn new(size: usize) -> Self {
        Self {
            size,
            px: vec![0.0; size * size * 4],
        }
    }

    fn over(&mut self, index: usize, source: [f32; 3], alpha: f32) {
        let dst = &mut self.px[index * 4..index * 4 + 4];
        let dst_alpha = dst[3];
        let out_alpha = alpha + dst_alpha * (1.0 - alpha);
        if out_alpha <= 0.0 {
            dst.copy_from_slice(&[0.0; 4]);
            return;
        }
        for channel in 0..3 {
            dst[channel] =
                (source[channel] * alpha + dst[channel] * dst_alpha * (1.0 - alpha)) / out_alpha;
        }
        dst[3] = out_alpha;
    }

    fn into_image(self) -> Image<'static> {
        let mut rgba = Vec::with_capacity(self.px.len());
        for pixel in self.px.chunks_exact(4) {
            let alpha = (pixel[3].clamp(0.0, 1.0) * 255.0).round() as u8;
            if alpha == 0 {
                // A fully transparent pixel carries no colour. Without this a pixel the
                // coverage reached but that rounds away would keep a visible RGB underneath an
                // alpha of zero, which is a latent bug waiting for a compositor that
                // filters rather than blends.
                rgba.extend_from_slice(&[0, 0, 0, 0]);
                continue;
            }
            for channel in &pixel[..3] {
                rgba.push((channel.clamp(0.0, 1.0) * 255.0).round() as u8);
            }
            rgba.push(alpha);
        }
        Image::new_owned(rgba, self.size as u32, self.size as u32)
    }
}

/// Composites `coverage` over the canvas, picking each pixel's colour from `colour(u)` and
/// its opacity from `opacity(u)`. Straight alpha throughout: the system composites the
/// finished icon over the menu bar without baking its background into the edges.
fn paint(
    canvas: &mut Canvas,
    coverage: &[f32],
    frame_u: &[f32],
    colour: impl Fn(f32) -> [u8; 3],
    opacity: impl Fn(f32) -> f32,
) {
    for (index, &cover) in coverage.iter().enumerate() {
        let alpha = cover * opacity(frame_u[index]);
        if alpha <= 0.0 {
            continue;
        }
        let rgb = colour(frame_u[index]);
        canvas.over(
            index,
            [
                rgb[0] as f32 / 255.0,
                rgb[1] as f32 / 255.0,
                rgb[2] as f32 / 255.0,
            ],
            alpha,
        );
    }
}

/// Reusable solid green/red disc. The desktop status item uses the saber instead.
pub fn render_status_dot(live: bool, size: u32) -> Image<'static> {
    /// Accessible green/red that survives both light and dark menu bars.
    const LIVE: [u8; 3] = [0x22, 0xC5, 0x5E];
    const DOWN: [u8; 3] = [0xEF, 0x44, 0x44];
    let color = if live { LIVE } else { DOWN };
    let size = size.max(MIN_ICON_SIZE);
    let mut rgba = vec![0u8; (size * size * 4) as usize];
    let centre = size as f32 / 2.0;
    // A filled disc with a small transparent margin, so it reads as a glyph
    // rather than a full-bleed square at 16px.
    let radius = centre - 2.0;
    for y in 0..size {
        for x in 0..size {
            let dx = x as f32 + 0.5 - centre;
            let dy = y as f32 + 0.5 - centre;
            if dx * dx + dy * dy <= radius * radius {
                let i = ((y * size + x) * 4) as usize;
                rgba[i] = color[0];
                rgba[i + 1] = color[1];
                rgba[i + 2] = color[2];
                rgba[i + 3] = 0xFF;
            }
        }
    }
    Image::new_owned(rgba, size, size)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Compare the handle behind the emitter, clear of the blade's rounded cap.
    const HILT_CLEAR_OF_THE_EMITTER: f32 = EMITTER_U - 3.0;
    /// Below this alpha a pixel is invisible and whatever colour is stored under it cannot
    /// be observed, so it is compared by coverage alone.
    const INKED_ALPHA: u8 = 8;

    /// Pixel index for a point in the saber's own frame, the inverse of [`SaberFrame::new`].
    fn at(frame_u: f32, frame_v: f32, size: u32) -> usize {
        let k = std::f32::consts::FRAC_1_SQRT_2;
        let scale = size as f32 / SABER_VIEW;
        let x = ((frame_u + frame_v) * k * scale - 0.5)
            .round()
            .clamp(0.0, (size - 1) as f32) as usize;
        let y = ((frame_v - frame_u) * k * scale - 0.5)
            .round()
            .clamp(0.0, (size - 1) as f32) as usize;
        y * size as usize + x
    }

    fn pixel(image: &Image, index: usize) -> [u8; 4] {
        let rgba = image.rgba();
        [
            rgba[index * 4],
            rgba[index * 4 + 1],
            rgba[index * 4 + 2],
            rgba[index * 4 + 3],
        ]
    }

    fn alpha_at(image: &Image, index: usize) -> u8 {
        pixel(image, index)[3]
    }

    /// A point `t` of the way along the blade, emitter to tip.
    fn on_blade(t: f32) -> f32 {
        mix_f32(EMITTER_U, TIP_U, t)
    }

    /// Asserts an inked pixel's hue: green-dominant or red-dominant, never a blend.
    fn assert_hue(px: [u8; 4], live: bool, what: &str) {
        let [r, g, b, a] = px;
        assert!(a > 0, "{what} must be inked: {px:?}");
        if live {
            assert!(g > r && g > b, "a live {what} must read green: {px:?}");
        } else {
            assert!(r > g && r > b, "a down {what} must read red: {px:?}");
        }
    }

    #[test]
    fn the_blade_carries_the_collector_colour() {
        for live in [true, false] {
            let image = render_lightsaber(live, 88);
            // Check saturated blade pixels: colour carries the collector status.
            let mut best = (0u8, [0u8; 4]);
            for step in 0..=60 {
                let index = at(
                    on_blade(step as f32 / 60.0) + 0.3,
                    AXIS_V + BLADE_HALF * 0.5,
                    88,
                );
                let px = pixel(&image, index);
                if px[3] < 200 {
                    continue;
                }
                let sat = px[0].max(px[1]).max(px[2]) - px[0].min(px[1]).min(px[2]);
                if sat > best.0 {
                    best = (sat, px);
                }
            }
            let (sat, blade) = best;
            assert!(
                sat > 96,
                "the blade must be saturated, not tinted: {blade:?}"
            );
            assert_hue(blade, live, "blade");
        }
    }

    #[test]
    fn the_hilt_stays_the_marks_metal_in_both_states() {
        for live in [true, false] {
            let image = render_lightsaber(live, 88);
            let hilt = pixel(&image, at(mix_f32(HILT_FROM_U, EMITTER_U, 0.5), AXIS_V, 88));
            assert_eq!(hilt[3], 0xFF, "the hilt is opaque metal: {hilt:?}");
            let [r, g, b, _] = hilt;
            // Slate runs cool: blue over red and blue over green. Either of the other two
            // would mean the blade's colour had bled into the hilt.
            assert!(
                b > r,
                "the hilt must not take the red blade's cast: {hilt:?}"
            );
            assert!(
                b > g,
                "the hilt must not take the green blade's cast: {hilt:?}"
            );
        }
    }

    #[test]
    fn both_states_are_the_same_saber_and_only_the_blade_colour_changes() {
        let size = 88u32;
        let live = render_lightsaber(true, size);
        let down = render_lightsaber(false, size);
        let frame = SaberFrame::new(size as usize, size as f32 / SABER_VIEW);

        let mut hilt_compared = 0usize;
        // The pommel sits inside the compared region, so the comparison below is about the
        // hilt and not about an empty corner of the icon.
        assert!(
            alpha_at(&live, at(HILT_FROM_U, AXIS_V, size)) >= INKED_ALPHA,
            "the pommel must be inked, or this test is comparing nothing"
        );
        for (index, &frame_u) in frame.u.iter().enumerate() {
            // The silhouette is a property of the mark, not of the collector state: a red
            // saber and a green one must have byte-identical coverage, or the icon would be
            // changing shape as well as colour.
            assert_eq!(
                alpha_at(&live, index),
                alpha_at(&down, index),
                "the silhouette must not depend on collector state (pixel {index})"
            );
            if frame_u > HILT_CLEAR_OF_THE_EMITTER || alpha_at(&live, index) < INKED_ALPHA {
                continue;
            }
            assert_eq!(
                pixel(&live, index),
                pixel(&down, index),
                "everything behind the emitter is the mark's own metal and must not depend \
                 on collector state (pixel {index})"
            );
            hilt_compared += 1;
        }
        assert!(
            hilt_compared > 150,
            "this must have compared a run of real hilt, not a handful of pixels: \
             {hilt_compared}"
        );
    }

    #[test]
    fn the_icon_is_a_diagonal_saber_rather_than_a_disc() {
        let size = 88u32;
        let image = render_lightsaber(true, size);
        let frame = SaberFrame::new(size as usize, size as f32 / SABER_VIEW);

        let mut along = (f32::MAX, f32::MIN);
        let mut across = (f32::MAX, f32::MIN);
        for (index, (frame_u, frame_v)) in frame.u.iter().zip(&frame.v).enumerate() {
            if alpha_at(&image, index) < 128 {
                continue;
            }
            along.0 = along.0.min(*frame_u);
            along.1 = along.1.max(*frame_u);
            across.0 = across.0.min(*frame_v);
            across.1 = across.1.max(*frame_v);
        }
        let length = along.1 - along.0;
        let width = across.1 - across.0;
        // A disc inscribed in this box is square in the saber's own frame; the saber is a
        // long bar along the diagonal. This is the assertion that the glyph is a saber and
        // not a coloured circle.
        assert!(
            length > width * 2.0,
            "the silhouette must be a bar along the blade, not a disc: {length} x {width}"
        );
        assert!(
            (28.0..=40.0).contains(&length),
            "the saber must span the asset's diagonal: {length}"
        );
        assert!(
            (7.0..=12.0).contains(&width),
            "the saber must stay the width of its hilt and emitter collar: {width}"
        );
        // Both ends of the bar are inked: the pommel and the tip.
        assert!(
            alpha_at(&image, at(HILT_FROM_U, AXIS_V, size)) == 0xFF,
            "the hilt must end in a solid pommel"
        );
        assert!(
            alpha_at(&image, at(TIP_U, AXIS_V, size)) == 0xFF,
            "the blade must end in a solid tip"
        );
    }

    #[test]
    fn the_flat_blade_has_no_bloom_outside_its_edge() {
        for live in [true, false] {
            let image = render_lightsaber(live, 88);
            assert_hue(pixel(&image, at(on_blade(0.6), AXIS_V, 88)), live, "blade");
            assert_eq!(alpha_at(&image, at(on_blade(0.6), AXIS_V + 3.7, 88)), 0);
        }
    }

    #[test]
    fn it_reads_as_a_glyph_at_the_real_menu_bar_size() {
        for size in [16, 20, 22, 24, 32] {
            for live in [true, false] {
                let image = render_lightsaber(live, size);
                let pixels = size as usize * size as usize;
                let inked = (0..pixels)
                    .filter(|&index| alpha_at(&image, index) > 32)
                    .count();
                let share = inked as f32 / pixels as f32;
                // A glyph, not a dot (a filled disc would be ~0.4) and not a full-bleed square.
                assert!(
                    (0.04..0.30).contains(&share),
                    "the icon must read as a glyph at {size}px, not a blob or a sheet: {share}"
                );
                // The saber is one continuous object: nothing along the blade is unpainted.
                for step in 0..=40 {
                    let frame_u = mix_f32(HILT_FROM_U, TIP_U, step as f32 / 40.0);
                    assert!(
                        alpha_at(&image, at(frame_u, AXIS_V, size)) > 0,
                        "the saber must be continuous from pommel to tip, but it is blank at \
                     u={frame_u}"
                    );
                }
            }
        }
    }

    #[test]
    fn the_saber_carries_colour_a_template_tint_would_destroy() {
        // macOS tints a template image to match the menu bar, which is what flattened the
        // previous monochrome asset. If the icon were greyscale, presenting it with
        // template treatment on would cost nothing -- so prove it is not greyscale, because
        // that is why `apply_status_item` presents it as a non-template image.
        for live in [true, false] {
            let image = render_lightsaber(live, small_icon_size());
            let mut most_saturated = 0u8;
            for index in 0..(image.width() * image.height()) as usize {
                let [r, g, b, a] = pixel(&image, index);
                if a < 200 {
                    continue;
                }
                most_saturated = most_saturated.max(r.max(g).max(b) - r.min(g).min(b));
            }
            assert!(
                most_saturated > 96,
                "the icon must carry real colour worth protecting from template tinting: \
                 most saturated channel spread was {most_saturated}"
            );
        }
    }

    /// Writes the menu-bar previews: the real slot, a nearest-neighbour blow-up of that
    /// exact raster (what macOS actually scales), a clean large render, and the icon
    /// composited over both menu-bar appearances.
    ///
    ///     cd dash/tray/src-tauri
    ///     KYBER_SABER_PREVIEW_DIR=/tmp/kyber-lightsaber-preview \
    ///       cargo test --lib tray_badge::tests::write_menu_bar_previews -- --ignored
    ///     for bmp in "$KYBER_SABER_PREVIEW_DIR"/*.bmp; do
    ///       sips -s format png "$bmp" --out "${bmp%.bmp}.png" >/dev/null
    ///     done
    ///
    /// BMP rather than PNG because `tauri::image::Image` decodes but does not encode, and a
    /// hand-rolled PNG writer would be more code in this file than the glyph is worth.
    /// `sips` ships with macOS and does the conversion.
    #[test]
    #[ignore = "writes preview dumps to $KYBER_SABER_PREVIEW_DIR; run it by hand"]
    fn write_menu_bar_previews() {
        let dir = std::env::var("KYBER_SABER_PREVIEW_DIR")
            .unwrap_or_else(|_| "/tmp/kyber-lightsaber-preview".to_owned());
        std::fs::create_dir_all(&dir).expect("create the preview directory");

        let write = |name: &str, image: &Image| {
            write_bmp(&std::path::Path::new(&dir).join(name), image);
        };
        for (state, live) in [("live", true), ("down", false)] {
            let actual = render_lightsaber(live, small_icon_size());
            write(&format!("saber-{state}-menubar.bmp"), &actual);
            write(
                &format!("saber-{state}-menubar-4x.bmp"),
                &upscale(&actual, 4),
            );
            write(
                &format!("saber-{state}-large.bmp"),
                &render_lightsaber(live, 176),
            );
            for (appearance, background) in
                [("light", [0xEF, 0xEF, 0xEF]), ("dark", [0x2B, 0x2B, 0x2E])]
            {
                write(
                    &format!("saber-{state}-on-{appearance}.bmp"),
                    &over_background(&actual, background, 6),
                );
            }
        }
    }

    fn upscale(image: &Image, factor: u32) -> Image<'static> {
        let size = image.width() * factor;
        let source = image.rgba();
        let mut rgba = Vec::with_capacity((size * size * 4) as usize);
        for y in 0..size {
            for x in 0..size {
                let at = ((y / factor) * image.width() + x / factor) as usize * 4;
                rgba.extend_from_slice(&source[at..at + 4]);
            }
        }
        Image::new_owned(rgba, size, size)
    }

    /// Composites the icon over a menu-bar appearance colour, padded so the icon's edges
    /// are visible against it.
    fn over_background(image: &Image, background: [u8; 3], pad: u32) -> Image<'static> {
        let size = image.width() + pad * 2;
        let mut rgba = vec![0u8; (size * size * 4) as usize];
        for chunk in rgba.chunks_exact_mut(4) {
            chunk.copy_from_slice(&[background[0], background[1], background[2], 0xFF]);
        }
        for y in 0..image.height() {
            for x in 0..image.width() {
                let source = &image.rgba()[((y * image.width() + x) as usize * 4)..][..4];
                let alpha = source[3] as u32;
                if alpha == 0 {
                    continue;
                }
                let out = ((y + pad) * size + x + pad) as usize * 4;
                for channel in 0..3 {
                    let blended = (source[channel] as u32 * alpha
                        + background[channel] as u32 * (255 - alpha))
                        / 255;
                    rgba[out + channel] = blended as u8;
                }
                rgba[out + 3] = 0xFF;
            }
        }
        Image::new_owned(rgba, size, size)
    }

    fn write_bmp(path: &std::path::Path, image: &Image) {
        let (width, height) = (image.width(), image.height());
        let row = (width as usize * 3).div_ceil(4) * 4;
        let mut pixels = vec![0u8; row * height as usize];
        // A positive BMP height means bottom-up, so the image's last row is written first.
        // Getting this backwards previews an upside-down saber, which is exactly the thing
        // the preview exists to rule out.
        for (file_row, y) in (0..height as usize).rev().enumerate() {
            for x in 0..width as usize {
                let [r, g, b, _] = pixel(image, y * width as usize + x);
                let out = file_row * row + x * 3;
                pixels[out..out + 3].copy_from_slice(&[b, g, r]);
            }
        }
        let header = bmp_header(width, height, (row * height as usize) as u32);
        std::fs::write(path, [header, pixels].concat()).expect("write the preview bitmap");
        eprintln!("wrote {}", path.display());
    }

    /// A 24-bit bottom-up BITMAPINFOHEADER. Windows BMP is the one bitmap format with no
    /// compression and no dependency, which is all a preview needs.
    fn bmp_header(width: u32, height: u32, pixel_bytes: u32) -> Vec<u8> {
        let mut header = vec![0u8; 14 + 40];
        header[0..2].copy_from_slice(b"BM");
        header[2..6].copy_from_slice(&(54u32 + pixel_bytes).to_le_bytes());
        header[10..14].copy_from_slice(&54u32.to_le_bytes());
        header[14..18].copy_from_slice(&40u32.to_le_bytes());
        header[18..22].copy_from_slice(&width.to_le_bytes());
        header[22..26].copy_from_slice(&height.to_le_bytes());
        header[26..28].copy_from_slice(&1u16.to_le_bytes());
        header[28..30].copy_from_slice(&24u16.to_le_bytes());
        header[34..38].copy_from_slice(&pixel_bytes.to_le_bytes());
        header
    }

    #[test]
    fn the_status_dot_is_opaque_and_coloured() {
        for live in [true, false] {
            let image = render_status_dot(live, 22);
            assert_eq!(image.width(), 22);
            assert_eq!(image.height(), 22);
            let rgba = image.rgba();
            // The centre pixel is the disc: opaque and the expected colour.
            let cx = 11u32;
            let cy = 11u32;
            let i = ((cy * 22 + cx) * 4) as usize;
            let expected = if live {
                [0x22u8, 0xC5, 0x5E]
            } else {
                [0xEFu8, 0x44, 0x44]
            };
            assert_eq!([rgba[i], rgba[i + 1], rgba[i + 2]], expected);
            assert_eq!(rgba[i + 3], 0xFF);
            // A corner pixel is outside the disc: transparent.
            assert_eq!(rgba[3], 0x00);
        }
    }
}
