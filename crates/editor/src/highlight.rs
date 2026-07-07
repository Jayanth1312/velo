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
    /// (Document::rev, palette::epoch) the cache was built for — theme swaps
    /// must re-resolve span colors even when the text is unchanged.
    cached_key: Option<(u64, u64)>,
    cache: Vec<Vec<Span>>,
}

impl Highlighter {
    pub fn for_path(path: &str) -> Option<Self> {
        let name = path.rsplit(['/', '\\']).next()?;
        let ext = name.rsplit('.').next()?;
        if ext == name {
            return None; // no extension (e.g. Makefile)
        }
        let ext = ext.to_ascii_lowercase();
        // (language, highlights query) per supported extension.
        let (lang, query): (tree_sitter::Language, &str) = match ext.as_str() {
            "rs" => (
                tree_sitter_rust::LANGUAGE.into(),
                tree_sitter_rust::HIGHLIGHTS_QUERY,
            ),
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
            "go" => (
                tree_sitter_go::LANGUAGE.into(),
                tree_sitter_go::HIGHLIGHTS_QUERY,
            ),
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
            "css" => (
                tree_sitter_css::LANGUAGE.into(),
                tree_sitter_css::HIGHLIGHTS_QUERY,
            ),
            _ => return None,
        };
        let names: Vec<&str> = CAPTURES.iter().map(|(n, _)| *n).collect();
        let mut config = HighlightConfiguration::new(lang, &ext, query, "", "").ok()?;
        config.configure(&names);
        Some(Self {
            config,
            ts: TsHighlighter::new(),
            cached_key: None,
            cache: Vec::new(),
        })
    }

    /// Per-line spans for `text`; recomputed only when `rev` changes.
    pub fn lines(&mut self, text: &str, rev: u64) -> &[Vec<Span>] {
        let key = (rev, palette::epoch());
        if self.cached_key == Some(key) {
            return &self.cache;
        }
        self.cached_key = Some(key);
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

        let Ok(events) = self.ts.highlight(&self.config, text.as_bytes(), None, |_| None)
        else {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rust_keywords_get_colored() {
        let mut h = Highlighter::for_path("main.rs").expect("rust supported");
        let spans = h.lines("fn main() {}\n", 1);
        // "fn" on line 0 must be inside some span (keyword capture).
        let hit = spans[0].iter().any(|s| s.start == 0 && s.end >= 2);
        assert!(
            hit,
            "spans: {:?}",
            spans[0].iter().map(|s| (s.start, s.end)).collect::<Vec<_>>()
        );
    }

    #[test]
    fn cache_by_rev() {
        let mut h = Highlighter::for_path("a.py").unwrap();
        let p1 = h.lines("def f():\n    pass\n", 7).as_ptr();
        let p2 = h.lines("IGNORED — same rev means cache hit", 7).as_ptr();
        assert_eq!(p1, p2);
    }

    #[test]
    fn theme_change_invalidates_span_cache() {
        let mut h = Highlighter::for_path("a.rs").unwrap();
        let before = h.lines("fn main() {}\n", 1)[0].clone();
        let mut ansi = palette::BASE16;
        ansi[5] = [1, 2, 3]; // keyword slot
        palette::set_theme(ansi, [9, 9, 9], [9, 9, 9], [9, 9, 9]);
        let after = h.lines("fn main() {}\n", 1)[0].clone();
        // Restore defaults so parallel tests see sane colors.
        palette::set_theme(
            palette::BASE16,
            palette::DEFAULT_FG,
            palette::DEFAULT_FG,
            palette::SELECTION_BG,
        );
        assert_ne!(before, after, "same rev must re-resolve colors after set_theme");
    }

    #[test]
    fn unknown_extension_is_none() {
        assert!(Highlighter::for_path("notes.txt").is_none());
        assert!(Highlighter::for_path("Makefile").is_none());
    }

    #[test]
    fn all_advertised_languages_construct() {
        for f in [
            "a.rs", "a.js", "a.jsx", "a.ts", "a.tsx", "a.go", "a.py", "a.java",
            "a.html", "a.css",
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
