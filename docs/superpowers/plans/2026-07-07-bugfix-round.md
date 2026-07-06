# Bug-fix Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix zoom glide bounce, wheel-scroll stutter, verify wide-char/emoji handling, blend editor tab strip with theme, live-recolor open editor files on theme change, add Ctrl+W tab close.

**Architecture:** Rust engine (crates/renderer, term-core, editor, ffi) renders into WinUI 3 SwapChainPanels; C# (csharp/Velo.App) is chrome + input routing. Fixes are small surgical changes in each layer.

**Tech Stack:** Rust (wgpu, alacritty_terminal, tree-sitter), C# WinUI 3.

## Global Constraints

- `cargo test` (workspace) must pass on Linux; `cargo check --target x86_64-pc-windows-gnu` must pass (ffi is Windows-only code).
- C# cannot be built in WSL — user builds/verifies on Windows. Keep C# diffs minimal and obviously correct.
- Spec: `docs/superpowers/specs/2026-07-07-bugfix-round-design.md`.

---

### Task 1: Wheel-glide clamp + tau (A2 stutter)

**Files:**
- Modify: `crates/renderer/src/lib.rs` (ScrollAnim ~lines 68-99, `scroll_bump` ~line 391, tests module ~line 930)

**Interfaces:**
- Produces: `ScrollAnim::bump(&mut self, rows_up: i32, cell_h: f32, max_px: f32)` (new `max_px` param); `Renderer::scroll_bump(rows_up)` unchanged externally.

- [ ] **Step 1: Update existing tests + add failing test for tall clamp**

In `mod scroll_anim_tests`, update existing `bump(...)` calls to pass a large `max_px` (e.g. `600.0`), and add:

```rust
#[test]
fn bump_clamp_is_caller_supplied() {
    let mut a = ScrollAnim::default();
    a.bump(10, 20.0, 240.0);       // 12-cell cap: 10 rows * 20px fits
    assert_eq!(a.px, 200.0);
    a.bump(10, 20.0, 240.0);       // accumulates but clamps at 240
    assert_eq!(a.px, 240.0);
}
```

- [ ] **Step 2: Run** `cargo test -p renderer scroll_anim` — expect compile FAIL (bump takes 2 args).

- [ ] **Step 3: Implement**

```rust
/// Never displace more than this many cells — a `cat` flood should read as a
/// fast glide, not launch the grid off-screen.
const MAX_SCROLL_CELLS: f32 = 12.0;
/// Exponential decay time constant (ms); ~4*tau ≈ 260ms to visually settle.
const SCROLL_TAU_MS: f32 = 65.0;

impl ScrollAnim {
    /// Content moved `rows_up` rows up (negative = down) on a grid with
    /// `cell_h`-px rows: displace so it starts where it was and eases home.
    /// `max_px` bounds the displacement (viewport-aware, caller-supplied).
    pub fn bump(&mut self, rows_up: i32, cell_h: f32, max_px: f32) {
        self.px = (self.px + rows_up as f32 * cell_h).clamp(-max_px, max_px);
    }
}
```

And in `Renderer::scroll_bump`:

```rust
pub fn scroll_bump(&mut self, rows_up: i32) {
    // ponytail: feel-tuned on Windows; viewport-height cap keeps short panes sane.
    let max_px = self.cell_h * (self.grid_rows as f32).min(MAX_SCROLL_CELLS).max(1.0);
    self.scroll.bump(rows_up, self.cell_h, max_px);
}
```

- [ ] **Step 4: Run** `cargo test -p renderer` — expect PASS.
- [ ] **Step 5: Commit** `fix: wheel glide accumulates across notches (viewport-aware clamp, tau 65ms)`

---

### Task 2: Glide mute after resize/zoom (A1 bounce)

**Files:**
- Modify: `crates/ffi/src/lib.rs` (Pane struct ~line 170-200, `add_pane` pane init ~line 790, `render_pane` ~line 582, `rebuild_font` ~line 703, `resize_pane` ~line 669)

**Interfaces:**
- Produces: `Pane.glide_mute_until: std::time::Instant` field.

- [ ] **Step 1: Add field to `Pane` struct** (next to `force_full`):

```rust
/// Glides are suppressed until this instant. Resize/zoom triggers an async
/// ConPTY repaint whose history churn would otherwise read as content
/// motion and fire a bogus bounce glide.
glide_mute_until: std::time::Instant,
```

Init in `add_pane`'s `Pane { ... }` literal: `glide_mute_until: std::time::Instant::now(),`

