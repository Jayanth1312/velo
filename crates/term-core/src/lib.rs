//! term-core: wraps alacritty_terminal — owns the grid + parser and resolves a
//! flat, render-ready [`Frame`] of colored cells. No PTY dependency: the write
//! sink (responses the terminal emits, e.g. cursor reports) is injected as a closure.

use std::sync::Arc;

use parking_lot::Mutex;

use alacritty_terminal::event::{Event, EventListener};
use alacritty_terminal::grid::{Dimensions, Scroll};
use alacritty_terminal::index::{Column, Line, Point, Side};
use alacritty_terminal::selection::{Selection, SelectionRange, SelectionType};
use alacritty_terminal::term::cell::Flags;
use alacritty_terminal::term::{Config, Term, TermDamage, TermMode};
use alacritty_terminal::vte::ansi::{Color, CursorShape as AnsiCursorShape, Processor};

pub mod keys;
pub mod palette;

/// Grid dimensions passed to alacritty's `Term`. `TermSize` lives in a test-only
/// submodule upstream, so we supply our own `Dimensions` impl. Scrollback
/// history is separately sized via `Config::scrolling_history`; alacritty's
/// grid tracks total_lines (screen + history) internally.
struct WinSize {
    columns: usize,
    screen_lines: usize,
}

impl Dimensions for WinSize {
    fn total_lines(&self) -> usize {
        self.screen_lines
    }
    fn screen_lines(&self) -> usize {
        self.screen_lines
    }
    fn columns(&self) -> usize {
        self.columns
    }
}

/// Sink for bytes the terminal emits back to the PTY (e.g. cursor reports).
pub type WriteSink = Arc<dyn Fn(&[u8]) + Send + Sync>;

/// Forwards `Event::PtyWrite` (terminal-generated responses) to the PTY writer
/// and captures OSC window-title changes into a shared cell.
#[derive(Clone)]
pub struct EventProxy {
    on_write: WriteSink,
    title: Arc<Mutex<Option<String>>>,
}

impl EventListener for EventProxy {
    fn send_event(&self, event: Event) {
        match event {
            Event::PtyWrite(text) => (self.on_write)(text.as_bytes()),
            Event::Title(t) => *self.title.lock() = Some(t),
            Event::ResetTitle => *self.title.lock() = None,
            _ => {}
        }
    }
}

/// Cursor shape, mapped from the terminal's reported style.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CursorShape {
    Block,
    Underline,
    Bar,
    Hidden,
}

fn map_cursor(shape: AnsiCursorShape) -> CursorShape {
    match shape {
        AnsiCursorShape::Block | AnsiCursorShape::HollowBlock => CursorShape::Block,
        AnsiCursorShape::Underline => CursorShape::Underline,
        AnsiCursorShape::Beam => CursorShape::Bar,
        AnsiCursorShape::Hidden => CursorShape::Hidden,
    }
}

/// Shell-integration signal decoded from OSC 7 (cwd) / OSC 133 (command marks).
/// alacritty doesn't surface these, so we tee the byte stream (see [`OscTee`]).
pub enum ShellEvent {
    /// New working directory (OSC 7).
    Cwd(String),
    /// OSC 133 command mark. `phase`: 0 = prompt start (A), 1 = command start
    /// with `text` (C), 2 = command end (D) with `exit` + `dur_ms`.
    Command {
        phase: u8,
        exit: i32,
        dur_ms: u64,
        text: String,
    },
}

/// Read-only scanner that lifts OSC payloads out of the PTY byte stream without
/// consuming them (the full stream still goes to alacritty's parser). Carries
/// state across `feed` calls so an OSC split over two PTY reads still completes.
#[derive(Default)]
struct OscTee {
    in_osc: bool, // between `ESC ]` and the terminator
    esc: bool,    // saw `ESC` outside an OSC (waiting for `]`)
    st: bool,     // saw `ESC` inside an OSC (waiting for `\` = ST)
    buf: Vec<u8>, // current OSC payload (no intro / terminator)
}

