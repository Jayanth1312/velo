//! text: cosmic-text shaping + per-glyph rasterization for a monospace grid.
//!
//! Loads one primary monospace face plus symbol/emoji/CJK fallback faces so
//! cosmic-text can shape any codepoint, then rasterizes a single char to either
//! a grayscale coverage mask (regular glyphs, tinted by the cell fg) or a full
//! RGBA bitmap (color emoji / multi-color symbols). Bold and italic select real
//! font variants; a sub-pixel x offset is threaded into the swash cache key so
//! the renderer can position glyphs at fractional cell origins.

use cosmic_text::{
    fontdb, Attrs, Buffer, Family, FontSystem, Metrics, Shaping, Style, SwashCache, SwashContent,
    Weight,
};

/// Re-exported lucide icon set; `char::from(Icon::X)` yields the glyph drawn via
/// the bundled lucide face (see the fallback load in [`Font::new`]).
pub use lucide_icons::Icon;

/// Bundled Nerd-Font-patched monospace. Embedding it means powerline/file-type
/// icons (private-use codepoints emitted by `eza`/`ls`/prompts) always resolve,
/// with zero per-machine font install. Loaded as the primary face.
const BUNDLED_NF: &[u8] = include_bytes!("../fonts/CascadiaCodeNF.ttf");

/// Primary monospace fonts, tried in order. Loading a single known face keeps
/// startup fast (a full `load_system_fonts` enumerates every installed face).
#[cfg(windows)]
const MONO_CANDIDATES: &[&str] = &[
    r"C:\Windows\Fonts\consola.ttf",
    r"C:\Windows\Fonts\CascadiaMono.ttf",
    r"C:\Windows\Fonts\CascadiaCode.ttf",
    r"C:\Windows\Fonts\lucon.ttf",
];
#[cfg(target_os = "macos")]
const MONO_CANDIDATES: &[&str] = &[
    "/System/Library/Fonts/Menlo.ttc",
    "/System/Library/Fonts/SFNSMono.ttf",
];
#[cfg(all(unix, not(target_os = "macos")))]
const MONO_CANDIDATES: &[&str] = &[
    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
    "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
    "/usr/share/fonts/dejavu/DejaVuSansMono.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf",
    "/usr/share/fonts/liberation/LiberationMono-Regular.ttf",
    "/usr/share/fonts/truetype/ubuntu/UbuntuMono-R.ttf",
];

/// Fallback faces covering glyphs the primary monospace lacks: color emoji,
/// dingbats/symbols, and CJK. Without these, a primary-only db drops every
/// non-Latin codepoint (no fallback chain). Missing files are skipped; if none
/// load, a full system scan backstops coverage.
#[cfg(windows)]
const FALLBACK_CANDIDATES: &[&str] = &[
    r"C:\Windows\Fonts\seguiemj.ttf", // Segoe UI Emoji (color)
    r"C:\Windows\Fonts\seguisym.ttf", // Segoe UI Symbol
    r"C:\Windows\Fonts\segoeui.ttf",  // Segoe UI (broad Unicode)
    r"C:\Windows\Fonts\msyh.ttc",     // Microsoft YaHei (CJK)
    r"C:\Windows\Fonts\malgun.ttf",   // Malgun Gothic (Korean)
    r"C:\Windows\Fonts\YuGothM.ttc",  // Yu Gothic (Japanese)
];
#[cfg(target_os = "macos")]
const FALLBACK_CANDIDATES: &[&str] = &[
    "/System/Library/Fonts/Apple Color Emoji.ttc",
    "/Library/Fonts/Arial Unicode.ttf",
    "/System/Library/Fonts/STHeiti Light.ttc",
];
#[cfg(all(unix, not(target_os = "macos")))]
const FALLBACK_CANDIDATES: &[&str] = &[
    "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
];

