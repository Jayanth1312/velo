# Round 2 Implementation Plan — overscan glide, zoom snap, editor UX, reveal

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gap-free, stutter-free scroll glide (overscan rows); zoom snap guard; titlebar tab title; file-click opens editor; rounded tab pills; Ctrl+J bottom terminal; working Reveal.

**Architecture:** Overscan lives entirely in `crates/renderer` (slot buffer grows by 8 rows top+bottom; `scroll_bump` shifts slot rows so departing content stays visible during the glide). ffi picks glide vs snap. C# work is WinUI-only.

**Tech Stack:** Rust (wgpu), C# WinUI 3.

## Global Constraints

- `cargo test` workspace green on Linux; `cargo check -p velo_core` green (check BOTH `Pane` initializers when touching that struct — line ~797 and ~1722, the second is windows-only).
- C# built only on user's Windows machine — keep diffs surgical.
- Spec: `docs/superpowers/specs/2026-07-07-round2-scroll-editor-design.md`.

---

### Task 1: Renderer overscan + shift (R1)

**Files:** Modify `crates/renderer/src/lib.rs`

**Interfaces produced:**
- `pub const OVERSCAN_ROWS: u16 = 8;`
- `Renderer::scroll_reset(&mut self)` — px→0, wipe all slots, flag full upload.
- `Renderer::scroll_bump(rows_up)` — shifts slot rows + bumps px; snaps via `scroll_reset` when the result would exceed `OVERSCAN_ROWS` cells.
- Free fn `shift_slot_rows(slots, row_slots, rows_up, cell_h)` (unit-testable).

- [ ] **Step 1: Failing tests** (new `mod overscan_tests`): shifting a marked instance up one row moves its slot index up `row_slots` and its `rect[1]` down by `cell_h`; vacated rows are `ZERO_INSTANCE`; shift down mirrors.
- [ ] **Step 2: Implement:**
  - `slot_base` returns `((row + OVERSCAN_ROWS) as usize * cols + col) * SLOTS_PER_CELL`.
  - Allocation in `draw`: `total = cols * (rows + 2*OVERSCAN_ROWS) * SLOTS_PER_CELL`.
  - Dirty-row upload offsets use `(row + OVERSCAN_ROWS)`.
  - `full_upload: bool` field: set by shift/reset/`set_font_metrics` (which also wipes slots); consumed in `draw` (upload whole buffer, skip per-row writes).
  - `shift_slot_rows`: `rows_up > 0` → every slot row `r` gets old row `r + rows_up` content with `rect[1] -= rows_up as f32 * cell_h` (skip `ZERO_INSTANCE`); bottom vacated rows zeroed. Negative mirrors.
  - `scroll_bump`: compute `new_px = px + rows_up*cell_h`; `if new_px.abs() > OVERSCAN_ROWS as f32 * cell_h { self.scroll_reset(); } else { shift; px = new_px; }`. `ScrollAnim::bump` keeps its (rows_up, cell_h, max_px) signature for its own tests but Renderer no longer relies on the clamp (pass the overscan max).
  - Scissor: whenever instances are drawn, scissor to the grid rect (`pad_x, pad_y, cols*cell_w, rows*cell_h`, clamped to surface) so overscan rows never leak into the padding/leftover strip.
- [ ] **Step 3:** `cargo test -p renderer` PASS.
- [ ] **Step 4:** Commit `feat: renderer overscan rows — glide without edge gaps`.

### Task 2: ffi glide-vs-snap (R2)

**Files:** Modify `crates/ffi/src/lib.rs`

- [ ] **Step 1:** `GLIDE_MUTE_AFTER_RESIZE` 300→500 ms.
- [ ] **Step 2:** `render_pane`:

```rust
if frame.scrolled_up != 0 {
    let organic = frame.scrolled_up.unsigned_abs() <= renderer::OVERSCAN_ROWS as u32;
    if organic && std::time::Instant::now() >= pane.glide_mute_until {
        pane.renderer.scroll_bump(frame.scrolled_up);
    } else {
        pane.renderer.scroll_reset(); // snap: kill any live glide + stale overscan
    }
}
```

- [ ] **Step 3:** `cargo check -p velo_core` clean; commit `fix: snap instead of glide on oversized/muted shifts (zoom bounce)`.

### Task 3: Titlebar tab title (R3)

**Files:** Modify `csharp/Velo.App/MainWindow.xaml` (TitleBar grid ~line 199), `MainWindow.xaml.cs` (title/active-change handlers + `UpdatePanelTint`).