impl OscTee {
    /// Feed raw bytes; push each completed OSC payload into `out`.
    fn feed(&mut self, bytes: &[u8], out: &mut Vec<Vec<u8>>) {
        for &b in bytes {
            if !self.in_osc {
                if self.esc {
                    self.esc = false;
                    if b == b']' {
                        self.in_osc = true;
                        self.buf.clear();
                    }
                } else if b == 0x1b {
                    self.esc = true;
                }
            } else if self.st {
                self.st = false;
                self.in_osc = false;
                if b == b'\\' {
                    out.push(std::mem::take(&mut self.buf)); // ST terminator
                }
                self.buf.clear(); // ESC + other: abort
            } else if b == 0x07 {
                self.in_osc = false;
                out.push(std::mem::take(&mut self.buf)); // BEL terminator
            } else if b == 0x1b {
                self.st = true;
            } else if self.buf.len() < 8192 {
                self.buf.push(b);
            }
        }
    }
}

fn hex(c: u8) -> Option<u8> {
    match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        b'A'..=b'F' => Some(c - b'A' + 10),
        _ => None,
    }
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            if let (Some(h), Some(l)) = (hex(b[i + 1]), hex(b[i + 2])) {
                out.push(h * 16 + l);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// OSC 7 payload (`file://HOST/PATH` or a bare path) → decoded filesystem path.
/// Drops the `file://host` prefix and normalizes a Windows `/C:/..` to `C:/..`.
fn parse_osc7(rest: &[u8]) -> Option<String> {
    let s = std::str::from_utf8(rest).ok()?;
    let path = match s.strip_prefix("file://") {
        // Split host from path on the first separator (`/`, or `\` if a shell
        // emitted a raw Windows path). No separator => treat the whole thing as path.
        Some(after) => match after.find(['/', '\\']) {
            Some(i) => &after[i..],
            None => after,
        },
        None => s,
    };
    let p = percent_decode(path).replace('\\', "/");
    let bytes = p.as_bytes();
    if bytes.len() >= 3 && bytes[0] == b'/' && bytes[2] == b':' {
        Some(p[1..].to_string())
    } else {
        Some(p)
    }
}

/// Snapshot of the app-controlled mouse-reporting DECSET bits. `reporting` is
/// true when any report mode (click/drag/motion) is on; `motion`/`drag` say
/// which move events to forward; `sgr` says whether the app asked for SGR
/// (1006) extended coordinates (col/row > 223) as opposed to legacy X10.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MouseMode {
    pub reporting: bool,
    pub motion: bool,
    pub drag: bool,
    pub sgr: bool,
}

pub struct Terminal {
    term: Term<EventProxy>,
    parser: Processor,
    /// Last selection range observed; a change forces a full redraw since
    /// alacritty's damage tracking deliberately excludes selection.
    last_selection: Option<SelectionRange>,
    /// Last display offset observed; a change (scrollback) forces a full
    /// redraw since viewport row->grid line mapping shifts entirely.
    last_display_offset: usize,
    /// Latest OSC window title (shared with the `EventProxy`; `None` = default).
    title: Arc<Mutex<Option<String>>>,
    /// Shell-integration scanner + decoded-event queue (drained by the host).
    osc: OscTee,
    pending_shell: Vec<ShellEvent>,
    cmd_start: Option<std::time::Instant>,
}

impl Terminal {
    pub fn new(cols: u16, rows: u16, on_write: WriteSink) -> Self {
        let size = WinSize {
            columns: cols.max(1) as usize,
            screen_lines: rows.max(1) as usize,
        };
        let title = Arc::new(Mutex::new(None));
        let config = Config {
            scrolling_history: 10_000,
            ..Config::default()
        };
        let term = Term::new(
            config,
            &size,
            EventProxy {
                on_write,
                title: title.clone(),
            },
        );
        Self {
            term,
            parser: Processor::new(),
            last_selection: None,
            last_display_offset: 0,
            title,
            osc: OscTee::default(),
            pending_shell: Vec::new(),
            cmd_start: None,
        }
    }

    /// Latest OSC window title (None = shell reset to default).
    pub fn title(&self) -> Option<String> {
        self.title.lock().clone()
    }

    /// Drain decoded shell-integration events (OSC 7 / 133) since the last call.
    pub fn take_shell_events(&mut self) -> Vec<ShellEvent> {
        std::mem::take(&mut self.pending_shell)
    }

    /// Feed raw PTY output bytes through the ANSI parser into the grid.
    pub fn advance(&mut self, bytes: &[u8]) {
        self.parser.advance(&mut self.term, bytes);
        let mut oscs = Vec::new();
        self.osc.feed(bytes, &mut oscs);
        for osc in oscs {
            self.handle_osc(&osc);
        }
    }

