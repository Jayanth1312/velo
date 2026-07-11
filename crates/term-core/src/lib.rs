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
mod links;
pub mod palette;

pub use links::{Link, link_in_row};

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
    /// History length at the last frame, to derive content motion.
    last_history: usize,
    /// Whether the last frame was on the alt screen (suppresses the motion
    /// signal across screen switches, where history_size jumps to/from 0).
    last_alt: bool,
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
            last_history: 0,
            last_alt: false,
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
        // Reflow can change history length; re-baseline so the next frame's
        // scrolled_up doesn't read the jump as content motion.
        self.last_history = self.term.grid().history_size();
        self.last_display_offset = self.term.grid().display_offset();
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
        // Sampled before `renderable_content()` takes its borrow; consumed by
        // the scrolled_up computation at the end of this fn.
        let history = self.term.grid().history_size();
        let alt = self.term.mode().contains(TermMode::ALT_SCREEN);

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
            // into it visually. NB: the "gap after a pasted emoji" and the
            // 3-backspace erase dance in PowerShell are PSReadLine/ConPTY redraw
            // behavior over this 2-column layout (Windows Terminal shows the
            // same); the grid here just reflects what ConPTY sends.
            if flags.contains(Flags::WIDE_CHAR_SPACER) {
                continue;
            }

            let bold = flags.intersects(Flags::BOLD | Flags::BOLD_ITALIC);
            let italic = flags.intersects(Flags::ITALIC | Flags::BOLD_ITALIC);
            let underline = if flags.contains(Flags::UNDERCURL) {
                UnderlineKind::Curly
            } else if flags.contains(Flags::DOTTED_UNDERLINE) {
                UnderlineKind::Dotted
            } else if flags.contains(Flags::DASHED_UNDERLINE) {
                UnderlineKind::Dashed
            } else if flags.contains(Flags::DOUBLE_UNDERLINE) {
                UnderlineKind::Double
            } else if flags.contains(Flags::UNDERLINE) {
                UnderlineKind::Single
            } else {
                UnderlineKind::None
            };
            let strike = flags.contains(Flags::STRIKEOUT);

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
            if flags.intersects(Flags::DIM | Flags::DIM_BOLD) {
                fg = fg.map(|c| (c as u16 * 2 / 3) as u8);
            }
            let underline_color = cell.underline_color().map(palette::resolve).unwrap_or(fg);

            let selected = selection.is_some_and(|r| r.contains(indexed.point));
            let hidden = flags.contains(Flags::HIDDEN);
            let glyph = if cell.c == ' ' || hidden { '\0' } else { cell.c };

            // Keep a cell only if it draws something: a glyph, a non-default
            // background, a selection highlight, or a decoration (underline
            // or strikeout) on an otherwise-blank cell.
            if glyph == '\0'
                && bg == palette::DEFAULT_BG
                && !selected
                && underline == UnderlineKind::None
                && !strike
            {
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
                underline_color,
                strike,
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
        // Content motion = change of (history above viewport top). New lines
        // grow history (content up); scrolling back grows the offset (down);
        // lines arriving while scrolled back grow both and cancel out.
        let scrolled_up = if alt || self.last_alt {
            0
        } else {
            ((history as i64 - display_offset as i64)
                - (self.last_history as i64 - self.last_display_offset as i64))
                .clamp(i32::MIN as i64, i32::MAX as i64) as i32
        };
        self.last_selection = selection;
        self.last_display_offset = display_offset;
        self.last_history = history;
        self.last_alt = alt;
        Frame {
            cols,
            rows,
            cells,
            cursor_col: cursor.point.column.0 as u16,
            cursor_row: cursor.point.line.0.max(0) as u16,
            cursor_shape,
            damage: base_damage,
            scrolled_up,
        }
    }

    /// Link under (col, row) in the visible viewport: an OSC 8 hyperlink on the
    /// cell if the app set one, else a heuristic scan of the row's text.
    pub fn link_at(&self, col: u16, row: u16) -> Option<Link> {
        let cols = self.term.columns() as u16;
        let rows = self.term.screen_lines() as u16;
        if col >= cols || row >= rows {
            return None;
        }

        let content = self.term.renderable_content();
        let display_offset = content.display_offset;

        // Rebuild the row's text (char-per-column, same iteration as `frame()`)
        // alongside each column's hyperlink, so a click can resolve either an
        // OSC 8 link or fall back to a heuristic scan of the plain text.
        let mut text = String::with_capacity(cols as usize);
        let mut hyperlinks: Vec<Option<alacritty_terminal::term::cell::Hyperlink>> =
            Vec::with_capacity(cols as usize);
        // `display_iter` is line-ordered, so skip rows before the target and
        // stop as soon as we pass it instead of scanning the whole viewport.
        for indexed in content.display_iter {
            let line = indexed.point.line.0 + display_offset as i32;
            if line < row as i32 {
                continue;
            }
            if line > row as i32 {
                break;
            }
            let cell = indexed.cell;
            if cell.flags.contains(Flags::WIDE_CHAR_SPACER) {
                text.push(' ');
                hyperlinks.push(None);
                continue;
            }
            text.push(cell.c);
            hyperlinks.push(cell.hyperlink());
        }

        if let Some(hl) = hyperlinks.get(col as usize).and_then(|h| h.clone()) {
            let idx = col as usize;
            let mut start = idx;
            while start > 0 && hyperlinks[start - 1].as_ref() == Some(&hl) {
                start -= 1;
            }
            let mut end = idx;
            while end + 1 < hyperlinks.len() && hyperlinks[end + 1].as_ref() == Some(&hl) {
                end += 1;
            }
            return Some(Link {
                col_start: start as u16,
                col_end: end as u16,
                target: hl.uri().to_string(),
            });
        }

        link_in_row(&text, col)
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
    /// Rows the viewport content moved *up* since the last frame (new output
    /// scrolls content up; scrolling back into history moves it down =
    /// negative). 0 on the alt screen and across alt-screen transitions.
    /// Drives the renderer's smooth-scroll offset.
    pub scrolled_up: i32,
}

