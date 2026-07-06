//! Project a Document (+highlight spans) into a `term_core::Frame`: the same
//! cell grid the terminal renderer draws, so the editor inherits theme,
//! opacity, zoom, and smooth scrolling. ponytail: full damage every frame —
//! editor grids are the visible viewport only; per-row damage when profiles
//! demand it.

use crate::Document;
use term_core::{palette, CursorShape, Frame, FrameDamage, RenderCell};

pub const TAB_WIDTH: usize = 4;

/// One highlight run on a line: char columns [start, end) -> fg color.
#[derive(Clone, PartialEq, Debug)]
pub struct Span {
    pub start: usize,
    pub end: usize,
    pub color: [u8; 3],
}

#[derive(Default)]
pub struct View {
    /// First visible document line.
    top: usize,
    /// First visible text column (horizontal scroll; no word wrap in v1).
    left: usize,
    /// `top` at the previous frame — difference feeds Frame.scrolled_up.
    last_top: usize,
    /// Gutter width (digits + 1 space each side) at the previous frame; used
    /// by `hit` to subtract the gutter without re-measuring.
    gutter: usize,
    /// Cursor at the previous frame: the view follows the cursor only when it
    /// moved, so wheel scrolling can leave it off-screen (like every editor).
    last_cursor: Option<(usize, usize)>,
}

impl View {
    fn gutter_width(doc: &Document) -> usize {
        let digits = doc.line_count().max(1).to_string().len();
        digits + 2
    }

    /// Wheel scroll by `delta` lines (positive = down/toward end).
    pub fn scroll(&mut self, delta: i32, doc: &Document) {
        let max = doc.line_count().saturating_sub(1);
        self.top = (self.top as i64 + delta as i64).clamp(0, max as i64) as usize;
    }

    /// Viewport cell -> document position (before clamping by the Document).
    pub fn hit(&self, x_col: u16, y_row: u16) -> (usize, usize) {
        let line = self.top + y_row as usize;
        let col = (x_col as usize).saturating_sub(self.gutter) + self.left;
        (line, col)
    }

    /// Expand tabs for display; returns (visual string, char->visual col map).
    fn expand(line: &str) -> (Vec<char>, Vec<usize>) {
        let mut out = Vec::new();
        let mut map = Vec::new();
        for c in line.chars() {
            map.push(out.len());
            if c == '\t' {
                let n = TAB_WIDTH - (out.len() % TAB_WIDTH);
                out.extend(std::iter::repeat(' ').take(n));
            } else {
                out.push(c);
            }
        }
        map.push(out.len()); // end-of-line sentinel
        (out, map)
    }

