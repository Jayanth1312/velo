# Velo — Smooth scrolling, built-in editor, plugin/LSP system

Approved design (brainstormed 2026-07-06). Three subsystems, built in order.
Each phase gets its own implementation plan; this spec is the shared contract.

## Phase 0: Command-palette opacity fix (small, first)

Bug: command palette uses its own acrylic/blur brush not bound to the app
opacity setting — at opacity 100 the palette stays translucent.
Fix: bind the palette background to the same opacity/material source as the
app root so it follows the user's opacity setting exactly.

## Phase 1: Smooth GPU scrolling

Goal: streaming TUI output (Claude Code etc.) and wheel scrollback glide
pixel-smooth (kitty/neovide style) instead of jumping whole rows.

- **Renderer** (`crates/renderer`): fractional pixel scroll offset — vertex
  shader y-offset uniform. Render one extra row above and below the viewport
  so edges don't gap during the glide.
- **term-core**: `frame()` reports rows shifted since the last frame (new
  output scrolling content up, or wheel scrollback delta).
- **Engine** (`crates/ffi`): per-pane animation state. A shift of N rows sets
  offset to `N * cell_h`, eased to 0 over ~120 ms (exponential decay). Wheel
  scrollback drives the same path.
- **C#**: `CompositionTarget.Rendering` hook active only while an animation
  is in flight → `velo_pane_tick(pane, dt_ms)` → render. No idle timer.

## Phase 2: Editor mode (Rust GPU editor pane)

Goal: click the Files button in the left sidebar → body swaps from terminal
panes to a global editor workspace. VS Code-style multi-file tabs. Terminal
sessions keep running underneath (hidden, not destroyed).

- **New crate `crates/editor`**: ropey rope buffer; cursor/selection/undo;
  tree-sitter highlighting. v1 grammars: rust, javascript, typescript, go,
  python, java, html, css.
- **Rendering**: through the existing cell-grid wgpu renderer (monospace
  grid + line-number gutter + squiggle underline overlay). Inherits terminal
  theme tokens and background opacity automatically — highlight scopes map
  onto the existing editor-theme palette. No new render or theme stack.
- **Pane model**: pane becomes Terminal | Editor. One global editor
  workspace, not per terminal tab. Keyboard/char input reuses
  `velo_key`/`velo_char` routing.
- **File tabs**: strip at top of body — open on click in Files tree, close
  per-file, dirty dot per file.
- **Persistence (in-session)**: open files, cursor positions, unsaved
  buffers survive switching to terminal tabs and back. Engine owns editor
  state; C# only shows/hides the surface. Cross-restart restore = later
  (Plan 2 session restore).
- **Auto-save**: debounce 1 s after last edit + flush on tab switch/close.
  Dirty dot until saved. External-change conflict: v1 last-write-wins.
- **v1 feature freeze**: edit, undo/redo, find, select/copy/paste, line
  numbers, highlighting, LSP (Phase 3). NO split view, multi-cursor, or
  word-wrap in v1.

## Phase 3: Plugins + LSP

- **Runtime**: Lua via mlua in the Rust core. Plugin = folder in
  `%APPDATA%\velo\plugins\<name>\` with `manifest.toml` + `init.lua`.
  Manifest may declare plugin dependencies (installing a meta-plugin pulls
  its deps).
- **API surface**: `velo.register_lsp{...}`, `velo.command()`,
  `velo.on(event, fn)`, `velo.notify()`, `velo.spawn()`.
- **UI**: plugin button at the bottom of the right sidebar (mirrors the
  settings button placement) → Plugins panel: installed list + registry
  (JSON index hosted on GitHub) + install/uninstall.
- **LSP client in Rust**: `lsp-types` + JSON-RPC over stdio; one server per
  language per workspace root. v1 features: diagnostics, completion, hover,
  go-to-definition, rename.
- **LSP servers ship as plugins**: rust-analyzer (GitHub release),
  typescript-language-server (npm), gopls (go install), python-lsp-server
  (pip), jdtls (download), vscode html/css servers (npm). Opening a file
  with no LSP for its language → infobar offering one-click install.
- **"Editor Pack" meta-plugin**: editor core stays built-in (hooks renderer
  and panes too deeply for Lua); the meta-plugin enables editor mode and
  pulls the default LSP set via dependencies.
- **Registry seed ideas** (later): format-on-save, git gutter/blame,
  markdown preview, themes, snippets, TODO highlighter, CSS color swatches,
  SSH host manager.

## Verification (all phases)

- `cargo test` workspace on Linux; `cargo check --target x86_64-pc-windows-gnu`.
- Windows manual verify by user: palette opacity at 100, smooth glide during
  `claude` streaming + wheel scrollback, editor open/edit/save/theme/opacity,
  LSP diagnostics+completion in a rust/ts file.