    /// Decode one OSC payload into a shell event (OSC 7 cwd / OSC 133 marks).
    fn handle_osc(&mut self, osc: &[u8]) {
        if let Some(rest) = osc.strip_prefix(b"7;") {
            if let Some(cwd) = parse_osc7(rest) {
                self.pending_shell.push(ShellEvent::Cwd(cwd));
            }
        } else if let Some(rest) = osc.strip_prefix(b"133;") {
            match rest.first().copied() {
                Some(b'A') => self.pending_shell.push(ShellEvent::Command {
                    phase: 0,
                    exit: 0,
                    dur_ms: 0,
                    text: String::new(),
                }),
                Some(b'C') => {
                    self.cmd_start = Some(std::time::Instant::now());
                    let text = rest
                        .strip_prefix(b"C;")
                        .and_then(|t| std::str::from_utf8(t).ok())
                        .unwrap_or("")
                        .to_string();
                    self.pending_shell.push(ShellEvent::Command {
                        phase: 1,
                        exit: 0,
                        dur_ms: 0,
                        text,
                    });
                }
                Some(b'D') => {
                    let exit = rest
                        .strip_prefix(b"D;")
                        .and_then(|t| std::str::from_utf8(t).ok())
                        .and_then(|t| t.trim().parse::<i32>().ok())
                        .unwrap_or(0);
                    let dur_ms = self
                        .cmd_start
                        .take()
                        .map(|s| s.elapsed().as_millis() as u64)
                        .unwrap_or(0);
                    self.pending_shell.push(ShellEvent::Command {
                        phase: 2,
                        exit,
                        dur_ms,
                        text: String::new(),
                    });
                }
                _ => {}
            }
        }
    }

    pub fn resize(&mut self, cols: u16, rows: u16) {
        self.term.resize(WinSize {
            columns: cols.max(1) as usize,
            screen_lines: rows.max(1) as usize,
        });
    }

    /// Begin a simple (character-granularity) selection at a viewport cell.
    pub fn start_selection(&mut self, col: u16, row: u16, left_half: bool) {
        let point = self.viewport_point(col, row);
        let side = if left_half { Side::Left } else { Side::Right };
        self.term.selection = Some(Selection::new(SelectionType::Simple, point, side));
    }

    /// Extend the active selection to a viewport cell.
    pub fn update_selection(&mut self, col: u16, row: u16, left_half: bool) {
        let point = self.viewport_point(col, row);
        let side = if left_half { Side::Left } else { Side::Right };
        if let Some(sel) = self.term.selection.as_mut() {
            sel.update(point, side);
        }
    }

    pub fn clear_selection(&mut self) {
        self.term.selection = None;
    }

    /// Scroll the viewport into scrollback history by `delta_lines` (positive
    /// = toward history / up, negative = toward the present / down).
    pub fn scroll_display(&mut self, delta_lines: i32) {
        self.term.scroll_display(Scroll::Delta(delta_lines));
    }

    /// How many lines the viewport is currently scrolled back into history
    /// (0 = viewport shows the live bottom of the screen).
    pub fn display_offset(&self) -> usize {
        self.term.grid().display_offset()
    }

    /// Snap the viewport back to the live bottom of the screen. Alacritty
    /// doesn't do this automatically for bytes written straight to the PTY
    /// (only for its own input path), so callers should invoke this before
    /// forwarding keystrokes/paste while scrolled back.
    pub fn scroll_to_bottom(&mut self) {
        self.term.scroll_display(Scroll::Bottom);
    }

    /// Whether the alternate screen (e.g. vim, less) is active. Callers use
    /// this to decide whether mouse wheel input should scroll history or be
    /// translated into arrow-key sequences.
    pub fn alt_screen_active(&self) -> bool {
        self.term.mode().contains(TermMode::ALT_SCREEN)
    }

    /// Whether the app has enabled application cursor-key mode (DECCKM).
    /// When set, arrow/Home/End keys (with no modifiers) send `ESC O <letter>`
    /// instead of `CSI <letter>`.
    pub fn app_cursor(&self) -> bool {
        self.term.mode().contains(TermMode::APP_CURSOR)
    }

    /// Selected text, if any (for clipboard copy).
    pub fn selection_text(&self) -> Option<String> {
        self.term.selection_to_string()
    }

    /// Whether the app has enabled bracketed paste (DECSET 2004). When set, the
    /// host must wrap pasted text in `ESC[200~` / `ESC[201~`.
    pub fn bracketed_paste(&self) -> bool {
        self.term.mode().contains(TermMode::BRACKETED_PASTE)
    }