    pub fn frame(
        &mut self,
        doc: &Document,
        hl: &[Vec<Span>],
        cols: u16,
        rows: u16,
    ) -> Frame {
        let gutter = Self::gutter_width(doc);
        self.gutter = gutter;
        let text_cols = (cols as usize).saturating_sub(gutter);
        let (cur_line, cur_col) = doc.cursor();

        // Keep the cursor in view (vertical, then horizontal) — but only when
        // it moved since the last frame, so wheel scrolling isn't snapped back.
        let (_, curmap) = Self::expand(&doc.line(cur_line));
        let cur_vis = curmap[cur_col.min(curmap.len() - 1)];
        if self.last_cursor != Some((cur_line, cur_col)) {
            if cur_line < self.top {
                self.top = cur_line;
            }
            if rows > 0 && cur_line >= self.top + rows as usize {
                self.top = cur_line + 1 - rows as usize;
            }
            if cur_vis < self.left {
                self.left = cur_vis;
            }
            if text_cols > 0 && cur_vis >= self.left + text_cols {
                self.left = cur_vis + 1 - text_cols;
            }
        }
        self.last_cursor = Some((cur_line, cur_col));

        let scrolled_up = self.top as i64 - self.last_top as i64;
        self.last_top = self.top;

        let sel = doc.selection_range();
        let fg = palette::fg();
        let gutter_fg = palette::ansi(8); // themed "bright black"
        let mut cells = Vec::new();

        let mut cursor_cell = (0u16, 0u16);
        let mut cursor_visible = false;

        for r in 0..rows as usize {
            let li = self.top + r;
            if li >= doc.line_count() {
                break;
            }
            // Gutter: right-aligned 1-based line number.
            let num = (li + 1).to_string();
            for (k, ch) in num.chars().enumerate() {
                let col = gutter - 1 - num.len() + k;
                cells.push(RenderCell {
                    col: col as u16,
                    row: r as u16,
                    c: ch,
                    fg: gutter_fg,
                    bg: palette::DEFAULT_BG,
                    bold: false,
                    italic: false,
                    underline: false,
                    selected: false,
                });
            }

            let line = doc.line(li);
            let (vis, map) = Self::expand(&line);
            let line_start_char = doc.line_start_char(li);

            for (ci, ch) in line.chars().enumerate() {
                let v0 = map[ci];
                let v1 = map[ci + 1];
                let selected = sel.is_some_and(|(a, b)| {
                    let idx = line_start_char + ci;
                    idx >= a && idx < b
                });
                let color = hl
                    .get(li)
                    .and_then(|spans| {
                        spans
                            .iter()
                            .find(|s| ci >= s.start && ci < s.end)
                            .map(|s| s.color)
                    })
                    .unwrap_or(fg);
                for v in v0..v1 {
                    if v < self.left || v - self.left >= text_cols {
                        continue;
                    }
                    let vc = if ch == '\t' { ' ' } else { vis[v] };
                    if vc == ' ' && !selected {
                        continue; // background-only cell, nothing to draw
                    }
                    cells.push(RenderCell {
                        col: (gutter + v - self.left) as u16,
                        row: r as u16,
                        c: if vc == ' ' { '\0' } else { vc },
                        fg: color,
                        bg: palette::DEFAULT_BG,
                        bold: false,
                        italic: false,
                        underline: false,
                        selected,
                    });
                }
            }

            if li == cur_line {
                let v = curmap[cur_col.min(curmap.len() - 1)];
                if v >= self.left && (v - self.left) < text_cols.max(1) {
                    cursor_cell = ((gutter + v - self.left) as u16, r as u16);
                    cursor_visible = true;
                }
            }
        }

        // Frame.cells must be row-major sorted (renderer::row_ranges relies
        // on it); gutter cells interleave with text per row, so sort by (row, col).
        cells.sort_by_key(|c| (c.row, c.col));

        Frame {
            cols,
            rows,
            cells,
            cursor_col: cursor_cell.0,
            cursor_row: cursor_cell.1,
            cursor_shape: if cursor_visible {
                CursorShape::Bar
            } else {
                CursorShape::Hidden
            },
            damage: FrameDamage::Full,
            scrolled_up: scrolled_up.clamp(i32::MIN as i64, i32::MAX as i64) as i32,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Document;

    fn grid(f: &term_core::Frame) -> Vec<String> {
        // Reassemble the frame's glyphs into rows of text for assertions.
        let mut rows = vec![vec![' '; f.cols as usize]; f.rows as usize];
        for c in &f.cells {
            if c.c != '\0' {
                rows[c.row as usize][c.col as usize] = c.c;
            }
        }
        rows.into_iter().map(|r| r.into_iter().collect()).collect()
    }

    #[test]
    fn renders_text_with_gutter() {
        let d = Document::from_str("t.txt", "hello\nworld");
        let mut v = View::default();
        let f = v.frame(&d, &[], 20, 5);
        let g = grid(&f);
        // 2 lines -> gutter width 1 digit + 1 space padding each side = " 1 ".
        assert!(g[0].contains("hello"), "row0: {:?}", g[0]);
        assert!(g[0].trim_start().starts_with('1'), "row0: {:?}", g[0]);
        assert!(g[1].contains("world"));
        // Cursor at doc (0,0) lands after the gutter.
        assert_eq!(f.cursor_row, 0);
        assert!(f.cursor_col >= 2);
    }

    #[test]
    fn scrolls_to_keep_cursor_visible_and_reports_glide() {
        let text = (0..100).map(|i| format!("line{i}\n")).collect::<String>();
        let mut d = Document::from_str("t.txt", &text);
        let mut v = View::default();
        let _ = v.frame(&d, &[], 20, 10);
        d.set_cursor(50, 0);
        let f = v.frame(&d, &[], 20, 10);
        // Cursor visible: top scrolled so line 50 is the bottom row.
        assert_eq!(f.cursor_row as usize, 9);
        // Content moved up -> positive scrolled_up matching the delta.
        assert_eq!(f.scrolled_up, 41); // top went 0 -> 41
        let f2 = v.frame(&d, &[], 20, 10);
        assert_eq!(f2.scrolled_up, 0); // settled
    }

    #[test]
    fn wheel_scroll_clamps() {
        let d = Document::from_str("t.txt", "a\nb\nc");
        let mut v = View::default();
        let _ = v.frame(&d, &[], 20, 2);
        v.scroll(100, &d); // way past end
        let f = v.frame(&d, &[], 20, 2);
        // Top clamps to last line; cursor (0,0) is off-screen -> hidden.
        assert!(matches!(f.cursor_shape, term_core::CursorShape::Hidden));
        v.scroll(-100, &d);
        let f = v.frame(&d, &[], 20, 2);
        assert_eq!(f.cursor_row, 0);
    }

    #[test]
    fn tabs_render_as_four_spaces() {
        let d = Document::from_str("t.txt", "\tx");
        let mut v = View::default();
        let f = v.frame(&d, &[], 20, 2);
        let g = grid(&f);
        // After gutter+tab, 'x' sits at visual col 4 within the text area.
        let text_start = g[0].find(|c: char| c.is_ascii_digit()).unwrap() + 2;
        assert_eq!(g[0].chars().nth(text_start + 4), Some('x'), "{:?}", g[0]);
    }

    #[test]
    fn selection_marks_cells() {
        let mut d = Document::from_str("t.txt", "abcdef");
        let mut v = View::default();
        d.set_cursor(0, 1);
        d.select_to(0, 4);
        let f = v.frame(&d, &[], 20, 2);
        let sel: Vec<u16> = f.cells.iter().filter(|c| c.selected).map(|c| c.col).collect();
        assert_eq!(sel.len(), 3, "chars b,c,d selected");
    }

    #[test]
    fn hit_maps_back_through_gutter_and_scroll() {
        let text = (0..100).map(|i| format!("line{i}\n")).collect::<String>();
        let mut d = Document::from_str("t.txt", &text);
        let mut v = View::default();
        d.set_cursor(50, 0);
        let f = v.frame(&d, &[], 20, 10);
        let gutter = f.cursor_col; // cursor at col 0 -> x offset = gutter
        let (line, col) = v.hit(gutter + 3, 9);
        assert_eq!((line, col), (50, 3));
    }

    #[test]
    fn highlight_spans_color_cells() {
        let d = Document::from_str("t.txt", "let x");
        let mut v = View::default();
        let hl = vec![vec![Span { start: 0, end: 3, color: [1, 2, 3] }]];
        let f = v.frame(&d, &hl, 20, 2);
        let colored: Vec<char> = f
            .cells
            .iter()
            .filter(|c| c.fg == [1, 2, 3])
            .map(|c| c.c)
            .collect();
        assert_eq!(colored, vec!['l', 'e', 't']);
    }
}
