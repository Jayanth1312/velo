//! Color resolution: ANSI/indexed/spec -> RGB, with terminal defaults.
//!
//! The first 16 ANSI colors plus foreground / cursor / selection are themeable
//! at runtime (see [`set_theme`]); the background stays the [`DEFAULT_BG`]
//! sentinel so the renderer's clear color (the chrome tint, via `velo_set_bg`)
//! shows through default-bg cells for blur/transparency.

use alacritty_terminal::vte::ansi::{Color, NamedColor};
use std::sync::RwLock;

/// Default foreground (light gray).
pub const DEFAULT_FG: [u8; 3] = [0xd0, 0xd0, 0xd0];
/// Default background — solid black. Also the sentinel for "default bg" cells:
/// never themed here (the renderer clear color owns the real background), so the
/// transparency skip-check stays valid.
pub const DEFAULT_BG: [u8; 3] = [0x00, 0x00, 0x00];
/// Selection highlight background (muted blue-gray).
pub const SELECTION_BG: [u8; 3] = [0x3a, 0x4a, 0x66];

/// Standard 16 system colors (the themeable slice of the 256-color palette).
pub const BASE16: [[u8; 3]; 16] = [
    [0x00, 0x00, 0x00],
    [0x80, 0x00, 0x00],
    [0x00, 0x80, 0x00],
    [0x80, 0x80, 0x00],
    [0x00, 0x00, 0x80],
    [0x80, 0x00, 0x80],
    [0x00, 0x80, 0x80],
    [0xc0, 0xc0, 0xc0],
    [0x80, 0x80, 0x80],
    [0xff, 0x00, 0x00],
    [0x00, 0xff, 0x00],
    [0xff, 0xff, 0x00],
    [0x00, 0x00, 0xff],
    [0xff, 0x00, 0xff],
    [0x00, 0xff, 0xff],
    [0xff, 0xff, 0xff],
];

/// Runtime-overridable theme for the 16 system colors + fg/cursor/selection.
struct Theme {
    ansi: [[u8; 3]; 16],
    fg: [u8; 3],
    cursor: [u8; 3],
    selection: [u8; 3],
}

static THEME: RwLock<Theme> = RwLock::new(Theme {
    ansi: BASE16,
    fg: DEFAULT_FG,
    cursor: DEFAULT_FG,
    selection: SELECTION_BG,
});

/// Replace the active theme. Cube (16..232) and grayscale (232..256) entries are
/// not themeable and keep their standard xterm values.
pub fn set_theme(ansi: [[u8; 3]; 16], fg: [u8; 3], cursor: [u8; 3], selection: [u8; 3]) {
    *THEME.write().unwrap() = Theme { ansi, fg, cursor, selection };
}

/// Themed default foreground.
pub fn fg() -> [u8; 3] {
    THEME.read().unwrap().fg
}

/// Themed cursor color.
pub fn cursor() -> [u8; 3] {
    THEME.read().unwrap().cursor
}

/// Themed selection-highlight background.
pub fn selection() -> [u8; 3] {
    THEME.read().unwrap().selection
}

/// Indexed palette color with the runtime theme applied to 0..16 (the fixed
/// cube/grayscale above). The editor keys its syntax colors off this so
/// terminal themes restyle code automatically.
pub fn ansi(i: usize) -> [u8; 3] {
    if i < 16 {
        THEME.read().unwrap().ansi[i]
    } else {
        ANSI_256[i.min(255)]
    }
}

/// Standard xterm 256-color palette: 16 system + 6x6x6 cube + 24 grays. Used for
/// indexed colors >= 16; indices 0..16 go through the runtime theme in [`resolve`].
pub static ANSI_256: [[u8; 3]; 256] = build_palette();

const fn build_palette() -> [[u8; 3]; 256] {
    let mut p = [[0u8; 3]; 256];

    // 0..16: standard system colors.
    let mut i = 0;
    while i < 16 {
        p[i] = BASE16[i];
        i += 1;
    }

    // 16..232: 6x6x6 color cube.
    let mut n = 0;
    while n < 216 {
        let r = n / 36;
        let g = (n / 6) % 6;
        let b = n % 6;
        p[16 + n] = [cube(r), cube(g), cube(b)];
        n += 1;
    }

    // 232..256: 24-step grayscale ramp.
    let mut k = 0;
    while k < 24 {
        let v = (8 + 10 * k) as u8;
        p[232 + k] = [v, v, v];
        k += 1;
    }

    p
}

const fn cube(c: usize) -> u8 {
    if c == 0 {
        0
    } else {
        (55 + 40 * c) as u8
    }
}

pub fn resolve(c: Color) -> [u8; 3] {
    match c {
        Color::Spec(rgb) => [rgb.r, rgb.g, rgb.b],
        // 0..16 are themeable; >= 16 come from the fixed cube/grayscale palette.
        Color::Indexed(i) if i < 16 => THEME.read().unwrap().ansi[i as usize],
        Color::Indexed(i) => ANSI_256[i as usize],
        Color::Named(n) => match n {
            NamedColor::Foreground | NamedColor::BrightForeground | NamedColor::DimForeground => fg(),
            NamedColor::Cursor => cursor(),
            NamedColor::Background => DEFAULT_BG,
            // Black..BrightWhite map to 0..15; Dim* (>15) handled above, so this
            // never indexes past the base colors.
            other => THEME.read().unwrap().ansi[(other as usize).min(15)],
        },
    }
}