    /// Which mouse-reporting modes the app has enabled via DECSET, so the host
    /// knows whether raw pointer events should be encoded to the PTY (SGR 1006)
    /// instead of driving local text selection.
    pub fn mouse_mode(&self) -> MouseMode {
        let mode = self.term.mode();
        MouseMode {
            reporting: mode.intersects(
                TermMode::MOUSE_REPORT_CLICK | TermMode::MOUSE_DRAG | TermMode::MOUSE_MOTION,
            ),
            motion: mode.contains(TermMode::MOUSE_MOTION),
            drag: mode.contains(TermMode::MOUSE_DRAG),
            sgr: mode.contains(TermMode::SGR_MOUSE),
        }
    }

    /// Map a viewport (col, row) to a grid point. When scrolled back into
    /// history (`display_offset > 0`), viewport row 0 is grid line
    /// `-display_offset`, so subtract the offset to land on the right line.
    fn viewport_point(&self, col: u16, row: u16) -> Point {
        let cols = self.term.columns();
        let rows = self.term.screen_lines();
        let offset = self.display_offset() as i32;
        let row = (row as usize).min(rows.saturating_sub(1)) as i32;
        Point::new(
            Line(row - offset),
            Column((col as usize).min(cols.saturating_sub(1))),
        )
    }

    /// Resolve the visible grid into a flat list of render cells (only cells that
    /// draw something) plus cursor state and damage since the last frame.
    pub fn frame(&mut self) -> Frame {
        let cols = self.term.columns() as u16;
        let rows = self.term.screen_lines() as u16;

        // Collect content/cursor damage first (mutable borrow), then reset it.
        let mut base_damage = match self.term.damage() {
            TermDamage::Full => FrameDamage::Full,
            TermDamage::Partial(it) => FrameDamage::Rows(
                it.filter(|l| l.line < rows as usize)
                    .map(|l| l.line as u16)
                    .collect(),
            ),
        };
        self.term.reset_damage();

        let content = self.term.renderable_content();
        let selection = content.selection;
        let display_offset = content.display_offset;

        // Selection isn't part of damage; a change repaints everything.
        if selection != self.last_selection {
            base_damage = FrameDamage::Full;
        }
        // Scrolling shifts every viewport row -> grid line mapping; alacritty's
        // own damage tracking doesn't account for that, so force a full repaint.
        if display_offset != self.last_display_offset {
            base_damage = FrameDamage::Full;
        }

        let mut cells = Vec::new();
        for indexed in content.display_iter {
            // Grid lines are relative to the live screen bottom (0 = bottom-most
            // live row, negative = scrollback); shift by the display offset to
            // get the viewport-relative row alacritty's iterator already scoped
            // its range to.
            let line = indexed.point.line.0 + display_offset as i32;
            if line < 0 || line >= rows as i32 {
                continue;
            }
            let row = line as u16;
            let col = indexed.point.column.0 as u16;
            let cell = indexed.cell;
            let flags = cell.flags;

            // Skip the trailing spacer of a wide glyph; the wide cell overflows
            // into it visually.
            if flags.contains(Flags::WIDE_CHAR_SPACER) {
                continue;
            }

            let bold = flags.intersects(Flags::BOLD | Flags::BOLD_ITALIC);
            let italic = flags.intersects(Flags::ITALIC | Flags::BOLD_ITALIC);
            let underline = flags.intersects(Flags::UNDERLINE | Flags::DOUBLE_UNDERLINE);

            // Bold brightens named foreground colors.
            let mut fg_color = cell.fg;
            if bold {
                if let Color::Named(n) = fg_color {
                    fg_color = Color::Named(n.to_bright());
                }
            }
            let mut fg = palette::resolve(fg_color);
            let mut bg = palette::resolve(cell.bg);
            if flags.contains(Flags::INVERSE) {
                std::mem::swap(&mut fg, &mut bg);
            }

            let selected = selection.is_some_and(|r| r.contains(indexed.point));
            let hidden = flags.contains(Flags::HIDDEN);
            let glyph = if cell.c == ' ' || hidden { '\0' } else { cell.c };

            // Keep a cell only if it draws something: a glyph, a non-default
            // background, or a selection highlight.
            if glyph == '\0' && bg == palette::DEFAULT_BG && !selected {
                continue;
            }
            cells.push(RenderCell {
                col,
                row,
                c: glyph,
                fg,
                bg,
                bold,
                italic,
                underline,
                selected,
            });
        }

        let cursor = content.cursor;
        // The cursor's grid line is always within the live screen (never
        // negative); while scrolled back into history it'd land outside the
        // viewport, so hide it rather than draw it at a bogus row.
        let cursor_shape = if display_offset > 0 {
            CursorShape::Hidden
        } else {
            map_cursor(cursor.shape)
        };
        self.last_selection = selection;
        self.last_display_offset = display_offset;
        Frame {
            cols,
            rows,
            cells,
            cursor_col: cursor.point.column.0 as u16,
            cursor_row: cursor.point.line.0.max(0) as u16,
            cursor_shape,
            damage: base_damage,
        }
    }
}

