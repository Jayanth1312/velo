# Editor Mode (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Files button toggles a global editor workspace in the terminal body: VS Code-style multi-file tabs, syntax highlighting, auto-save, engine-owned state that survives view switches.

**Architecture:** New `crates/editor` holds documents (ropey), cursor/selection/undo, tree-sitter highlighting, and projects the active document into a `term_core::Frame`. The ffi `Pane` gains a `kind` (Terminal | Editor); an editor pane renders workspace frames through the *existing* wgpu renderer, so theme, opacity, zoom, and smooth scrolling (`Frame.scrolled_up`) work unchanged. C# adds an EditorHost (file tab strip + SwapChainPanel) shown instead of PaneHost while editor mode is on; terminal panes are hidden, never destroyed.

**Tech Stack:** Rust: ropey, tree-sitter + tree-sitter-highlight + 8 grammar crates. C#: WinUI3, existing `LibraryImport` ABI patterns.

## Global Constraints

- Tests must run on Linux: `cargo test -p editor -p term-core -p renderer`; ffi verified with `cargo check --target x86_64-pc-windows-gnu -p velo_core`.
- New deps allowed ONLY in `crates/editor`: ropey, tree-sitter, tree-sitter-highlight, tree-sitter-{rust,javascript,typescript,go,python,java,html,css}. Add via `cargo add` (latest compatible); register `editor = { path = "crates/editor" }` in the workspace `Cargo.toml`.
- v1 feature freeze (spec): edit, undo/redo, select/copy/paste, line numbers, highlighting. NO split view, NO multi-cursor, NO word-wrap, NO find (find slipped to Phase 3 with LSP UI — it needs the same overlay plumbing).
- Auto-save: 1 s debounce after last edit + flush on file close / editor-mode exit. Last-write-wins on external conflicts.
- ABI: append-only on `VeloCallbacks`; every Rust export mirrored in `csharp/Velo.App/Interop.cs`.
- Editor colors come from the themed terminal palette (`term_core::palette`) — no separate theme system.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## File Structure

```
crates/editor/
  Cargo.toml
  src/lib.rs        — pub use; Workspace is the only public entry point
  src/buffer.rs     — Document: rope, cursor, selection, undo, edits, save/load
  src/view.rs       — Document -> term_core::Frame projection (gutter, hscroll)
  src/highlight.rs  — tree-sitter per-line color spans, language by extension
  src/workspace.rs  — open files, ids, focus, dirty set, save_all
crates/ffi/src/lib.rs        — PaneKind, editor pane, velo_editor_* exports
crates/term-core/src/palette.rs — pub fn ansi(i) themed accessor
csharp/Velo.App/MainWindow.xaml     — EditorHost grid
csharp/Velo.App/MainWindow.xaml.cs  — editor mode toggle, tab strip, autosave
csharp/Velo.App/EditorFileVM.cs     — open-file tab view model
csharp/Velo.App/Interop.cs          — ABI mirror
```

---

### Task 1: `crates/editor` scaffold + Document editing core

**Files:**
- Create: `crates/editor/Cargo.toml`, `crates/editor/src/lib.rs`, `crates/editor/src/buffer.rs`
- Modify: `Cargo.toml` (workspace members + internal dep entry)

**Interfaces:**
- Produces (used by every later task):
  - `Document::from_str(path: &str, text: &str) -> Document` and `Document::load(path: &Path) -> std::io::Result<Document>` (rejects non-UTF-8 with `InvalidData`).
  - `Document::insert(&mut self, s: &str)` — inserts at cursor (replacing selection), moves cursor.
  - `Document::backspace(&mut self)`, `Document::delete(&mut self)` — delete selection if any, else one char.
  - `Document::undo(&mut self) -> bool`, `Document::redo(&mut self) -> bool`.
  - `Document::text(&self) -> String`, `Document::line_count(&self) -> usize`, `Document::line(&self, i) -> String`.
  - `Document::dirty(&self) -> bool`, `Document::save(&mut self) -> std::io::Result<()>` (clears dirty), `Document::path(&self) -> &str`, `Document::rev(&self) -> u64` (bumps on every edit; highlight cache key).
  - Cursor position: `Document::cursor(&self) -> (usize, usize)` (line, col in chars).

- [ ] **Step 1: Scaffold the crate**

`crates/editor/Cargo.toml`:

```toml
[package]
name = "editor"
edition.workspace = true
version.workspace = true

[dependencies]
term-core = { workspace = true }
```

Then run (from repo root): `cargo add -p editor ropey`

Workspace `Cargo.toml`: add `"crates/editor",` to `members` and `editor = { path = "crates/editor" }` under the internal path deps.

`crates/editor/src/lib.rs`:

```rust
//! editor: in-terminal text editor core. Documents (rope buffers with
//! cursor/selection/undo), tree-sitter highlighting, and a projector that
//! turns the active document into a `term_core::Frame` so the existing
//! terminal renderer draws it (theme/opacity/zoom/smooth-scroll for free).

mod buffer;
pub use buffer::Document;
```

- [ ] **Step 2: Write the failing tests**

At the bottom of `crates/editor/src/buffer.rs` (create the file with just the test module first):

```rust
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
```

- [ ] **Step 3: Run to verify failure**

Run: `cargo test -p editor`
Expected: compile FAIL — `Document` not defined.

- [ ] **Step 4: Implement `Document`**

`crates/editor/src/buffer.rs` (above the test module):

```rust
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
        // Exclude the newline (and CR of CRLF) from addressable columns.
        for c in s.chars_at(n).reversed().take(2) {
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
```

Note on `line_len`: `chars_at(n).reversed()` iterates backward from the end of the line slice — if the ropey API differs on the pinned version, the equivalent explicit form is:

```rust
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
```

Use whichever compiles; behavior must match the tests.

- [ ] **Step 5: Run tests**

Run: `cargo test -p editor`
Expected: 8 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Cargo.toml Cargo.lock crates/editor
git commit -m "feat: editor crate — Document rope buffer with undo/redo

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Cursor movement + word ops

**Files:**
- Modify: `crates/editor/src/buffer.rs`

