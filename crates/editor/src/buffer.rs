//! Document: one open file — rope buffer, cursor/selection, undo/redo, dirty
//! tracking. Positions are (line, col) in chars (ponytail: char columns, not
//! grapheme clusters; upgrade to unicode-segmentation if combining marks
//! misrender).

use ropey::Rope;
use std::path::Path;

/// One undoable edit: chars replaced at [start, start+removed) with `inserted`.
/// Inverse is applied on undo; reapplied on redo.
struct Edit {
    /// Char index of the edit start.
    start: usize,
    removed: String,
    inserted: String,
    /// Cursor (line, col) before the edit (restored on undo).
    cursor_before: (usize, usize),
}

pub struct Document {
    rope: Rope,
    path: String,
    /// Cursor as (line, col in chars). col may equal line length (end of line).
    line: usize,
    col: usize,
    /// Selection anchor; selection = anchor..cursor when Some.
    anchor: Option<(usize, usize)>,
    undo: Vec<Edit>,
    redo: Vec<Edit>,
    dirty: bool,
    /// Bumped on every content change (highlight cache key).
    rev: u64,
}

impl Document {
    pub fn from_str(path: &str, text: &str) -> Self {
        Self {
            rope: Rope::from_str(text),
            path: path.to_string(),
            line: 0,
            col: 0,
            anchor: None,
            undo: Vec::new(),
            redo: Vec::new(),
            dirty: false,
            rev: 0,
        }
    }

    pub fn load(path: &Path) -> std::io::Result<Self> {
        let bytes = std::fs::read(path)?;
        let text = String::from_utf8(bytes).map_err(|_| {
            std::io::Error::new(std::io::ErrorKind::InvalidData, "not UTF-8 text")
        })?;
        Ok(Self::from_str(&path.to_string_lossy(), &text))
    }

    pub fn path(&self) -> &str {
        &self.path
    }

    pub fn text(&self) -> String {
        self.rope.to_string()
    }

    pub fn line_count(&self) -> usize {
        self.rope.len_lines()
    }

    /// Line content without the trailing newline.
    pub fn line(&self, i: usize) -> String {
        if i >= self.rope.len_lines() {
            return String::new();
        }
        let s = self.rope.line(i).to_string();
        s.trim_end_matches(['\n', '\r']).to_string()
    }

    pub fn cursor(&self) -> (usize, usize) {
        (self.line, self.col)
    }

    pub fn dirty(&self) -> bool {
        self.dirty
    }

    pub fn rev(&self) -> u64 {
        self.rev
    }

    pub fn save(&mut self) -> std::io::Result<()> {
        std::fs::write(&self.path, self.text())?;
        self.dirty = false;
        Ok(())
    }

    // ---- position helpers -------------------------------------------------

    fn line_len(&self, line: usize) -> usize {
        let s = self.rope.line(line);
        let mut n = s.len_chars();
        while n > 0 {
            let c = s.char(n - 1);
            if c == '\n' || c == '\r' {
                n -= 1;
            } else {
                break;
            }
        }
        n
    }

    fn clamp(&self, line: usize, col: usize) -> (usize, usize) {
        let line = line.min(self.rope.len_lines().saturating_sub(1));
        (line, col.min(self.line_len(line)))
    }

    fn char_idx(&self, line: usize, col: usize) -> usize {
        self.rope.line_to_char(line) + col
    }

    pub fn set_cursor(&mut self, line: usize, col: usize) {
        let (l, c) = self.clamp(line, col);
        self.line = l;
        self.col = c;
        self.anchor = None;
    }

    /// Extend/replace selection: keep (or set) the anchor, move the cursor.
    pub fn select_to(&mut self, line: usize, col: usize) {
        if self.anchor.is_none() {
            self.anchor = Some((self.line, self.col));
        }
        let (l, c) = self.clamp(line, col);
        self.line = l;
        self.col = c;
    }

    /// Selection as ordered char range, if non-empty.
    pub fn selection_range(&self) -> Option<(usize, usize)> {
        let (al, ac) = self.anchor?;
        let a = self.char_idx(al, ac);
        let b = self.char_idx(self.line, self.col);
        if a == b {
            None
        } else {
            Some((a.min(b), a.max(b)))
        }
    }

    pub fn selection_text(&self) -> Option<String> {
        let (a, b) = self.selection_range()?;
        Some(self.rope.slice(a..b).to_string())
    }

    fn cursor_from_char(&self, idx: usize) -> (usize, usize) {
        let line = self.rope.char_to_line(idx);
        (line, idx - self.rope.line_to_char(line))
    }

    // ---- edits (all funnel through `apply` so undo stays consistent) ------

    fn apply(&mut self, start: usize, removed_len: usize, inserted: &str) {
        let removed: String = self.rope.slice(start..start + removed_len).to_string();
        let cursor_before = (self.line, self.col);
        self.rope.remove(start..start + removed_len);
        self.rope.insert(start, inserted);
        let end = start + inserted.chars().count();
        let (l, c) = self.cursor_from_char(end);
        self.line = l;
        self.col = c;
        self.anchor = None;
        self.undo.push(Edit {
            start,
            removed,
            inserted: inserted.to_string(),
            cursor_before,
        });
        self.redo.clear();
        self.dirty = true;
        self.rev += 1;
    }

    pub fn insert(&mut self, s: &str) {
        let (start, removed) = match self.selection_range() {
            Some((a, b)) => (a, b - a),
            None => (self.char_idx(self.line, self.col), 0),
        };
        self.apply(start, removed, s);
    }