/// What changed since the previous [`Frame`], for incremental GPU upload.
pub enum FrameDamage {
    /// Repaint every row.
    Full,
    /// Only these viewport rows changed.
    Rows(Vec<u16>),
}

/// A render-ready snapshot of the visible terminal.
pub struct Frame {
    pub cols: u16,
    pub rows: u16,
    /// Only cells that draw something (glyph, non-default bg, or selection).
    pub cells: Vec<RenderCell>,
    pub cursor_col: u16,
    pub cursor_row: u16,
    pub cursor_shape: CursorShape,
    pub damage: FrameDamage,
}

#[derive(Clone, Copy)]
pub struct RenderCell {
    pub col: u16,
    pub row: u16,
    /// `'\0'` means a background-only cell (no glyph).
    pub c: char,
    pub fg: [u8; 3],
    pub bg: [u8; 3],
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    pub selected: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    use alacritty_terminal::vte::ansi::{NamedColor, Rgb};

    fn term() -> Terminal {
        Terminal::new(4, 2, Arc::new(|_: &[u8]| {}))
    }

    #[test]
    fn resolve_named_red() {
        assert_eq!(
            palette::resolve(Color::Named(NamedColor::Red)),
            palette::ANSI_256[1]
        );
    }

    #[test]
    fn resolve_named_foreground_is_default_fg() {
        assert_eq!(
            palette::resolve(Color::Named(NamedColor::Foreground)),
            palette::DEFAULT_FG
        );
    }

    #[test]
    fn resolve_indexed_cube_corner_is_white() {
        // 231 is the last 6x6x6 cube entry: full r/g/b.
        assert_eq!(palette::resolve(Color::Indexed(231)), [0xff, 0xff, 0xff]);
    }

    #[test]
    fn resolve_spec_passthrough() {
        assert_eq!(
            palette::resolve(Color::Spec(Rgb { r: 1, g: 2, b: 3 })),
            [1, 2, 3]
        );
    }

    #[test]
    fn truecolor_fg_passthrough() {
        let mut t = term();
        // SGR 38;2;r;g;b sets a 24-bit fg.
        t.advance(b"\x1b[38;2;10;20;30mX");
        let f = t.frame();
        let cell = f.cells.iter().find(|c| c.c == 'X').expect("X present");
        assert_eq!(cell.fg, [10, 20, 30]);
    }

    #[test]
    fn inverse_swaps_fg_and_bg() {
        let mut t = term();
        // SGR 31 = red fg; SGR 7 = inverse; then write 'X'.
        t.advance(b"\x1b[31m\x1b[7mX");
        let frame = t.frame();
        let cell = frame.cells.iter().find(|c| c.c == 'X').expect("X cell");
        assert_eq!(cell.bg, palette::ANSI_256[1]);
        assert_eq!(cell.fg, palette::DEFAULT_BG);
    }

    #[test]
    fn bold_italic_underline_flags() {
        let mut t = term();
        t.advance(b"\x1b[1;3;4mZ");
        let f = t.frame();
        let cell = f.cells.iter().find(|c| c.c == 'Z').expect("Z present");
        assert!(cell.bold && cell.italic && cell.underline);
    }

    #[test]
    fn cursor_shape_bar_via_decscusr() {
        let mut t = term();
        // DECSCUSR 6 = steady bar.
        t.advance(b"\x1b[6 q");
        assert_eq!(t.frame().cursor_shape, CursorShape::Bar);
        // DECSCUSR 4 = steady underline.
        t.advance(b"\x1b[4 q");
        assert_eq!(t.frame().cursor_shape, CursorShape::Underline);
    }

    #[test]
    fn selection_marks_cells_and_forces_full_damage() {
        let mut t = term();
        t.advance(b"AB");
        let _ = t.frame(); // consume initial full damage
        t.start_selection(0, 0, true);
        t.update_selection(1, 0, false);
        let f = t.frame();
        assert!(matches!(f.damage, FrameDamage::Full), "selection forces full");
        let a = f.cells.iter().find(|c| c.c == 'A').expect("A present");
        assert!(a.selected, "A should be selected");
        assert_eq!(t.selection_text().as_deref(), Some("AB"));
    }