**Interfaces:**
- Produces (Task 6's key handler calls these):
  - `Document::move_cursor(&mut self, m: Move, select: bool)` where
    `pub enum Move { Left, Right, Up, Down, WordLeft, WordRight, Home, End, PageUp(usize), PageDown(usize), DocStart, DocEnd }`
  - `select: bool` = shift held (extend selection instead of collapsing).
  - Vertical moves keep a sticky "goal column" so Up/Down through short lines returns to the original column.

- [ ] **Step 1: Write the failing tests**

Append inside the existing `mod tests`:

```rust
    #[test]
    fn arrows_and_line_wrapping_moves() {
        let mut d = doc("ab\ncd");
        d.set_cursor(0, 2);
        d.move_cursor(Move::Right, false); // wraps to next line
        assert_eq!(d.cursor(), (1, 0));
        d.move_cursor(Move::Left, false); // wraps back
        assert_eq!(d.cursor(), (0, 2));
        d.move_cursor(Move::Home, false);
        assert_eq!(d.cursor(), (0, 0));
        d.move_cursor(Move::End, false);
        assert_eq!(d.cursor(), (0, 2));
    }

    #[test]
    fn up_down_sticky_column() {
        let mut d = doc("longline\nab\nlongline");
        d.set_cursor(0, 6);
        d.move_cursor(Move::Down, false); // short line clamps
        assert_eq!(d.cursor(), (1, 2));
        d.move_cursor(Move::Down, false); // returns to goal col
        assert_eq!(d.cursor(), (2, 6));
    }

    #[test]
    fn word_moves() {
        let mut d = doc("foo bar_baz  qux");
        d.set_cursor(0, 0);
        d.move_cursor(Move::WordRight, false);
        assert_eq!(d.cursor(), (0, 4)); // start of "bar_baz"
        d.move_cursor(Move::WordRight, false);
        assert_eq!(d.cursor(), (0, 13)); // start of "qux"
        d.move_cursor(Move::WordLeft, false);
        assert_eq!(d.cursor(), (0, 4));
    }

    #[test]
    fn shift_moves_select() {
        let mut d = doc("hello");
        d.set_cursor(0, 0);
        d.move_cursor(Move::Right, true);
        d.move_cursor(Move::Right, true);
        assert_eq!(d.selection_text().as_deref(), Some("he"));
        d.move_cursor(Move::Right, false); // unshifted move collapses
        assert!(d.selection_text().is_none());
    }

    #[test]
    fn page_and_doc_moves() {
        let text = (0..50).map(|i| format!("l{i}\n")).collect::<String>();
        let mut d = doc(&text);
        d.move_cursor(Move::PageDown(10), false);
        assert_eq!(d.cursor().0, 10);
        d.move_cursor(Move::DocEnd, false);
        assert_eq!(d.cursor().0, d.line_count() - 1);
        d.move_cursor(Move::PageUp(10), false);
        assert_eq!(d.cursor().0, d.line_count() - 1 - 10);
        d.move_cursor(Move::DocStart, false);
        assert_eq!(d.cursor(), (0, 0));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p editor move`
Expected: compile FAIL — `Move` not defined.

- [ ] **Step 3: Implement**

Add to `buffer.rs`:

```rust
/// Cursor movement commands (shift-extended when `select` is true).
#[derive(Clone, Copy)]
pub enum Move {
    Left,
    Right,
    Up,
    Down,
    WordLeft,
    WordRight,
    Home,
    End,
    /// Page size = visible rows, passed by the caller.
    PageUp(usize),
    PageDown(usize),
    DocStart,
    DocEnd,
}

fn is_word(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}
```

Add a `goal_col: Option<usize>` field to `Document` (init `None`; set it to `None` inside `set_cursor`, `select_to`, and `apply`). Then:

```rust
    pub fn move_cursor(&mut self, m: Move, select: bool) {
        if select && self.anchor.is_none() {
            self.anchor = Some((self.line, self.col));
        }
        // Vertical moves remember the widest column wanted; everything else
        // resets the goal.
        let mut keep_goal = false;
        match m {
            Move::Left => {
                if self.col > 0 {
                    self.col -= 1;
                } else if self.line > 0 {
                    self.line -= 1;
                    self.col = self.line_len(self.line);
                }
            }
            Move::Right => {
                if self.col < self.line_len(self.line) {
                    self.col += 1;
                } else if self.line + 1 < self.rope.len_lines() {
                    self.line += 1;
                    self.col = 0;
                }
            }
            Move::Up | Move::Down | Move::PageUp(_) | Move::PageDown(_) => {
                keep_goal = true;
                let goal = self.goal_col.unwrap_or(self.col);
                self.goal_col = Some(goal);
                let delta = match m {
                    Move::Up => -1i64,
                    Move::Down => 1,
                    Move::PageUp(n) => -(n as i64),
                    Move::PageDown(n) => n as i64,
                    _ => unreachable!(),
                };
                let max = self.rope.len_lines().saturating_sub(1) as i64;
                self.line = (self.line as i64 + delta).clamp(0, max) as usize;
                self.col = goal.min(self.line_len(self.line));
            }
            Move::WordLeft => {
                let mut idx = self.char_idx(self.line, self.col);
                while idx > 0 && !is_word(self.rope.char(idx - 1)) {
                    idx -= 1;
                }
                while idx > 0 && is_word(self.rope.char(idx - 1)) {
                    idx -= 1;
                }
                let (l, c) = self.cursor_from_char(idx);
                self.line = l;
                self.col = c;
            }
            Move::WordRight => {
                let len = self.rope.len_chars();
                let mut idx = self.char_idx(self.line, self.col);
                while idx < len && is_word(self.rope.char(idx)) {
                    idx += 1;
                }
                while idx < len && !is_word(self.rope.char(idx)) {
                    idx += 1;
                }
                let (l, c) = self.cursor_from_char(idx);
                self.line = l;
                self.col = c;
            }
            Move::Home => self.col = 0,
            Move::End => self.col = self.line_len(self.line),
            Move::DocStart => {
                self.line = 0;
                self.col = 0;
            }
            Move::DocEnd => {
                self.line = self.rope.len_lines().saturating_sub(1);
                self.col = self.line_len(self.line);
            }
        }
        if !keep_goal {
            self.goal_col = None;
        }
        if !select {
            self.anchor = None;
        }
    }
```

(`set_cursor`, `select_to`, and `apply` each gain `self.goal_col = None;`.)

- [ ] **Step 4: Run tests**

Run: `cargo test -p editor`
Expected: 13 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add crates/editor/src/buffer.rs
git commit -m "feat: editor cursor movement — arrows, words, pages, sticky column

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: View projection — Document → `term_core::Frame`

**Files:**
- Create: `crates/editor/src/view.rs`
- Modify: `crates/editor/src/lib.rs` (`mod view; pub use view::View;`)
- Modify: `crates/term-core/src/palette.rs` (public themed accessor)

**Interfaces:**
- Consumes: `Document` (Tasks 1-2); `term_core::{Frame, FrameDamage, RenderCell, CursorShape, palette}`.
- Produces (Task 6 renders this):
  - `View::default()` — per-document view state (vertical `top` line, horizontal `left` col, last frame bookkeeping).
  - `View::frame(&mut self, doc: &Document, hl: &[Vec<Span>], cols: u16, rows: u16) -> Frame` — full-damage frame: line-number gutter, tab expansion, selection, cursor; auto-scrolls to keep the cursor visible; sets `Frame.scrolled_up` to the vertical scroll delta so the renderer glides.
  - `View::scroll(&mut self, delta: i32, doc: &Document)` — wheel.
  - `View::hit(&self, x_col: u16, y_row: u16) -> (usize, usize)` — viewport cell → document (line, col) for mouse clicks (accounts for gutter, top, left; caller clamps via `Document::set_cursor`).
  - `pub struct Span { pub start: usize, pub end: usize, pub color: [u8; 3] }` (char cols; defined in view.rs so Task 4 can import it from the crate root).
- New in term-core palette: `pub fn ansi(i: usize) -> [u8; 3]` — themed for i<16, fixed table above.

- [ ] **Step 1: term-core palette accessor**

In `crates/term-core/src/palette.rs` add after `pub fn selection()`:

```rust
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
```

Run: `cargo test -p term-core` — existing tests still pass.

- [ ] **Step 2: Write the failing view tests**

`crates/editor/src/view.rs` starts as just this test module:

```rust
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
        let row = g[0].trim_start_matches(|c: char| c.is_ascii_digit() || c == ' ');
        // After gutter+tab, 'x' sits at visual col 4 within the text area.
        let text_start = g[0].find(|c: char| c.is_ascii_digit()).unwrap() + 2;
        assert_eq!(g[0].chars().nth(text_start + 4), Some('x'), "{row:?}");
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
        let gutter = (f.cursor_col) as u16; // cursor at col 0 -> x offset = gutter
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
```

- [ ] **Step 3: Run to verify failure**

Run: `cargo test -p editor view`
Expected: compile FAIL — `View`/`Span` not defined.

- [ ] **Step 4: Implement `View`**

Top of `crates/editor/src/view.rs`:

```rust
//! Project a Document (+highlight spans) into a `term_core::Frame`: the same
//! cell grid the terminal renderer draws, so the editor inherits theme,
//! opacity, zoom, and smooth scrolling. ponytail: full damage every frame —
//! editor grids are the visible viewport only; per-row damage when profiles
//! demand it.

use crate::Document;
use term_core::{palette, CursorShape, Frame, FrameDamage, RenderCell};

pub const TAB_WIDTH: usize = 4;

/// One highlight run on a line: char columns [start, end) -> fg color.
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

        // Keep the cursor in view (vertical, then horizontal).
        if cur_line < self.top {
            self.top = cur_line;
        }
        if rows > 0 && cur_line >= self.top + rows as usize {
            self.top = cur_line + 1 - rows as usize;
        }
        // Horizontal: use the cursor's VISUAL column on its line.
        let (_, curmap) = Self::expand(&doc.line(cur_line));
        let cur_vis = curmap[cur_col.min(curmap.len() - 1)];
        if cur_vis < self.left {
            self.left = cur_vis;
        }
        if text_cols > 0 && cur_vis >= self.left + text_cols {
            self.left = cur_vis + 1 - text_cols;
        }

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
                let col = gutter - 2 - num.len() + 1 + k;
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
            let line_start_char = doc_line_char(doc, li);

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

/// Char index of the start of `line` (mirrors Document internals without
/// exposing the rope).
fn doc_line_char(doc: &Document, line: usize) -> usize {
    doc.line_start_char(line)
}
```

Add to `Document` (buffer.rs):

```rust
    /// Char index where `line` starts (view/selection bookkeeping).
    pub fn line_start_char(&self, line: usize) -> usize {
        self.rope.line_to_char(line.min(self.rope.len_lines().saturating_sub(1)))
    }
```

And `crates/editor/src/lib.rs` becomes:

```rust
mod buffer;
mod view;
pub use buffer::{Document, Move};
pub use view::{Span, View};
```

- [ ] **Step 5: Run tests**

Run: `cargo test -p editor`
Expected: all pass (13 + 7 new). Debug coordinate math against the test expectations, not the other way around — the tests encode the gutter/tab/scroll contract Task 6 renders.

- [ ] **Step 6: Commit**

```bash
git add crates/editor crates/term-core/src/palette.rs
git commit -m "feat: editor view — Frame projection with gutter, hscroll, selection

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Syntax highlighting (tree-sitter)

**Files:**
- Create: `crates/editor/src/highlight.rs`
- Modify: `crates/editor/src/lib.rs` (`mod highlight; pub use highlight::Highlighter;`), `crates/editor/Cargo.toml`

**Interfaces:**
- Consumes: `Span` (Task 3).
- Produces (Task 5 owns one per document):
  - `Highlighter::for_path(path: &str) -> Option<Highlighter>` — language by extension: rs, js/mjs/cjs/jsx, ts/mts/cts, tsx, go, py, java, html/htm, css. `None` = no highlighting (plain text).
  - `Highlighter::lines(&mut self, text: &str, rev: u64) -> &[Vec<Span>]` — per-line spans; recomputes only when `rev` changed (cache key). Skips files > 1 MiB (returns empty).

- [ ] **Step 1: Add dependencies**

```bash
cargo add -p editor tree-sitter tree-sitter-highlight tree-sitter-rust \
  tree-sitter-javascript tree-sitter-typescript tree-sitter-go \
  tree-sitter-python tree-sitter-java tree-sitter-html tree-sitter-css
```

If any grammar crate fails version resolution against the `tree-sitter` core, pin the core to the version the grammars agree on (check the error output; grammars publish `tree-sitter` ranges in their manifests).

- [ ] **Step 2: Write the failing tests**

Test module in `crates/editor/src/highlight.rs`:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rust_keywords_get_colored() {
        let mut h = Highlighter::for_path("main.rs").expect("rust supported");
        let spans = h.lines("fn main() {}\n", 1);
        // "fn" on line 0 must be inside some span (keyword capture).
        let hit = spans[0].iter().any(|s| s.start == 0 && s.end >= 2);
        assert!(hit, "spans: {:?}", spans[0].iter().map(|s| (s.start, s.end)).collect::<Vec<_>>());
    }

    #[test]
    fn cache_by_rev() {
        let mut h = Highlighter::for_path("a.py").unwrap();
        let p1 = h.lines("def f():\n    pass\n", 7).as_ptr();
        let p2 = h.lines("IGNORED — same rev means cache hit", 7).as_ptr();
        assert_eq!(p1, p2);
    }

    #[test]
    fn unknown_extension_is_none() {
        assert!(Highlighter::for_path("notes.txt").is_none());
        assert!(Highlighter::for_path("Makefile").is_none());
    }

    #[test]
    fn all_advertised_languages_construct() {
        for f in [
            "a.rs", "a.js", "a.jsx", "a.ts", "a.tsx", "a.go", "a.py",
            "a.java", "a.html", "a.css",
        ] {
            assert!(Highlighter::for_path(f).is_some(), "{f} must be supported");
        }
    }

    #[test]
    fn huge_files_skip_highlighting() {
        let mut h = Highlighter::for_path("big.rs").unwrap();
        let big = "// x\n".repeat(300_000); // > 1 MiB
        assert!(h.lines(&big, 1).iter().all(|l| l.is_empty()));
    }

    #[test]
    fn multiline_spans_split_per_line() {
        let mut h = Highlighter::for_path("a.rs").unwrap();
        let spans = h.lines("/* a\nb */ fn x() {}\n", 1);
        // The block comment covers parts of line 0 and line 1.
        assert!(!spans[0].is_empty());
        assert!(!spans[1].is_empty());
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `cargo test -p editor highlight`
Expected: compile FAIL — `Highlighter` not defined.

- [ ] **Step 4: Implement**

`crates/editor/src/highlight.rs`:

```rust
//! tree-sitter syntax highlighting -> per-line [`Span`]s. Colors are themed
//! ANSI palette indices so terminal themes restyle code automatically.
//! ponytail: full re-highlight per edit, cached by Document::rev; files over
//! 1 MiB skip highlighting. Incremental parsing when profiles demand it.

use crate::Span;
use term_core::palette;
use tree_sitter_highlight::{HighlightConfiguration, HighlightEvent, Highlighter as TsHighlighter};

/// Capture names we recognize, ordered; index = highlight id.
/// Colors: themed ANSI slots (keyword=magenta 5, string=green 2, comment=
/// bright-black 8, number/constant=yellow 3, function=blue 4, type=cyan 6,
/// property=cyan 6, operator/punctuation=white 7).
const CAPTURES: &[(&str, usize)] = &[
    ("keyword", 5),
    ("string", 2),
    ("comment", 8),
    ("number", 3),
    ("constant", 3),
    ("constant.builtin", 3),
    ("function", 4),
    ("function.method", 4),
    ("type", 6),
    ("type.builtin", 6),
    ("property", 6),
    ("attribute", 6),
    ("operator", 7),
    ("punctuation.bracket", 7),
    ("punctuation.delimiter", 7),
    ("variable.builtin", 5),
    ("tag", 5),
    ("label", 3),
];

const MAX_HIGHLIGHT_BYTES: usize = 1 << 20;

pub struct Highlighter {
    config: HighlightConfiguration,
    ts: TsHighlighter,
    cached_rev: Option<u64>,
    cache: Vec<Vec<Span>>,
}

impl Highlighter {
    pub fn for_path(path: &str) -> Option<Self> {
        let ext = path.rsplit('.').next()?.to_ascii_lowercase();
        // (language, highlights query) per supported extension.
        let (lang, query): (tree_sitter::Language, &str) = match ext.as_str() {
            "rs" => (tree_sitter_rust::LANGUAGE.into(), tree_sitter_rust::HIGHLIGHTS_QUERY),
            "js" | "mjs" | "cjs" | "jsx" => (
                tree_sitter_javascript::LANGUAGE.into(),
                tree_sitter_javascript::HIGHLIGHT_QUERY,
            ),
            "ts" | "mts" | "cts" => (
                tree_sitter_typescript::LANGUAGE_TYPESCRIPT.into(),
                tree_sitter_typescript::HIGHLIGHTS_QUERY,
            ),
            "tsx" => (
                tree_sitter_typescript::LANGUAGE_TSX.into(),
                tree_sitter_typescript::HIGHLIGHTS_QUERY,
            ),
            "go" => (tree_sitter_go::LANGUAGE.into(), tree_sitter_go::HIGHLIGHTS_QUERY),
            "py" => (
                tree_sitter_python::LANGUAGE.into(),
                tree_sitter_python::HIGHLIGHTS_QUERY,
            ),
            "java" => (
                tree_sitter_java::LANGUAGE.into(),
                tree_sitter_java::HIGHLIGHTS_QUERY,
            ),
            "html" | "htm" => (
                tree_sitter_html::LANGUAGE.into(),
                tree_sitter_html::HIGHLIGHTS_QUERY,
            ),
            "css" => (tree_sitter_css::LANGUAGE.into(), tree_sitter_css::HIGHLIGHTS_QUERY),
            _ => return None,
        };
        let names: Vec<&str> = CAPTURES.iter().map(|(n, _)| *n).collect();
        let mut config = HighlightConfiguration::new(lang, &ext, query, "", "").ok()?;
        config.configure(&names);
        Some(Self {
            config,
            ts: TsHighlighter::new(),
            cached_rev: None,
            cache: Vec::new(),
        })
    }

    /// Per-line spans for `text`; recomputed only when `rev` changes.
    pub fn lines(&mut self, text: &str, rev: u64) -> &[Vec<Span>] {
        if self.cached_rev == Some(rev) {
            return &self.cache;
        }
        self.cached_rev = Some(rev);
        let n_lines = text.split('\n').count();
        self.cache = vec![Vec::new(); n_lines.max(1)];
        if text.len() > MAX_HIGHLIGHT_BYTES {
            return &self.cache;
        }

        // Byte offset -> (line, char col) walker. Events arrive in byte order.
        let line_starts: Vec<usize> = std::iter::once(0)
            .chain(text.match_indices('\n').map(|(i, _)| i + 1))
            .collect();
        let to_pos = |byte: usize| -> (usize, usize) {
            let line = match line_starts.binary_search(&byte) {
                Ok(l) => l,
                Err(l) => l - 1,
            };
            let col = text[line_starts[line]..byte].chars().count();
            (line, col)
        };

        let Ok(events) = self.ts.highlight(&self.config, text.as_bytes(), None, |_| None) else {
            return &self.cache;
        };
        let mut current: Option<usize> = None;
        for ev in events.flatten() {
            match ev {
                HighlightEvent::HighlightStart(h) => current = Some(h.0),
                HighlightEvent::HighlightEnd => current = None,
                HighlightEvent::Source { start, end } => {
                    let Some(id) = current else { continue };
                    let color = palette::ansi(CAPTURES[id].1);
                    let (sl, sc) = to_pos(start);
                    let (el, ec) = to_pos(end);
                    if sl == el {
                        self.cache[sl].push(Span { start: sc, end: ec, color });
                    } else {
                        // Split multi-line captures at line boundaries.
                        self.cache[sl].push(Span { start: sc, end: usize::MAX / 2, color });
                        for l in (sl + 1)..el {
                            self.cache[l].push(Span { start: 0, end: usize::MAX / 2, color });
                        }
                        if ec > 0 {
                            self.cache[el].push(Span { start: 0, end: ec, color });
                        }
                    }
                }
            }
        }
        &self.cache
    }
}
```

Const names vary slightly between grammar crates (`HIGHLIGHTS_QUERY` vs `HIGHLIGHT_QUERY`, `LANGUAGE` vs `language()`): if the build errors, check the crate docs (`cargo doc -p <crate> --no-deps` or the crate source under `~/.cargo/registry`) and use the actual exported names — the mapping table above is the intent, not gospel.

- [ ] **Step 5: Run tests**

Run: `cargo test -p editor`
Expected: all pass (20 + 6 new). Also: `cargo check --target x86_64-pc-windows-gnu -p editor` — grammar crates compile C via `cc`; this validates the mingw cross-build early (known risk).

- [ ] **Step 6: Commit**

```bash
git add crates/editor Cargo.toml Cargo.lock
git commit -m "feat: tree-sitter highlighting for 8 languages, themed palette colors

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Workspace — multi-file state

**Files:**
- Create: `crates/editor/src/workspace.rs`
- Modify: `crates/editor/src/lib.rs` (`mod workspace; pub use workspace::Workspace;`)

**Interfaces:**
- Consumes: `Document`, `View`, `Highlighter` (Tasks 1-4).
- Produces (Task 6's ABI wraps exactly this):
  - `Workspace::default()`
  - `Workspace::open(&mut self, path: &Path) -> std::io::Result<u32>` — loads (or refocuses, dedup by canonical path) and focuses; returns stable file id.
  - `Workspace::close(&mut self, id: u32) -> std::io::Result<()>` — saves if dirty, removes; focus moves to the next remaining file (or none).
  - `Workspace::focus(&mut self, id: u32)`, `Workspace::focused(&self) -> Option<u32>`
  - `Workspace::save_all(&mut self) -> std::io::Result<()>`
  - `Workspace::dirty_ids(&self) -> Vec<u32>`
  - `Workspace::doc_mut(&mut self) -> Option<&mut Document>` — focused document (edit ops).
  - `Workspace::frame(&mut self, cols: u16, rows: u16) -> Option<term_core::Frame>` — focused doc through its View + Highlighter.
  - `Workspace::scroll(&mut self, delta: i32)`, `Workspace::hit(&self, col: u16, row: u16) -> Option<(usize, usize)>`

- [ ] **Step 1: Write the failing tests**

Test module in `crates/editor/src/workspace.rs`:

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn tmp(name: &str, content: &str) -> std::path::PathBuf {
        let p = std::env::temp_dir().join(format!("velo_ws_{name}"));
        let mut f = std::fs::File::create(&p).unwrap();
        f.write_all(content.as_bytes()).unwrap();
        p
    }

    #[test]
    fn open_focus_dedup() {
        let a = tmp("a.rs", "fn a() {}");
        let b = tmp("b.rs", "fn b() {}");
        let mut w = Workspace::default();
        let ia = w.open(&a).unwrap();
        let ib = w.open(&b).unwrap();
        assert_ne!(ia, ib);
        assert_eq!(w.focused(), Some(ib));
        // Re-open = refocus, same id.
        assert_eq!(w.open(&a).unwrap(), ia);
        assert_eq!(w.focused(), Some(ia));
        let _ = std::fs::remove_file(a);
        let _ = std::fs::remove_file(b);
    }

    #[test]
    fn close_saves_dirty_and_refocuses() {
        let a = tmp("c.rs", "old");
        let b = tmp("d.rs", "");
        let mut w = Workspace::default();
        let ia = w.open(&a).unwrap();
        let ib = w.open(&b).unwrap();
        w.focus(ia);
        w.doc_mut().unwrap().insert("new ");
        assert_eq!(w.dirty_ids(), vec![ia]);
        w.close(ia).unwrap();
        assert!(std::fs::read_to_string(&a).unwrap().starts_with("new "));
        assert_eq!(w.focused(), Some(ib));
        w.close(ib).unwrap();
        assert_eq!(w.focused(), None);
        let _ = std::fs::remove_file(a);
        let _ = std::fs::remove_file(b);
    }

    #[test]
    fn frame_none_without_files_and_some_with() {
        let mut w = Workspace::default();
        assert!(w.frame(80, 24).is_none());
        let a = tmp("e.py", "x = 1");
        w.open(&a).unwrap();
        let f = w.frame(80, 24).unwrap();
        assert!(f.cells.iter().any(|c| c.c == 'x'));
        let _ = std::fs::remove_file(a);
    }

    #[test]
    fn state_persists_across_focus_switches() {
        let a = tmp("f.rs", "line\n".repeat(100).as_str());
        let b = tmp("g.rs", "");
        let mut w = Workspace::default();
        let ia = w.open(&a).unwrap();
        let ib = w.open(&b).unwrap();
        w.focus(ia);
        w.doc_mut().unwrap().set_cursor(50, 2);
        let _ = w.frame(80, 10); // view scrolls to cursor
        w.focus(ib);
        let _ = w.frame(80, 10);
        w.focus(ia);
        assert_eq!(w.doc_mut().unwrap().cursor(), (50, 2)); // survived
        let _ = std::fs::remove_file(a);
        let _ = std::fs::remove_file(b);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p editor workspace`
Expected: compile FAIL — `Workspace` not defined.

- [ ] **Step 3: Implement**

`crates/editor/src/workspace.rs`:

```rust
//! Workspace: the set of open files. Engine-owned, so open files, cursors,
//! and unsaved buffers survive C#-side view switches by construction.

use crate::{Document, Highlighter, View};
use std::path::Path;

struct OpenFile {
    id: u32,
    doc: Document,
    view: View,
    hl: Option<Highlighter>,
    /// Canonical path for dedup (falls back to the raw path string).
    canon: String,
}

#[derive(Default)]
pub struct Workspace {
    files: Vec<OpenFile>,
    focused: Option<u32>,
    next_id: u32,
}

impl Workspace {
    pub fn open(&mut self, path: &Path) -> std::io::Result<u32> {
        let canon = std::fs::canonicalize(path)
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_else(|_| path.to_string_lossy().into_owned());
        if let Some(f) = self.files.iter().find(|f| f.canon == canon) {
            let id = f.id;
            self.focused = Some(id);
            return Ok(id);
        }
        let doc = Document::load(path)?;
        let id = self.next_id;
        self.next_id += 1;
        let hl = Highlighter::for_path(doc.path());
        self.files.push(OpenFile {
            id,
            doc,
            view: View::default(),
            hl,
            canon,
        });
        self.focused = Some(id);
        Ok(id)
    }

    pub fn close(&mut self, id: u32) -> std::io::Result<()> {
        let Some(i) = self.files.iter().position(|f| f.id == id) else {
            return Ok(());
        };
        if self.files[i].doc.dirty() {
            self.files[i].doc.save()?;
        }
        self.files.remove(i);
        if self.focused == Some(id) {
            // Neighbor at the same index, else the last file, else none.
            self.focused = self
                .files
                .get(i.min(self.files.len().saturating_sub(1)))
                .filter(|_| !self.files.is_empty())
                .map(|f| f.id);
        }
        Ok(())
    }

    pub fn focus(&mut self, id: u32) {
        if self.files.iter().any(|f| f.id == id) {
            self.focused = Some(id);
        }
    }

    pub fn focused(&self) -> Option<u32> {
        self.focused
    }

    pub fn save_all(&mut self) -> std::io::Result<()> {
        for f in &mut self.files {
            if f.doc.dirty() {
                f.doc.save()?;
            }
        }
        Ok(())
    }

    pub fn dirty_ids(&self) -> Vec<u32> {
        self.files.iter().filter(|f| f.doc.dirty()).map(|f| f.id).collect()
    }

    fn focused_file(&mut self) -> Option<&mut OpenFile> {
        let id = self.focused?;
        self.files.iter_mut().find(|f| f.id == id)
    }

    pub fn doc_mut(&mut self) -> Option<&mut Document> {
        self.focused_file().map(|f| &mut f.doc)
    }

    pub fn frame(&mut self, cols: u16, rows: u16) -> Option<term_core::Frame> {
        let f = self.focused_file()?;
        let empty: &[Vec<crate::Span>] = &[];
        let spans = match &mut f.hl {
            Some(h) => h.lines(&f.doc.text(), f.doc.rev()),
            None => empty,
        };
        Some(f.view.frame(&f.doc, spans, cols, rows))
    }

    pub fn scroll(&mut self, delta: i32) {
        if let Some(f) = self.focused_file() {
            f.view.scroll(delta, &f.doc);
        }
    }

    pub fn hit(&self, col: u16, row: u16) -> Option<(usize, usize)> {
        let id = self.focused?;
        self.files.iter().find(|f| f.id == id).map(|f| f.view.hit(col, row))
    }
}
```

Borrow check note: `frame()` borrows `f.hl` mutably while reading `f.doc` — both are disjoint fields of the same `&mut OpenFile`, which Rust allows; `f.doc.text()` produces an owned `String`, but `h.lines(...)` returns a borrow of `f.hl`'s cache while `f.view.frame(&f.doc, ...)` needs `f.view` mutably — also disjoint. If the compiler objects to the temporary `text` String's lifetime, bind it first: `let text = f.doc.text(); let spans = h.lines(&text, f.doc.rev());`.

- [ ] **Step 4: Run tests**

Run: `cargo test -p editor`
Expected: all pass (26 + 4 new).

- [ ] **Step 5: Commit**

```bash
git add crates/editor/src
git commit -m "feat: editor workspace — multi-file open/close/focus with save-on-close

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: ffi — editor pane, ABI exports, input routing

**Files:**
- Modify: `crates/ffi/Cargo.toml` (add `editor = { workspace = true }`)
- Modify: `crates/ffi/src/lib.rs`:
  - `struct Pane` (~line 168): add `kind` field
  - `render_pane` (~line 514): branch frame source on kind
  - Engine struct: add `workspace: editor::Workspace`, `editor_pane: usize` (`NO_SESSION` sentinel when unattached)
  - `VeloCallbacks`: append `on_editor_dirty`
  - exports section: the `velo_editor_*` family

**Interfaces:**
- Consumes: `editor::Workspace` (Task 5) — `open/close/focus/save_all/dirty_ids/doc_mut/frame/scroll/hit`; `editor::{Move, Document}`.
- Produces (Task 7 mirrors these exactly):
  - `velo_editor_attach(eng, out_swapchain: *mut *mut c_void) -> u32` — creates the editor pane (swapchain + renderer, `PaneKind::Editor`); returns pane id or `u32::MAX`.
  - `velo_editor_resize(eng, w: u32, h: u32)`
  - `velo_editor_open(eng, path_utf16: *const u16, len: usize) -> i64` — file id or -1 (unreadable/non-UTF-8).
  - `velo_editor_close_file(eng, id: u32)` — saves dirty first.
  - `velo_editor_focus_file(eng, id: u32)`
  - `velo_editor_save_all(eng)`
  - `velo_editor_key(eng, vk: u32, mods: u32) -> u8` — 1 = handled (C# marks Handled and swallows the matching char).
  - `velo_editor_char(eng, cu: u32)`
  - `velo_editor_scroll(eng, delta_lines: i32)`
  - `velo_editor_mouse(eng, kind: u32, x: f32, y: f32, mods: u32)` — 0 down / 1 move / 2 up.
  - `VeloCallbacks.on_editor_dirty: Option<extern "C" fn(*mut c_void, u32, u8)>` — (file_id, dirty 0/1); fired on every dirty-state flip.

- [ ] **Step 1: Pane kind + editor pane creation**

Add above `struct Pane`:

```rust
    /// What a pane draws: a terminal session's frames, or the editor
    /// workspace's. Editor panes reuse the whole render path (renderer,
    /// glide, bg/alpha/zoom) — only the frame source differs.
    #[derive(PartialEq, Clone, Copy)]
    enum PaneKind {
        Terminal,
        Editor,
    }
```

Add `kind: PaneKind,` to `struct Pane`; set `kind: PaneKind::Terminal` at every existing `Pane { ... }` construction site (grep `session: NO_SESSION` / the `Pane {` literals — pane 0 creation in `velo_attach` and `velo_pane_new`'s body both construct panes; factor is NOT required, just add the field).

Engine struct gains (near `panes: Vec<Option<Pane>>`):

```rust
        /// Editor workspace: engine-owned so open files survive view switches.
        workspace: editor::Workspace,
        /// Index into `panes` of the editor pane (`NO_SESSION` until attached).
        editor_pane: usize,
```

(init `workspace: editor::Workspace::default(), editor_pane: NO_SESSION,` where the Engine is built.)

`velo_editor_attach` reuses the existing pane-creation code path: find `velo_pane_new`'s implementation (it builds a composition swapchain, wraps backbuffers, pushes `Some(Pane {...})`). Mirror it into an engine method `fn editor_attach(&mut self) -> Option<(usize, *mut c_void)>` that (a) returns the existing editor pane's swapchain if `self.editor_pane != NO_SESSION` (idempotent re-attach), else (b) creates the pane exactly like `velo_pane_new` but with `kind: PaneKind::Editor`, stores its index in `self.editor_pane`, and returns the raw `IDXGISwapChain3` pointer the same way `velo_pane_new` reports it.

- [ ] **Step 2: Frame source branch in `render_pane`**

At the top of `render_pane`, replace the session lookup with a branch:

```rust
        fn render_pane(&mut self, idx: usize) {
            let kind = match self.panes.get(idx) {
                Some(Some(p)) => p.kind,
                _ => return,
            };
            let mut frame = match kind {
                PaneKind::Editor => {
                    let (cols, rows) = match self.panes.get(idx) {
                        Some(Some(p)) => (p.cols, p.rows),
                        _ => return,
                    };
                    match self.workspace.frame(cols, rows) {
                        Some(f) => f,
                        None => term_core::Frame {
                            cols,
                            rows,
                            cells: Vec::new(),
                            cursor_col: 0,
                            cursor_row: 0,
                            cursor_shape: term_core::CursorShape::Hidden,
                            damage: term_core::FrameDamage::Full,
                            scrolled_up: 0,
                        },
                    }
                }
                PaneKind::Terminal => {
                    let sid = match self.panes.get(idx) {
                        Some(Some(p)) => p.session,
                        _ => return,
                    };
                    if sid == NO_SESSION {
                        return;
                    }
                    match self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                        Some(s) => s.terminal.frame(),
                        None => return,
                    }
                }
            };
            // ... existing body unchanged from `let Some(Some(pane)) = ...` on.
```

Everything below (force_full, scroll_bump, draw, Present, on_anim) already works for both kinds.

- [ ] **Step 3: Dirty-notification plumbing**

Append to `VeloCallbacks` (AFTER `on_anim` — append-only ABI):

```rust
        /// (ctx, file_id, dirty) — an editor buffer's dirty state flipped.
        pub on_editor_dirty: Option<extern "C" fn(*mut c_void, u32, u8)>,
```

(`on_editor_dirty: None,` in `Default`.)

Engine gets a helper called after every editing/saving editor operation:

```rust
        /// Diff dirty state against the last notification and emit flips.
        fn notify_editor_dirty(&mut self) {
            let now = self.workspace.dirty_ids();
            if now == self.last_dirty {
                return;
            }
            if let Some(f) = self.cb.on_editor_dirty {
                for id in now.iter().filter(|i| !self.last_dirty.contains(i)) {
                    f(self.cb.ctx, *id, 1);
                }
                for id in self.last_dirty.iter().filter(|i| !now.contains(i)) {
                    f(self.cb.ctx, *id, 0);
                }
            }
            self.last_dirty = now;
        }
```

with `last_dirty: Vec<u32>` on Engine (init empty).

- [ ] **Step 4: Key/char/mouse handling**

Engine methods (VK codes match the constants already used in `key_seq`/`on_key` — 0x25-0x28 arrows, 0x21/0x22 page, 0x23/0x24 end/home, 0x08 backspace, 0x2E delete, 0x0D enter, 0x09 tab; mods bit0=Ctrl bit1=Shift bit2=Alt):

```rust
        /// Editor key handling. Returns true when handled (C# swallows the
        /// matching WM_CHAR). Printable chars arrive via `editor_char`.
        fn editor_key(&mut self, vk: u16, mods: u32) -> bool {
            use editor::Move;
            let ctrl = mods & 1 != 0;
            let shift = mods & 2 != 0;
            let rows = match self.panes.get(self.editor_pane) {
                Some(Some(p)) => p.rows as usize,
                _ => 24,
            };
            let Some(doc) = self.workspace.doc_mut() else {
                return false;
            };
            let handled = match (vk, ctrl) {
                (0x25, false) => { doc.move_cursor(Move::Left, shift); true }
                (0x27, false) => { doc.move_cursor(Move::Right, shift); true }
                (0x25, true) => { doc.move_cursor(Move::WordLeft, shift); true }
                (0x27, true) => { doc.move_cursor(Move::WordRight, shift); true }
                (0x26, _) => { doc.move_cursor(Move::Up, shift); true }
                (0x28, _) => { doc.move_cursor(Move::Down, shift); true }
                (0x24, false) => { doc.move_cursor(Move::Home, shift); true }
                (0x23, false) => { doc.move_cursor(Move::End, shift); true }
                (0x24, true) => { doc.move_cursor(Move::DocStart, shift); true }
                (0x23, true) => { doc.move_cursor(Move::DocEnd, shift); true }
                (0x21, _) => { doc.move_cursor(Move::PageUp(rows), shift); true }
                (0x22, _) => { doc.move_cursor(Move::PageDown(rows), shift); true }
                (0x08, _) => { doc.backspace(); true }
                (0x2E, _) => { doc.delete(); true }
                (0x0D, _) => { doc.insert("\n"); true }
                (0x09, _) => { doc.insert("\t"); true }
                (0x5A, true) if shift => { doc.redo(); true }  // Ctrl+Shift+Z
                (0x5A, true) => { doc.undo(); true }           // Ctrl+Z
                (0x59, true) => { doc.redo(); true }           // Ctrl+Y
                (0x53, true) => {                              // Ctrl+S
                    let _ = doc.save();
                    true
                }
                (0x43, true) => {                              // Ctrl+C
                    if let Some(text) = doc.selection_text() {
                        Self::set_clipboard(&text);
                    }
                    true
                }
                (0x58, true) => {                              // Ctrl+X
                    if let Some(text) = doc.selection_text() {
                        Self::set_clipboard(&text);
                        doc.backspace(); // deletes the selection
                    }
                    true
                }
                (0x41, true) => {                              // Ctrl+A
                    doc.move_cursor(Move::DocStart, false);
                    doc.move_cursor(Move::DocEnd, true);
                    true
                }
                _ => false,
            };
            if handled {
                self.notify_editor_dirty();
                let ep = self.editor_pane;
                self.render_pane(ep);
            }
            handled
        }

        fn editor_char(&mut self, cu: u32) {
            // Skip control chars (Enter/Tab/Backspace arrive via editor_key;
            // Ctrl+letter WM_CHARs must not insert control bytes).
            let Some(c) = char::from_u32(cu) else { return };
            if c.is_control() {
                return;
            }
            if let Some(doc) = self.workspace.doc_mut() {
                doc.insert(&c.to_string());
                self.notify_editor_dirty();
                let ep = self.editor_pane;
                self.render_pane(ep);
            }
        }
```

Ctrl+V (paste): the existing `velo_paste_utf16` path is terminal-directed — add the editor equivalent inside `editor_key`'s match: `(0x56, true) => { if let Some(text) = Self::get_clipboard() { doc.insert(&text); } true }`. For `set_clipboard`/`get_clipboard`: the terminal's Ctrl+Shift+C/V handlers in `on_key` already talk to the Windows clipboard — extract those bodies into `fn set_clipboard(text: &str)` / `fn get_clipboard() -> Option<String>` associated functions and call them from both places (read the existing `on_key` implementation around the 0x43/0x56 arms first; reuse, don't duplicate).

Mouse (viewport px → cell, same math as `pixel_to_cell` but no session):

```rust
        fn editor_mouse(&mut self, kind: u32, x: f32, y: f32) {
            let (cols, rows) = match self.panes.get(self.editor_pane) {
                Some(Some(p)) => (p.cols, p.rows),
                _ => return,
            };
            let pad = (PAD_LOGICAL_PX * self.dpi_scale).round();
            let col = (((x - pad) / self.font.cell_w).max(0.0) as u16).min(cols.saturating_sub(1));
            let row = (((y - pad) / self.font.cell_h).max(0.0) as u16).min(rows.saturating_sub(1));
            let Some((line, dcol)) = self.workspace.hit(col, row) else { return };
            if let Some(doc) = self.workspace.doc_mut() {
                match kind {
                    0 => doc.set_cursor(line, dcol),
                    1 => doc.select_to(line, dcol),
                    _ => {}
                }
            }
            let ep = self.editor_pane;
            self.render_pane(ep);
        }
```

(C# only sends kind=1 while the left button is down — the same latching it already does for terminal panes.)

- [ ] **Step 5: Exports**

Next to the `velo_pane_*` exports, all with the standard `# Safety` doc (`eng` must be a live handle from `velo_attach`):

```rust
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_attach(eng: *mut Engine, out_swapchain: *mut *mut c_void) -> u32 {
        let Some(e) = eng.as_mut() else { return u32::MAX };
        match e.editor_attach() {
            Some((idx, sc)) => {
                if !out_swapchain.is_null() {
                    *out_swapchain = sc;
                }
                idx as u32
            }
            None => u32::MAX,
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_resize(eng: *mut Engine, w: u32, h: u32) {
        if let Some(e) = eng.as_mut() {
            let ep = e.editor_pane;
            e.resize_pane(ep, w, h); // the existing pane-resize path (swapchain + grid recompute)
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_open(eng: *mut Engine, path: *const u16, len: usize) -> i64 {
        let Some(e) = eng.as_mut() else { return -1 };
        let s = String::from_utf16_lossy(std::slice::from_raw_parts(path, len));
        match e.workspace.open(std::path::Path::new(&s)) {
            Ok(id) => {
                let ep = e.editor_pane;
                e.render_pane(ep);
                id as i64
            }
            Err(_) => -1,
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_close_file(eng: *mut Engine, id: u32) {
        if let Some(e) = eng.as_mut() {
            let _ = e.workspace.close(id);
            e.notify_editor_dirty();
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_focus_file(eng: *mut Engine, id: u32) {
        if let Some(e) = eng.as_mut() {
            e.workspace.focus(id);
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_save_all(eng: *mut Engine) {
        if let Some(e) = eng.as_mut() {
            let _ = e.workspace.save_all();
            e.notify_editor_dirty();
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_key(eng: *mut Engine, vk: u32, mods: u32) -> u8 {
        match eng.as_mut() {
            Some(e) => e.editor_key(vk as u16, mods) as u8,
            None => 0,
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_char(eng: *mut Engine, cu: u32) {
        if let Some(e) = eng.as_mut() {
            e.editor_char(cu);
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_scroll(eng: *mut Engine, delta_lines: i32) {
        if let Some(e) = eng.as_mut() {
            e.workspace.scroll(delta_lines);
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_mouse(eng: *mut Engine, kind: u32, x: f32, y: f32, _mods: u32) {
        if let Some(e) = eng.as_mut() {
            e.editor_mouse(kind, x, y);
        }
    }
```

`resize_pane` above refers to whatever the existing `velo_pane_resize` calls internally — read it and reuse that method; if the logic is inline in the export, extract it into `fn resize_pane(&mut self, idx: usize, w: u32, h: u32)` first and call it from both exports. Guard every editor export against `editor_pane == NO_SESSION` (the `panes.get` calls already return `None` for `NO_SESSION` because it's out of bounds — verify that assumption holds: `NO_SESSION` must be `usize::MAX`, not a small sentinel).

Also: exclude the editor pane from terminal-only paths — check `velo_pane_close` (must refuse to close the editor pane), `pane_scroll`, `velo_pane_bind` (must refuse binding a session onto it: `if pane.kind == PaneKind::Editor { return; }`), and `set_focused_pane`.

- [ ] **Step 6: Verify**

Run: `cargo check --target x86_64-pc-windows-gnu -p velo_core && cargo test -p editor -p term-core -p renderer -p text`
Expected: clean check; all Linux tests pass.

- [ ] **Step 7: Commit**

```bash
git add crates/ffi Cargo.toml Cargo.lock
git commit -m "feat: editor pane in ffi — velo_editor_* ABI, input routing, dirty callback

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: C# — editor mode UI

**Files:**
- Create: `csharp/Velo.App/EditorFileVM.cs`
- Modify: `csharp/Velo.App/Interop.cs` (mirror all Task 6 exports + callback field)
- Modify: `csharp/Velo.App/MainWindow.xaml` (EditorHost inside ContentRoot)
- Modify: `csharp/Velo.App/MainWindow.xaml.cs` (mode toggle, file open, tab strip handlers, autosave, callbacks)

**Interfaces:**
- Consumes: every `velo_editor_*` export and `OnEditorDirty` (Task 6).
- Produces: user-facing editor mode. End of the chain.

- [ ] **Step 1: Interop mirror**

`Interop.cs` — append to `VeloCallbacks` (after `OnAnim`):

```csharp
        public IntPtr OnEditorDirty;      // (ctx, file_id, dirty)
```

and the imports:

```csharp
    // ---- Editor pane -------------------------------------------------------

    /// Create (or return) the editor pane; writes its swapchain to *outSwapchain.
    /// Returns the pane id, or uint.MaxValue on failure.
    [LibraryImport(Core)]
    internal static partial uint velo_editor_attach(IntPtr eng, IntPtr* outSwapchain);

    [LibraryImport(Core)]
    internal static partial void velo_editor_resize(IntPtr eng, uint w, uint h);

    /// Open (or refocus) a file; returns its id, or -1 (unreadable / not UTF-8).
    [LibraryImport(Core)]
    internal static partial long velo_editor_open(IntPtr eng, ushort* path, nuint len);

    /// Close a file (saves it first if dirty).
    [LibraryImport(Core)]
    internal static partial void velo_editor_close_file(IntPtr eng, uint id);

    [LibraryImport(Core)]
    internal static partial void velo_editor_focus_file(IntPtr eng, uint id);

    [LibraryImport(Core)]
    internal static partial void velo_editor_save_all(IntPtr eng);

    /// Editor key-down. mods bit0=Ctrl, bit1=Shift, bit2=Alt. 1 = handled.
    [LibraryImport(Core)]
    internal static partial byte velo_editor_key(IntPtr eng, uint vk, uint mods);

    [LibraryImport(Core)]
    internal static partial void velo_editor_char(IntPtr eng, uint cu);

    [LibraryImport(Core)]
    internal static partial void velo_editor_scroll(IntPtr eng, int deltaLines);

    /// kind 0=down,1=move,2=up; (x,y) physical px inside the editor panel.
    [LibraryImport(Core)]
    internal static partial void velo_editor_mouse(IntPtr eng, uint kind, float x, float y, uint mods);
```

- [ ] **Step 2: View model**

`csharp/Velo.App/EditorFileVM.cs`:

```csharp
using System.ComponentModel;
using System.IO;

namespace Velo.App;

/// <summary>One open editor file: drives the editor tab strip.</summary>
public sealed class EditorFileVM : INotifyPropertyChanged
{
    public uint Id { get; init; }
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);

    private bool _dirty;
    public bool Dirty
    {
        get => _dirty;
        set
        {
            if (_dirty == value) return;
            _dirty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Dirty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirtyVisibility)));
        }
    }

    public Microsoft.UI.Xaml.Visibility DirtyVisibility =>
        _dirty ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- [ ] **Step 3: XAML — EditorHost**

In `MainWindow.xaml`, inside `ContentRoot` `Grid.Row="1"` as a SIBLING after the `PaneHost` grid (same row, so visibility swap toggles which fills the body):

```xml
            <!-- Editor mode host: file tab strip + editor swapchain. Shown in
                 place of PaneHost while editor mode is on; terminal panes stay
                 alive underneath (hidden, not destroyed). -->
            <Grid x:Name="EditorHost"
                  Grid.Row="1"
                  Visibility="Collapsed"
                  Background="Transparent">
                <Grid.RowDefinitions>
                    <RowDefinition Height="34" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>

                <ListView x:Name="EditorTabs"
                          Grid.Row="0"
                          ItemsSource="{x:Bind EditorFiles}"
                          SelectionMode="Single"
                          SelectionChanged="EditorTabs_SelectionChanged"
                          ScrollViewer.HorizontalScrollMode="Enabled"
                          ScrollViewer.HorizontalScrollBarVisibility="Hidden">
                    <ListView.ItemsPanel>
                        <ItemsPanelTemplate>
                            <ItemsStackPanel Orientation="Horizontal" />
                        </ItemsPanelTemplate>
                    </ListView.ItemsPanel>
                    <ListView.ItemTemplate>
                        <DataTemplate x:DataType="local:EditorFileVM">
                            <Grid ColumnSpacing="6" Padding="10,0,4,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0"
                                           Text="{x:Bind Name}"
                                           VerticalAlignment="Center"
                                           FontSize="12" />
                                <Ellipse Grid.Column="1"
                                         Width="7" Height="7"
                                         Fill="{ThemeResource AccentFillColorDefaultBrush}"
                                         Visibility="{x:Bind DirtyVisibility, Mode=OneWay}"
                                         VerticalAlignment="Center" />
                                <Button Grid.Column="2"
                                        Content="&#xE711;"
                                        FontFamily="Segoe MDL2 Assets"
                                        FontSize="10"
                                        Width="22" Height="22"
                                        Padding="0"
                                        Background="Transparent"
                                        BorderThickness="0"
                                        Tag="{x:Bind Id}"
                                        Click="EditorTabClose_Click" />
                            </Grid>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>

                <SwapChainPanel x:Name="EditorPanel"
                                Grid.Row="1"
                                IsTabStop="True"
                                SizeChanged="EditorPanel_SizeChanged"
                                KeyDown="EditorPanel_KeyDown"
                                CharacterReceived="EditorPanel_CharacterReceived"
                                PointerPressed="EditorPanel_PointerPressed"
                                PointerMoved="EditorPanel_PointerMoved"
                                PointerReleased="EditorPanel_PointerReleased"
                                PointerWheelChanged="EditorPanel_PointerWheelChanged" />
            </Grid>
```

- [ ] **Step 4: Code-behind**

Add to `MainWindow.xaml.cs` (a new `// ---- Editor mode ----` region). Read the existing `TerminalPanel_Loaded` / `Panel_KeyDown` / `PaneHost_Pointer*` handlers first and reuse their idioms (modifier reading, physical-px conversion via `XamlRoot.RasterizationScale`, `ISwapChainPanelNative` binding):

```csharp
    // ---- Editor mode -------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<EditorFileVM> EditorFiles { get; } = new();
    private bool _editorMode;
    private bool _editorAttached;
    private bool _editorMouseDown;
    private DispatcherTimer? _autosave;

    /// Files button toggles editor mode; file clicks in the tree open here.
    private void SetEditorMode(bool on)
    {
        if (_editorMode == on) return;
        _editorMode = on;
        if (on && !_editorAttached)
            AttachEditorPane();
        if (!on)
            Native.velo_editor_save_all(_engine);   // flush on exit (spec)
        PaneHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EditorHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) EditorPanel.Focus(FocusState.Programmatic);
        else FocusTerminal();
    }

    private unsafe void AttachEditorPane()
    {
        IntPtr sc = IntPtr.Zero;
        uint id = Native.velo_editor_attach(_engine, &sc);
        if (id == uint.MaxValue || sc == IntPtr.Zero) return;
        ((ISwapChainPanelNative)((IWinRTObject)EditorPanel).NativeObject.AsInterface<ISwapChainPanelNative>()).SetSwapChain(sc);
        _editorAttached = true;
        EditorPanel_SizeChanged(EditorPanel, null!);
    }

    private void EditorPanel_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        if (!_editorAttached) return;
        double scale = EditorPanel.XamlRoot?.RasterizationScale ?? 1.0;
        uint w = (uint)Math.Max(1, EditorPanel.ActualWidth * scale);
        uint h = (uint)Math.Max(1, EditorPanel.ActualHeight * scale);
        Native.velo_editor_resize(_engine, w, h);
    }

    /// Open (or refocus) a file in the editor; enters editor mode.
    private unsafe void OpenInEditor(string path)
    {
        SetEditorMode(true);
        long id;
        fixed (char* p = path)
            id = Native.velo_editor_open(_engine, (ushort*)p, (nuint)path.Length);
        if (id < 0) { ShellOpen(path); return; } // binary/unreadable: OS fallback
        var fid = (uint)id;
        var vm = EditorFiles.FirstOrDefault(f => f.Id == fid);
        if (vm is null)
        {
            vm = new EditorFileVM { Id = fid, Path = path };
            EditorFiles.Add(vm);
        }
        EditorTabs.SelectedItem = vm;
        EditorPanel.Focus(FocusState.Programmatic);
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorTabs.SelectedItem is EditorFileVM vm)
        {
            Native.velo_editor_save_all(_engine); // flush on tab switch (spec)
            Native.velo_editor_focus_file(_engine, vm.Id);
            EditorPanel.Focus(FocusState.Programmatic);
        }
    }

    private void EditorTabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not uint id) return;
        Native.velo_editor_close_file(_engine, id);
        var vm = EditorFiles.FirstOrDefault(f => f.Id == id);
        if (vm is not null) EditorFiles.Remove(vm);
        if (EditorFiles.Count == 0) SetEditorMode(false);
        else if (EditorTabs.SelectedItem is null) EditorTabs.SelectedIndex = EditorFiles.Count - 1;
    }

    private void EditorPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        uint mods = ReadMods(); // reuse the exact helper Panel_KeyDown uses for ctrl/shift/alt bits
        if (Native.velo_editor_key(_engine, (uint)e.Key, mods) != 0)
            e.Handled = true;
    }

    private void EditorPanel_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        Native.velo_editor_char(_engine, args.Character);
        args.Handled = true;
    }

    private void EditorPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(EditorPanel).Properties.MouseWheelDelta;
        Native.velo_editor_scroll(_engine, -delta * 3 / 120);
        e.Handled = true;
    }

    private void EditorPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var (x, y) = EditorPhysicalPoint(e);
        _editorMouseDown = true;
        EditorPanel.CapturePointer(e.Pointer);
        Native.velo_editor_mouse(_engine, 0, x, y, ReadMods());
        EditorPanel.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void EditorPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_editorMouseDown) return;
        var (x, y) = EditorPhysicalPoint(e);
        Native.velo_editor_mouse(_engine, 1, x, y, ReadMods());
    }

    private void EditorPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _editorMouseDown = false;
        EditorPanel.ReleasePointerCaptures();
        var (x, y) = EditorPhysicalPoint(e);
        Native.velo_editor_mouse(_engine, 2, x, y, ReadMods());
    }

    private (float, float) EditorPhysicalPoint(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(EditorPanel).Position;
        double scale = EditorPanel.XamlRoot?.RasterizationScale ?? 1.0;
        return ((float)(pt.X * scale), (float)(pt.Y * scale));
    }

    /// Dirty flip from Rust: update the tab dot + restart the 1s autosave timer.
    private void OnEditorDirtyUi(uint fileId, bool dirty)
    {
        var vm = EditorFiles.FirstOrDefault(f => f.Id == fileId);
        if (vm is not null) vm.Dirty = dirty;
        if (!dirty) return;
        if (_autosave is null)
        {
            _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autosave.Tick += (_, _) =>
            {
                _autosave!.Stop();
                Native.velo_editor_save_all(_engine); // Rust fires dirty=false back
            };
        }
        _autosave.Stop();
        _autosave.Start();
    }

    [UnmanagedCallersOnly]
    private static void OnEditorDirty(IntPtr ctx, uint fileId, byte dirty)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.OnEditorDirtyUi(fileId, dirty != 0));
    }
```

Adaptation notes for the implementer (verify against the real file, do not guess):
- `ReadMods()` — the terminal key handler builds the ctrl/shift/alt bitmask inline or via a helper; use the identical code (extract a helper if it's inline).
- Swapchain binding — copy the EXACT binding incantation from `TerminalPanel_Loaded`/`velo_pane_new` usage (the `ISwapChainPanelNative` cast in this codebase may differ from the sketch above).
- `FocusTerminal()` already exists (grep it).
- `BuildCallbacks()` gains: `OnEditorDirty = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte, void>)&OnEditorDirty,`

- [ ] **Step 5: Wire the Files button + file clicks**

- `FilesTabButton`'s existing click handler (grep `FilesTabButton` — the details-tab selector): after selecting the Files details tab, also call `SetEditorMode(true)`. Clicking it while ALREADY in editor mode calls `SetEditorMode(false)` (toggle).
- `FilesTree_ItemInvoked` (MainWindow.xaml.cs:631): add a file branch —

```csharp
        if (fi is { IsDir: false } && _editorMode)
            OpenInEditor(fi.Path);
```

- `FilesList_DoubleTapped` (line ~668): in editor mode, files route to `OpenInEditor(fi.Path)` instead of `ShellOpen`.
- Terminal tab selection (the handler that binds a session on tab click — grep `SelectById`): call `SetEditorMode(false)` first so clicking a terminal tab returns to the terminal view (workspace state survives in the engine).

- [ ] **Step 6: Verify + commit**

Run: `cargo check --target x86_64-pc-windows-gnu -p velo_core && cargo test -p editor -p term-core -p renderer -p text`
C# builds only on Windows. Windows manual verify (user): Files button → editor mode; click .rs/.py files → tabs appear, highlighted, themed; type → dirty dot → auto-saves after 1 s; Ctrl+Z/Y, Ctrl+C/V/X, click/drag select; wheel glides; switch to a terminal tab and back → files/cursors intact; zoom + opacity apply to the editor; non-UTF-8 file falls back to the OS opener.

```bash
git add csharp/Velo.App
git commit -m "feat: editor mode UI — file tabs, input routing, autosave

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
