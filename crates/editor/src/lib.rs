//! editor: in-terminal text editor core. Documents (rope buffers with
//! cursor/selection/undo), tree-sitter highlighting, and a projector that
//! turns the active document into a `term_core::Frame` so the existing
//! terminal renderer draws it (theme/opacity/zoom/smooth-scroll for free).

mod buffer;
pub use buffer::{Document, Move};