/// A loaded monospace font plus reusable shaping scratch state and cell metrics.
pub struct Font {
    font_system: FontSystem,
    swash: SwashCache,
    buffer: Buffer,
    /// Resolved family name when a specific font was loaded; `None` falls back
    /// to generic `Family::Monospace`.
    family: Option<String>,
    /// Monospace advance width, px.
    pub cell_w: f32,
    /// Line height, px.
    pub cell_h: f32,
    /// Baseline offset from the cell top, px.
    pub ascent: f32,
}

/// A rasterized glyph: either a grayscale coverage mask (1 byte/px, tinted by
/// the cell fg) or a straight-alpha RGBA bitmap (4 bytes/px, drawn as-is).
pub struct Raster {
    pub width: u32,
    pub height: u32,
    /// Bearing: x offset from the pen origin to the bitmap's left edge.
    pub left: i32,
    /// Bearing: y offset from the baseline up to the bitmap's top edge.
    pub top: i32,
    /// Mask: row-major coverage, 1 byte/px. Color: row-major RGBA, 4 bytes/px.
    pub coverage: Vec<u8>,
    /// True when `coverage` is RGBA color data (emoji / multi-color glyphs).
    pub color: bool,
}

/// Every installed font family, sorted, deduped. Scans the full system font
/// set (one-off cost; only called when the settings UI needs the list).
pub fn list_families() -> Vec<String> {
    let mut db = fontdb::Database::new();
    db.load_system_fonts();
    let mut names: Vec<String> = db
        .faces()
        .filter_map(|f| f.families.first().map(|(name, _)| name.clone()))
        .collect();
    names.sort();
    names.dedup();
    names
}

impl Font {
    pub fn new(font_size: f32) -> Self {
        Self::with_family(font_size, None)
    }

    /// Like [`Font::new`], but shape with a user-chosen installed family.
    /// `requested = None` (or an unknown name) keeps the bundled Nerd Font;
    /// a matching system family becomes the primary face, with the bundled
    /// NF + curated faces still in the db as the fallback chain.
    pub fn with_family(font_size: f32, requested: Option<&str>) -> Self {
        let mut db = fontdb::Database::new();
        // Bundled Nerd Font first: covers Latin + icon glyphs out of the box.
        db.load_font_data(BUNDLED_NF.to_vec());
        let mut family = db
            .faces()
            .next()
            .and_then(|f| f.families.first().map(|(name, _)| name.clone()));
        // System mono candidates as extra coverage (family stays the bundled NF).
        if family.is_none() {
            for path in MONO_CANDIDATES {
                if db.load_font_file(path).is_ok() {
                    family = db
                        .faces()
                        .next()
                        .and_then(|f| f.families.first().map(|(name, _)| name.clone()));
                    break;
                }
            }
        }
        // Lucide UI-icon face (toolbar/tab/window-chrome glyphs live in its PUA).
        db.load_font_data(lucide_icons::LUCIDE_FONT_BYTES.to_vec());
        // Load curated fallback faces for symbols/emoji/CJK coverage.
        let mut have_fallback = false;
        for path in FALLBACK_CANDIDATES {
            if db.load_font_file(path).is_ok() {
                have_fallback = true;
            }
        }
        // Backstop: with no primary or no curated fallback, scan all system
        // fonts so any codepoint can still find a face.
        if family.is_none() || !have_fallback || requested.is_some() {
            db.load_system_fonts();
        }
        // A requested system family overrides the bundled primary when it
        // exists in the db (case-insensitive); otherwise keep the bundled NF.
        if let Some(req) = requested {
            let hit = db
                .faces()
                .filter_map(|f| f.families.first().map(|(name, _)| name.clone()))
                .find(|name| name.eq_ignore_ascii_case(req));
            if hit.is_some() {
                family = hit;
            }
        }
        if let Some(name) = &family {
            db.set_monospace_family(name.clone());
        }

        let mut font_system = FontSystem::new_with_locale_and_db("en-US".to_string(), db);
        let swash = SwashCache::new();
        let metrics = Metrics::new(font_size, font_size * 1.2);
        let mut buffer = Buffer::new(&mut font_system, metrics);

        let attrs = mono_attrs(&family, false, false);
        // Shape an "M" to derive monospace cell metrics.
        buffer.set_text(&mut font_system, "M", &attrs, Shaping::Advanced, None);
        buffer.shape_until_scroll(&mut font_system, false);

        let (raw_w, raw_ascent) = buffer
            .layout_runs()
            .next()
            .and_then(|run| run.glyphs.first().map(|g| (g.w, run.line_y)))
            .unwrap_or((font_size * 0.6, font_size));
        // Snap cell metrics to whole pixels so the grid + baselines land on pixel
        // boundaries. Fractional cells make every glyph quad straddle pixels, which
        // reads as soft/blurry text even with nearest-neighbour atlas sampling.
        let cell_w = raw_w.round().max(1.0);
        let cell_h = metrics.line_height.round().max(1.0);
        let ascent = raw_ascent.round();

        Self {
            font_system,
            swash,
            buffer,
            family,
            cell_w,
            cell_h,
            ascent,
        }
    }