    #[test]
    fn osc7_cwd_and_osc133_command_marks() {
        let mut t = term();
        t.advance(b"\x1b]7;file://host/C:/Users/me%20x\x07");
        t.advance(b"\x1b]133;C;ls -la\x07");
        t.advance(b"\x1b]133;D;3\x07");
        let ev = t.take_shell_events();
        assert_eq!(ev.len(), 3);
        assert!(matches!(&ev[0], ShellEvent::Cwd(p) if p == "C:/Users/me x"));
        assert!(
            matches!(&ev[1], ShellEvent::Command { phase: 1, text, .. } if text == "ls -la")
        );
        assert!(matches!(&ev[2], ShellEvent::Command { phase: 2, exit: 3, .. }));
        // Drained.
        assert!(t.take_shell_events().is_empty());
    }

    #[test]
    fn osc7_powershell_style_backslash_path() {
        // Real PowerShell emitter: file://HOST + /C:\Users\me (backslashes).
        let mut t = term();
        t.advance(b"\x1b]7;file://DESKTOP/C:\\Users\\me\x07");
        let ev = t.take_shell_events();
        assert!(matches!(&ev[0], ShellEvent::Cwd(p) if p == "C:/Users/me"));
    }

    #[test]
    fn osc_split_across_advance_calls() {
        let mut t = term();
        // OSC 7 cut mid-path between two PTY reads.
        t.advance(b"\x1b]7;file://h/C:/Pro");
        t.advance(b"jects\x07");
        let ev = t.take_shell_events();
        assert!(matches!(&ev[0], ShellEvent::Cwd(p) if p == "C:/Projects"));
    }

    #[test]
    fn scroll_display_moves_offset_into_history() {
        let mut t = term();
        // 2 rows visible; push well past that so there's scrollback.
        for i in 0..20 {
            t.advance(format!("line{i}\r\n").as_bytes());
        }
        assert_eq!(t.display_offset(), 0);
        t.scroll_display(5);
        assert!(t.display_offset() > 0, "scrolling up should move into history");
        t.scroll_display(-1000);
        assert_eq!(t.display_offset(), 0, "large negative delta snaps back to bottom");
    }

    #[test]
    fn frame_reflects_scrolled_content() {
        let mut t = term();
        for i in 0..20 {
            t.advance(format!("L{i}\r\n").as_bytes());
        }
        let _ = t.frame(); // consume damage from feeding
        t.scroll_display(20); // scroll to the very top
        let f = t.frame();
        // Top-of-scrollback content ("L0") should now be visible somewhere.
        let has_l0 = f.cells.iter().any(|c| c.c == 'L') ;
        assert!(has_l0, "scrolled-back content should render");
    }

    #[test]
    fn scrolling_forces_full_damage() {
        let mut t = term();
        for i in 0..20 {
            t.advance(format!("line{i}\r\n").as_bytes());
        }
        let _ = t.frame(); // consume initial full damage
        // Idle frame (no scroll) is incremental.
        match t.frame().damage {
            FrameDamage::Full => panic!("unrelated frame should not be full"),
            FrameDamage::Rows(_) => {}
        }
        t.scroll_display(3);
        let f = t.frame();
        assert!(matches!(f.damage, FrameDamage::Full), "scrolling forces full damage");
    }

    #[test]
    fn scroll_to_bottom_resets_offset() {
        let mut t = term();
        for i in 0..20 {
            t.advance(format!("line{i}\r\n").as_bytes());
        }
        t.scroll_display(10);
        assert!(t.display_offset() > 0);
        t.scroll_to_bottom();
        assert_eq!(t.display_offset(), 0);
    }

    #[test]
    fn idle_frame_is_partial_not_full() {
        let mut t = term();
        t.advance(b"hi");
        let _ = t.frame(); // first frame is fully damaged
        // No new input -> next frame is incremental (alacritty always re-damages
        // the cursor row for blink, so expect at most one dirty row, never Full).
        match t.frame().damage {
            FrameDamage::Rows(rows) => {
                assert!(rows.len() <= 1, "idle => only the cursor row, got {rows:?}");
            }
            FrameDamage::Full => panic!("idle frame must not be fully damaged"),
        }
    }
}
