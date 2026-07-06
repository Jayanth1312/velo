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
        self.files
            .iter()
            .filter(|f| f.doc.dirty())
            .map(|f| f.id)
            .collect()
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
        Some(match &mut f.hl {
            Some(h) => {
                let text = f.doc.text();
                let spans = h.lines(&text, f.doc.rev());
                f.view.frame(&f.doc, spans, cols, rows)
            }
            None => f.view.frame(&f.doc, &[], cols, rows),
        })
    }

    pub fn scroll(&mut self, delta: i32) {
        if let Some(f) = self.focused_file() {
            f.view.scroll(delta, &f.doc);
        }
    }

    pub fn hit(&self, col: u16, row: u16) -> Option<(usize, usize)> {
        let id = self.focused?;
        self.files
            .iter()
            .find(|f| f.id == id)
            .map(|f| f.view.hit(col, row))
    }
}

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
        let a = tmp("f.rs", &"line\n".repeat(100));
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