    pub fn backspace(&mut self) {
        match self.selection_range() {
            Some((a, b)) => self.apply(a, b - a, ""),
            None => {
                let idx = self.char_idx(self.line, self.col);
                if idx == 0 {
                    return;
                }
                // Delete one char back; treat CRLF as one unit.
                let mut n = 1;
                if idx >= 2
                    && self.rope.char(idx - 1) == '\n'
                    && self.rope.char(idx - 2) == '\r'
                {
                    n = 2;
                }
                self.apply(idx - n, n, "");
            }
        }
    }

    pub fn delete(&mut self) {
        match self.selection_range() {
            Some((a, b)) => self.apply(a, b - a, ""),
            None => {
                let idx = self.char_idx(self.line, self.col);
                if idx >= self.rope.len_chars() {
                    return;
                }
                let mut n = 1;
                if self.rope.char(idx) == '\r'
                    && idx + 1 < self.rope.len_chars()
                    && self.rope.char(idx + 1) == '\n'
                {
                    n = 2;
                }
                self.apply(idx, n, "");
            }
        }
    }

    pub fn undo(&mut self) -> bool {
        let Some(e) = self.undo.pop() else {
            return false;
        };
        let end = e.start + e.inserted.chars().count();
        self.rope.remove(e.start..end);
        self.rope.insert(e.start, &e.removed);
        let (l, c) = e.cursor_before;
        self.line = l.min(self.rope.len_lines().saturating_sub(1));
        self.col = c.min(self.line_len(self.line));
        self.anchor = None;
        self.dirty = true;
        self.rev += 1;
        self.redo.push(e);
        true
    }

    pub fn redo(&mut self) -> bool {
        let Some(e) = self.redo.pop() else {
            return false;
        };
        let end = e.start + e.removed.chars().count();
        self.rope.remove(e.start..end);
        self.rope.insert(e.start, &e.inserted);
        let (l, c) = self.cursor_from_char(e.start + e.inserted.chars().count());
        self.line = l;
        self.col = c;
        self.anchor = None;
        self.dirty = true;
        self.rev += 1;
        self.undo.push(e);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn doc(s: &str) -> Document {
        Document::from_str("test.txt", s)
    }

    #[test]
    fn insert_at_cursor_moves_cursor() {
        let mut d = doc("");
        d.insert("hi");
        assert_eq!(d.text(), "hi");
        assert_eq!(d.cursor(), (0, 2));
        d.insert("\n!");
        assert_eq!(d.text(), "hi\n!");
        assert_eq!(d.cursor(), (1, 1));
        assert!(d.dirty());
    }

    #[test]
    fn backspace_and_delete() {
        let mut d = doc("ab");
        d.set_cursor(0, 1);
        d.backspace();
        assert_eq!(d.text(), "b");
        assert_eq!(d.cursor(), (0, 0));
        d.delete();
        assert_eq!(d.text(), "");
        d.backspace(); // at origin: no-op, no panic
        assert_eq!(d.text(), "");
    }

    #[test]
    fn backspace_joins_lines() {
        let mut d = doc("a\nb");
        d.set_cursor(1, 0);
        d.backspace();
        assert_eq!(d.text(), "ab");
        assert_eq!(d.cursor(), (0, 1));
    }

    #[test]
    fn undo_redo_roundtrip() {
        let mut d = doc("x");
        d.set_cursor(0, 1);
        d.insert("y");
        d.insert("z");
        assert_eq!(d.text(), "xyz");
        assert!(d.undo());
        assert_eq!(d.text(), "xy");
        assert!(d.undo());
        assert_eq!(d.text(), "x");
        assert!(!d.undo()); // stack empty
        assert!(d.redo());
        assert!(d.redo());
        assert_eq!(d.text(), "xyz");
        assert!(!d.redo());
        // A fresh edit clears the redo branch.
        d.undo();
        d.insert("Q");
        assert!(!d.redo());
        assert_eq!(d.text(), "xyQ");
    }

    #[test]
    fn selection_replaced_by_insert() {
        let mut d = doc("hello world");
        d.set_cursor(0, 0);
        d.select_to(0, 5); // anchor at (0,0), cursor at (0,5)
        d.insert("bye");
        assert_eq!(d.text(), "bye world");
        assert_eq!(d.cursor(), (0, 3));
        // Undo restores both the deletion and the insertion as one step.
        assert!(d.undo());
        assert_eq!(d.text(), "hello world");
    }

    #[test]
    fn rev_bumps_on_edit_only() {
        let mut d = doc("a");
        let r0 = d.rev();
        d.set_cursor(0, 1); // movement is not an edit
        assert_eq!(d.rev(), r0);
        d.insert("b");
        assert!(d.rev() > r0);
    }

    #[test]
    fn save_writes_and_clears_dirty() {
        let p = std::env::temp_dir().join("velo_editor_test_save.txt");
        let mut d = Document::from_str(p.to_str().unwrap(), "");
        d.insert("data");
        assert!(d.dirty());
        d.save().unwrap();
        assert!(!d.dirty());
        assert_eq!(std::fs::read_to_string(&p).unwrap(), "data");
        let _ = std::fs::remove_file(&p);
    }

    #[test]
    fn load_rejects_non_utf8() {
        let p = std::env::temp_dir().join("velo_editor_test_bin.bin");
        std::fs::write(&p, [0xFF, 0xFE, 0x00, 0x80]).unwrap();
        assert!(Document::load(&p).is_err());
        let _ = std::fs::remove_file(&p);
    }
}