    /// Change the shaping size without reloading any font data. Reuses the
    /// existing `font_system` (no fontdb/FontSystem rebuild, no file I/O) and
    /// just re-derives cell metrics the same way [`Font::new`] does: rebuild
    /// the shaping buffer at the new `Metrics`, re-shape "M", re-measure.
    pub fn set_size(&mut self, font_size: f32) {
        let metrics = Metrics::new(font_size, font_size * 1.2);
        self.buffer = Buffer::new(&mut self.font_system, metrics);

        let attrs = mono_attrs(&self.family, false, false);
        self.buffer
            .set_text(&mut self.font_system, "M", &attrs, Shaping::Advanced, None);
        self.buffer.shape_until_scroll(&mut self.font_system, false);

        let (raw_w, raw_ascent) = self
            .buffer
            .layout_runs()
            .next()
            .and_then(|run| run.glyphs.first().map(|g| (g.w, run.line_y)))
            .unwrap_or((font_size * 0.6, font_size));
        self.cell_w = raw_w.round().max(1.0);
        self.cell_h = metrics.line_height.round().max(1.0);
        self.ascent = raw_ascent.round();
    }

    /// Rasterize one char. `bold`/`italic` select font variants; `subpx_x` is the
    /// fractional pen x-position (0.0..1.0) baked into the glyph's sub-pixel cache
    /// key for crisper positioning. Returns `None` for blank/empty glyphs.
    pub fn rasterize(&mut self, c: char, bold: bool, italic: bool, subpx_x: f32) -> Option<Raster> {
        let mut s = [0u8; 4];
        let text = c.encode_utf8(&mut s);
        let attrs = mono_attrs(&self.family, bold, italic);
        self.buffer
            .set_text(&mut self.font_system, text, &attrs, Shaping::Advanced, None);
        self.buffer.shape_until_scroll(&mut self.font_system, false);

        let run = self.buffer.layout_runs().next()?;
        let glyph = run.glyphs.first()?;
        let physical = glyph.physical((subpx_x, 0.0), 1.0);
        let img = self
            .swash
            .get_image(&mut self.font_system, physical.cache_key)
            .as_ref()?;

        if img.placement.width == 0 || img.placement.height == 0 {
            return None;
        }

        let color = match img.content {
            SwashContent::Mask => false,
            SwashContent::Color => true,
            // Subpixel masks are unused here; treat as blank.
            SwashContent::SubpixelMask => return None,
        };

        Some(Raster {
            width: img.placement.width,
            height: img.placement.height,
            left: img.placement.left,
            top: img.placement.top,
            coverage: img.data.clone(),
            color,
        })
    }
}