- [ ] **Step 2: Stamp the mute.** In `resize_pane` right after `p.apply_size(...)`:

```rust
p.glide_mute_until = std::time::Instant::now() + GLIDE_MUTE_AFTER_RESIZE;
```

In `rebuild_font`, inside the per-pane loop after `p.recompute_grid(...)`:

```rust
p.glide_mute_until = std::time::Instant::now() + GLIDE_MUTE_AFTER_RESIZE;
```

Constant near the other top-of-file consts:

```rust
/// How long after a resize/zoom to suppress scroll glides (ConPTY repaints
/// arrive asynchronously, several frames later).
const GLIDE_MUTE_AFTER_RESIZE: std::time::Duration = std::time::Duration::from_millis(300);
```

- [ ] **Step 3: Honor it in `render_pane`:**

```rust
if frame.scrolled_up != 0 && std::time::Instant::now() >= pane.glide_mute_until {
    pane.renderer.scroll_bump(frame.scrolled_up);
}
```

- [ ] **Step 4: Verify** `cargo check --target x86_64-pc-windows-gnu -p velo-ffi` (use actual ffi package name from `crates/ffi/Cargo.toml`) — expect clean. (ffi has no Linux tests; logic is three lines, covered by Windows manual verify.)
- [ ] **Step 5: Commit** `fix: mute scroll glides 300ms after resize/zoom (ConPTY repaint bounce)`

---

### Task 3: Wide-char/emoji frame extraction test (A3)

**Files:**
- Modify: `crates/term-core/src/lib.rs` (tests module, near `scrolled_up_*` tests)

- [ ] **Step 1: Add test** (adapt `feed`/constructor names to the existing tests in the same module — they exist at ~line 612):

```rust
#[test]
fn wide_char_emits_single_leading_cell() {
    let mut t = Terminal::new(20, 4); // match existing test constructor
    t.feed("😀".as_bytes());          // match existing test feed helper
    let f = t.frame();
    let emoji: Vec<_> = f.cells.iter().filter(|c| c.c == '😀').collect();
    assert_eq!(emoji.len(), 1, "emoji must render exactly once");
    assert_eq!(emoji[0].col, 0);
    // The spacer cell (col 1) must not emit a glyph.
    assert!(!f.cells.iter().any(|c| c.col == 1 && c.c != '\0'));
}
```

- [ ] **Step 2: Run** `cargo test -p term-core wide_char` — expected PASS (spacer skip already exists at line 484). If FAIL, fix frame extraction so `WIDE_CHAR_SPACER` cells are skipped and re-run.
- [ ] **Step 3: Commit** `test: wide char renders once, spacer emits nothing`

Note for reviewer: the backspace `�` is PSReadLine echoing a broken surrogate; if user's Windows Terminal comparison shows identical behavior, no velo change — documented in spec.

---

### Task 4: Palette epoch → live editor recolor (A4b)

**Files:**
- Modify: `crates/term-core/src/palette.rs` (set_theme ~line 57)
- Modify: `crates/editor/src/highlight.rs` (struct fields ~line 40, `lines` ~line 104, tests ~line 160)

**Interfaces:**
- Produces: `term_core::palette::epoch() -> u64`, bumped by every `set_theme` call.

- [ ] **Step 1: Failing test in `highlight.rs` tests:**

```rust
#[test]
fn theme_change_invalidates_span_cache() {
    let mut h = Highlighter::for_path("a.rs").unwrap();
    let before = h.lines("fn main() {}\n", 1)[0].clone();
    let mut ansi = palette::BASE16;
    ansi[5] = [1, 2, 3]; // keyword slot
    palette::set_theme(ansi, [9, 9, 9], [9, 9, 9], [9, 9, 9]);
    let after = h.lines("fn main() {}\n", 1)[0].clone();
    assert_ne!(before, after, "same rev must re-resolve colors after set_theme");
}
```

(`Span` may need `#[derive(Clone, PartialEq, Debug)]` — add if missing.)

- [ ] **Step 2: Run** `cargo test -p editor theme_change` — expect FAIL (cache hit returns old colors).

- [ ] **Step 3: Implement.** `palette.rs`:

```rust
use std::sync::atomic::{AtomicU64, Ordering};

/// Bumped on every theme swap; caches of resolved RGB key off this.
static EPOCH: AtomicU64 = AtomicU64::new(0);

pub fn epoch() -> u64 {
    EPOCH.load(Ordering::Relaxed)
}
```