/// Underline style, as distinguished by SGR 4 (with the `:n` subparameter)
/// and SGR 21.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub enum UnderlineKind {
    #[default]
    None,
    Single,
    Double,
    Curly,
    Dotted,
    Dashed,
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
    pub underline: UnderlineKind,
    /// SGR 58 underline color; defaults to `fg` when unset.
    pub underline_color: [u8; 3],
    pub strike: bool,
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
    fn scrolled_up_counts_lines_pushed_to_history() {
        let mut t = term(); // 4 cols x 2 rows
        let _ = t.frame(); // settle initial state
        // 4 newlines on a 2-row screen -> lines pushed into history.
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let f = t.frame();
        assert!(f.scrolled_up > 0, "expected positive shift, got {}", f.scrolled_up);
        // Second frame with no new data: no motion.
        assert_eq!(t.frame().scrolled_up, 0);
    }

    #[test]
    fn wide_char_emits_single_leading_cell() {
        let mut t = term(); // 4 cols x 2 rows
        t.advance("😀".to_string().as_bytes());
        let f = t.frame();
        let emoji: Vec<_> = f.cells.iter().filter(|c| c.c == '😀').collect();
        assert_eq!(emoji.len(), 1, "emoji must render exactly once");
        assert_eq!(emoji[0].col, 0);
        // The spacer cell (col 1) must not emit a glyph.
        assert!(!f.cells.iter().any(|c| c.col == 1 && c.c != '\0'));
    }

    #[test]
    fn emoji_backspace_erase_sequence_clears_cleanly() {
        // What a shell sends to erase a 2-col emoji: BS BS, two spaces, BS BS.
        let mut t = term();
        t.advance("😀".as_bytes());
        t.advance(b"\x08\x08  \x08\x08");
        let f = t.frame();
        assert!(
            !f.cells.iter().any(|c| c.c != '\0'),
            "grid must be empty after erase, found {:?}",
            f.cells.iter().filter(|c| c.c != '\0').map(|c| c.c).collect::<Vec<_>>()
        );
    }

    #[test]
    fn replacement_char_renders_as_itself() {
        // ConPTY hands us U+FFFD mid-edit on a half-erased surrogate pair.
        // We render the grid faithfully (hiding data would be worse); this
        // test pins that the char passes through rather than crashing/blanking.
        let mut t = term();
        t.advance("\u{FFFD}".to_string().as_bytes());
        let f = t.frame();
        assert!(f.cells.iter().any(|c| c.c == '\u{FFFD}'));
    }

    #[test]
    fn scrolled_up_negative_on_scrollback() {
        let mut t = term();
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let _ = t.frame();
        t.scroll_display(2); // toward history -> content moves down
        assert_eq!(t.frame().scrolled_up, -2);
        t.scroll_to_bottom(); // back to present -> content moves up
        assert_eq!(t.frame().scrolled_up, 2);
    }

    #[test]
    fn scrolled_up_zero_on_alt_screen() {
        let mut t = term();
        let _ = t.frame();
        t.advance(b"\x1b[?1049h"); // enter alt screen
        t.advance(b"x\r\ny\r\nz\r\nw");
        assert_eq!(t.frame().scrolled_up, 0);
        t.advance(b"\x1b[?1049l"); // leave alt screen: transition frame also 0
        assert_eq!(t.frame().scrolled_up, 0);
    }

    #[test]
    fn scrolled_up_zero_after_resize() {
        let mut t = term();
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let _ = t.frame();
        t.resize(6, 3); // reflow may change history size; must not read as motion
        assert_eq!(t.frame().scrolled_up, 0);
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
        assert!(cell.bold && cell.italic && cell.underline == UnderlineKind::Single);
    }

    #[test]
    fn underline_kinds_from_sgr() {
        let cases: [(&[u8], UnderlineKind); 5] = [
            (b"\x1b[4mU", UnderlineKind::Single),
            // Double underline is SGR 4:2 in this vte version (0.15.0);
            // SGR 21 maps to Attr::CancelBold here, not DoubleUnderline.
            (b"\x1b[4:2mU", UnderlineKind::Double),
            (b"\x1b[4:3mU", UnderlineKind::Curly),
            (b"\x1b[4:4mU", UnderlineKind::Dotted),
            (b"\x1b[4:5mU", UnderlineKind::Dashed),
        ];
        for (seq, kind) in cases {
            let mut t = term();
            t.advance(seq);
            let f = t.frame();
            let c = f.cells.iter().find(|c| c.c == 'U').unwrap();
            assert_eq!(c.underline, kind, "seq {:?}", std::str::from_utf8(seq));
        }
    }

    #[test]
    fn underline_color_sgr58() {
        let mut t = term();
        t.advance(b"\x1b[4m\x1b[58;2;10;20;30mU");
        let f = t.frame();
        let c = f.cells.iter().find(|c| c.c == 'U').unwrap();
        assert_eq!(c.underline_color, [10, 20, 30]);
    }

    #[test]
    fn underline_color_defaults_to_fg() {
        let mut t = term();
        t.advance(b"\x1b[38;2;1;2;3m\x1b[4mU");
        let f = t.frame();
        let c = f.cells.iter().find(|c| c.c == 'U').unwrap();
        assert_eq!(c.underline_color, [1, 2, 3]);
    }

    #[test]
    fn strikeout_flag() {
        let mut t = term();
        t.advance(b"\x1b[9mS");
        let f = t.frame();
        assert!(f.cells.iter().find(|c| c.c == 'S').unwrap().strike);
    }

    #[test]
    fn dim_darkens_fg() {
        let mut t = term();
        t.advance(b"\x1b[2m\x1b[38;2;90;90;90mD");
        let f = t.frame();
        let c = f.cells.iter().find(|c| c.c == 'D').unwrap();
        assert_eq!(c.fg, [60, 60, 60]); // 2/3 of 90
    }

    #[test]
    fn styled_space_still_emits_decoration_cell() {
        // A space with underline/strike draws something: must not be culled.
        let mut t = term();
        t.advance(b"\x1b[4m \x1b[0m");
        let f = t.frame();
        assert!(f.cells.iter().any(|c| c.col == 0 && c.underline == UnderlineKind::Single));
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

    #[test]
    fn mouse_mode_tracks_decset() {
        let mut t = term();
        assert_eq!(t.mouse_mode(), MouseMode::default(), "everything off initially");

        t.advance(b"\x1b[?1000h"); // click reporting
        let mm = t.mouse_mode();
        assert!(mm.reporting && !mm.drag && !mm.motion && !mm.sgr);

        t.advance(b"\x1b[?1002h\x1b[?1006h"); // + drag tracking, SGR coords
        let mm = t.mouse_mode();
        assert!(mm.reporting && mm.drag && mm.sgr);

        t.advance(b"\x1b[?1003h"); // any-motion tracking
        assert!(t.mouse_mode().motion);

        t.advance(b"\x1b[?1003l\x1b[?1002l\x1b[?1000l\x1b[?1006l");
        assert_eq!(t.mouse_mode(), MouseMode::default(), "all DECSET bits reset");
    }

    #[test]
    fn osc8_hyperlink_wins() {
        // default term() is 4x2 — too narrow; use a wider one
        let mut t = Terminal::new(40, 4, Arc::new(|_: &[u8]| {}));
        t.advance(b"\x1b]8;;https://osc8.example\x1b\\click\x1b]8;;\x1b\\");
        let l = t.link_at(2, 0).expect("osc8 link");
        assert_eq!(l.target, "https://osc8.example");
    }

    #[test]
    fn link_at_scans_row_text() {
        let mut t = Terminal::new(40, 4, Arc::new(|_: &[u8]| {}));
        t.advance(b"see https://example.com");
        assert_eq!(t.link_at(8, 0).unwrap().target, "https://example.com");
        assert!(t.link_at(1, 0).is_none());
    }
}