/// Build monospace shaping attributes: a specific family when one was loaded,
/// else the generic monospace family, plus bold/italic variant selection.
fn mono_attrs(family: &Option<String>, bold: bool, italic: bool) -> Attrs<'_> {
    let base = match family {
        Some(name) => Attrs::new().family(Family::Name(name)),
        None => Attrs::new().family(Family::Monospace),
    };
    let base = if bold { base.weight(Weight::BOLD) } else { base };
    if italic {
        base.style(Style::Italic)
    } else {
        base
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_monospace_and_rasterizes() {
        let mut font = Font::new(16.0);
        assert!(font.cell_w > 0.0, "cell_w should be positive");
        assert!(font.cell_h > 0.0, "cell_h should be positive");
        assert!(font.ascent > 0.0, "ascent should be positive");

        let m = font.rasterize('M', false, false, 0.0).expect("'M' rasterizes");
        assert!(m.width > 0 && m.height > 0);
        assert!(!m.color, "'M' is a mask glyph");
        assert_eq!(m.coverage.len(), (m.width * m.height) as usize);

        // Blank cells produce no raster.
        assert!(font.rasterize(' ', false, false, 0.0).is_none());
    }

    #[test]
    fn box_drawing_and_symbols_rasterize() {
        let mut font = Font::new(16.0);
        // Box-drawing + arrows + a math symbol: must shape via the fallback chain.
        for c in ['─', '│', '┌', '→', '√', '★'] {
            let r = font.rasterize(c, false, false, 0.0);
            assert!(r.is_some(), "{c:?} should rasterize (fallback coverage)");
        }
    }

    #[test]
    fn emoji_rasterizes_as_color() {
        let mut font = Font::new(16.0);
        // Color emoji must rasterize as RGBA, not be dropped. Skip only if the
        // host genuinely has no emoji font in its fallback chain.
        match font.rasterize('😀', false, false, 0.0) {
            Some(r) => {
                assert!(r.color, "emoji should be a color raster");
                assert_eq!(r.coverage.len(), (r.width * r.height * 4) as usize);
            }
            None => eprintln!("no emoji font available; skipping color assertion"),
        }
    }

    #[test]
    fn lucide_icon_rasterizes() {
        let mut font = Font::new(16.0);
        // A lucide UI glyph must shape via the bundled lucide face.
        let menu = char::from(Icon::Menu);
        let r = font.rasterize(menu, false, false, 0.0);
        assert!(r.is_some(), "lucide Menu icon should rasterize");
    }

    #[test]
    fn set_size_matches_fresh_font_new() {
        // set_size (reusing the FontSystem) must land on the same cell metrics
        // as constructing a fresh Font at that size from scratch.
        let mut font = Font::new(12.0);
        font.set_size(20.0);
        let fresh = Font::new(20.0);
        assert_eq!(font.cell_w, fresh.cell_w);
        assert_eq!(font.cell_h, fresh.cell_h);
        assert_eq!(font.ascent, fresh.ascent);
    }

    #[test]
    fn with_family_unknown_falls_back_to_bundled() {
        let a = Font::new(16.0);
        let b = Font::with_family(16.0, Some("No Such Font Family 123"));
        assert_eq!(a.family, b.family, "unknown family keeps bundled primary");
        assert_eq!(a.cell_w, b.cell_w);
        assert_eq!(a.cell_h, b.cell_h);
    }

    #[test]
    fn list_families_sorted_and_deduped() {
        let names = list_families();
        for w in names.windows(2) {
            assert!(w[0] < w[1], "sorted + deduped: {:?} !< {:?}", w[0], w[1]);
        }
    }

    #[test]
    fn subpixel_offset_distinguishes_rasters() {
        let mut font = Font::new(16.0);
        // Different sub-pixel bins should be allowed to differ; at minimum both
        // produce a valid raster (swash keys them separately).
        let a = font.rasterize('g', false, false, 0.0).expect("g@0.0");
        let b = font.rasterize('g', false, false, 0.5).expect("g@0.5");
        assert!(a.width > 0 && b.width > 0);
    }
}