- [ ] **Step 1:** XAML: inside TitleBar grid add `<TextBlock x:Name="TitleBarTitle" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="12" IsHitTestVisible="False" TextTrimming="CharacterEllipsis" MaxWidth="420" />`.
- [ ] **Step 2:** C#: helper `UpdateTitleBarTitle()` sets `TitleBarTitle.Text` to active `TabVM` title (empty string when none); call from the existing title-changed UI handler and active-tab-changed path (locate: handlers that update `TabVM.Title` / `TabList.SelectedItem`). Foreground set alongside `EditorTabs.Foreground` in `UpdatePanelTint`.
- [ ] **Step 3:** Commit `feat: active tab title centered in the title bar`.

### Task 4: File click opens editor (R4)

**Files:** Modify `csharp/Velo.App/MainWindow.xaml.cs:649,684`

- [ ] **Step 1:** `FilesTree_ItemInvoked`: `else if (fi is { IsDir: false }) OpenInEditor(fi.Path);` (drop `&& _editorMode`). `FilesList_DoubleTapped`: file branch becomes `OpenInEditor(fi.Path);` (drop ShellOpen fallback).
- [ ] **Step 2:** Commit `fix: files tree opens files in the editor regardless of mode`.

### Task 5: Rounded tab pills (R5)

**Files:** Modify `csharp/Velo.App/MainWindow.xaml` (EditorTabs template), `EditorFileVM.cs`, `MainWindow.xaml.cs` (`EditorTabs_SelectionChanged`).

- [ ] **Step 1:** `EditorFileVM`: add `IsSelected` bool with change notification + `SelectedBackground` brush (or bind Border.Background via converter-free `x:Bind` on a Brush property returning `#33FFFFFF`/transparent).
- [ ] **Step 2:** Template: wrap tab Grid in `<Border CornerRadius="6" Padding="14,5" Background="{x:Bind SelectedBrush, Mode=OneWay}">`; presenter brushes (existing `ListView.Resources`) all → Transparent.
- [ ] **Step 3:** `EditorTabs_SelectionChanged`: set `IsSelected` false on removed, true on added VMs.
- [ ] **Step 4:** Commit `fix: editor tabs are rounded pills with real padding`.

### Task 6: Ctrl+J bottom terminal (R6)

**Files:** Modify `csharp/Velo.App/MainWindow.xaml` (EditorHost rows), `MainWindow.xaml.cs`.

- [ ] **Step 1:** XAML: EditorHost RowDefinitions 36/*/`Auto`; row 2 = `<Grid x:Name="EditorTermHost" Height="220" Visibility="Collapsed" Background="Transparent" IsTabStop="True"><SwapChainPanel x:Name="EditorTermPanel" SizeChanged="EditorTerm_SizeChanged" /></Grid>` with KeyDown/CharacterReceived/PointerWheelChanged/PointerPressed handlers.
- [ ] **Step 2:** C#: `_editorTermPane = -1`. `ToggleEditorTerminal()`:
  - first open: `velo_pane_new` → bind returned swapchain to `EditorTermPanel` (reuse the existing swapchain-binding helper used for split panes), `velo_pane_bind(pane, active session)`, initial `velo_pane_resize` from panel actual size.
  - toggle visibility; on show `velo_pane_focus(_editorTermPane)` + focus `EditorTermHost`; on hide `velo_pane_focus(editor pane id)` + focus `EditorPanel`.
  - re-bind session when active tab changes while visible (hook the active-changed path).
- [ ] **Step 3:** Input: `EditorTermHost` KeyDown → intercept Ctrl+J (toggle) else delegate to the same code path `Panel_KeyDown` uses (velo_key); CharacterReceived → velo_char; wheel/pointer → `velo_pane_mouse` with `_editorTermPane` (mirror PaneHost per-pane mouse handler). `EditorPanel_KeyDown` also intercepts Ctrl+J → toggle.
- [ ] **Step 4:** `EditorTerm_SizeChanged` → `velo_pane_resize(_editorTermPane, physical px)` (same DPI math as other panes).
- [ ] **Step 5:** Commit `feat: Ctrl+J bottom terminal panel in editor mode`.

### Task 7: Reveal fix (R7)

**Files:** Modify `csharp/Velo.App/MainWindow.xaml.cs:546-568`

- [ ] **Step 1:** Extend `ToWindowsPath`: non-/mnt absolute unix path → `\\wsl.localhost\<distro>` + path with `\`; distro = first line of `wsl.exe -l -q` output, cached in a static (null on failure → return null).
- [ ] **Step 2:** `InfoReveal_Click`: `Log.Write($"reveal: raw={raw} win={win}")`; only launch when `win != null && Directory.Exists(win)`; else log skip. No `Environment.CurrentDirectory` fallback.
- [ ] **Step 3:** Commit `fix: reveal converts WSL home paths and never falls back to Documents`.

### Task 8: Verification

- [ ] `cargo test --workspace` green; `cargo check -p velo_core` green.
- [ ] Hand to user: Windows build + spec's manual list.
