//! Renders today's spend into the tray icon. Windows and most Linux panels cannot place a
//! title next to a tray icon the way the macOS menubar does, so the number becomes the icon:
//! a 4x7 pixel font drawn at the panel's native small-icon size, so it stays crisp instead
//! of being a scaled-down bitmap.

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
    let color = if dark_taskbar { ACCENT_DARK_TASKBAR } else { ACCENT_LIGHT_TASKBAR };
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
    let top = glyphs.iter().map(|g| g.metrics.height as i32 + g.metrics.ymin).max().unwrap_or(0);
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
    let color = if dark_taskbar { ACCENT_DARK_TASKBAR } else { ACCENT_LIGHT_TASKBAR };

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
    if px > 0 { px as u32 } else { BASE_ICON_SIZE }
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
    let output = crate::cli::system_command("reg.exe")
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
            text.lines().any(|l| l.contains("SystemUsesLightTheme") && l.trim_end().ends_with("0x0"))
        }
        Err(_) => true,
    }
}

#[cfg(not(target_os = "windows"))]
pub fn taskbar_is_dark() -> bool {
    true
}