In `set_theme`, after the write: `EPOCH.fetch_add(1, Ordering::Relaxed);`

`highlight.rs`: replace `cached_rev: Option<u64>` with `cached_key: Option<(u64, u64)>` (init `None`), and in `lines`:

```rust
let key = (rev, palette::epoch());
if self.cached_key == Some(key) {
    return &self.cache;
}
self.cached_key = Some(key);
```

- [ ] **Step 4: Run** `cargo test -p editor && cargo test -p term-core` — expect PASS. (Tests share global palette state; if other tests race, mark this test `#[serial]` only if the workspace already uses serial_test — otherwise restore theme at test end with `palette::set_theme(palette::BASE16, DEFAULT_FG, DEFAULT_FG, SELECTION_BG)`.)
- [ ] **Step 5: Commit** `fix: open editor files recolor on theme change (palette epoch in span cache key)`

---

### Task 5: Editor tab strip theme blend (A4a)

**Files:**
- Modify: `csharp/Velo.App/MainWindow.xaml` (EditorTabs ListView, lines 303-354)
- Modify: `csharp/Velo.App/MainWindow.xaml.cs` (`UpdatePanelTint` ~line 2412)

- [ ] **Step 1: XAML — theme-neutral item brushes.** Inside `<ListView x:Name="EditorTabs" ...>` add before `ItemsPanel`:

```xml
<ListView.Resources>
    <SolidColorBrush x:Key="ListViewItemBackground" Color="Transparent" />
    <SolidColorBrush x:Key="ListViewItemBackgroundPointerOver" Color="#22FFFFFF" />
    <SolidColorBrush x:Key="ListViewItemBackgroundSelected" Color="#33FFFFFF" />
    <SolidColorBrush x:Key="ListViewItemBackgroundSelectedPointerOver" Color="#3DFFFFFF" />
</ListView.Resources>
```

- [ ] **Step 2: C# — tint strip + foreground with the theme.** In `UpdatePanelTint`, after `DetailsSurface.Background = surface;`:

```csharp
EditorTabs.Background = surface;
var fg = Themes.Rgb(Themes.ByName(_settings.ThemeName).Fg);
EditorTabs.Foreground = new SolidColorBrush(
    Microsoft.UI.ColorHelper.FromArgb(0xFF, fg.R, fg.G, fg.B));
```

(`TextBlock` in the tab template inherits `Foreground`; close-button glyph inherits too.)

- [ ] **Step 3: Sanity** — no WSL build; visually verified by user on Windows (tab strip matches chrome tint, tabs are translucent overlays, text uses theme fg).
- [ ] **Step 4: Commit** `fix: editor tab strip follows theme tint + fg`

---

### Task 6: Ctrl+W closes editor tab (A4c)

**Files:**
- Modify: `csharp/Velo.App/MainWindow.xaml.cs` (`EditorTabClose_Click` ~line 2654, `EditorPanel_KeyDown` ~line 2665)

- [ ] **Step 1: Extract close helper** (replaces `EditorTabClose_Click` body):

```csharp
private void EditorTabClose_Click(object sender, RoutedEventArgs e)
{
    if ((sender as FrameworkElement)?.Tag is not uint id) return;
    CloseEditorFile(id);
}

private void CloseEditorFile(uint id)
{
    Native.velo_editor_close_file(_engine, id);
    var vm = EditorFiles.FirstOrDefault(f => f.Id == id);
    if (vm is not null) EditorFiles.Remove(vm);
    if (EditorFiles.Count == 0) SetEditorMode(false);
    else if (EditorTabs.SelectedItem is null)
        EditorTabs.SelectedIndex = EditorFiles.Count - 1;
}
```

- [ ] **Step 2: Hook Ctrl+W** at the top of `EditorPanel_KeyDown`, before forwarding to `velo_editor_key`:

```csharp
if (e.Key == Windows.System.VirtualKey.W && (Modifiers() & 1) != 0)
{
    if (EditorTabs.SelectedItem is EditorFileVM cur) CloseEditorFile(cur.Id);
    e.Handled = true;
    return;
}
```

- [ ] **Step 3: Commit** `feat: Ctrl+W closes focused editor tab`

---

### Task 7: Workspace verification

- [ ] **Step 1:** `cargo test --workspace` — all pass.
- [ ] **Step 2:** `cargo check --target x86_64-pc-windows-gnu` — clean.
- [ ] **Step 3:** Hand to user for Windows manual verify (spec's Verification list).
