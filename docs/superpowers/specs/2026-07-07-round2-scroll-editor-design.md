# Velo — Round 2: overscan glide, zoom snap, editor UX, reveal fix

Approved design (2026-07-07). Follow-up to the bug-fix round: scroll glide
rebuilt with overscan, remaining zoom bounce killed, editor mode UX gaps.

## R1. Renderer overscan (fixes jitter + vanishing edge rows)

Problem: the glide displaces the whole grid, but the instance buffer holds
only viewport rows → gaps open at the edges while gliding (editor gutter
lines "disappear"), and once the displacement clamp saturates, new row
motion is dropped (stutter).

Fix (crates/renderer):
- `OVERSCAN_ROWS = 8` extra rows above and below the viewport in `slots`.
  Slot row = frame row + OVERSCAN_ROWS; slot grid = grid_rows + 16.
- `scroll_bump(rows_up)` first shifts slot contents vertically by
  `rows_up` rows (patching each moved instance's y by `-rows_up * cell_h`),
  so the rows that scroll off the viewport land in overscan and stay
  visible during the glide. Then displacement bumps as before.
- Clamp = `OVERSCAN_ROWS` cells (glide never exceeds what overscan can
  cover). Tau stays 65 ms. At real wheel/streaming rates equilibrium
  displacement ≈ 2 cells, far from the clamp, so dropout disappears.
- A bump beyond the overscan (|rows| > OVERSCAN_ROWS) snaps: reset px to 0,
  clear overscan (see R2).
- Full-buffer upload on frames where a shift happened (scroll frames were
  already full-damage in practice).
- Overscan rows are cleared on resize/set_font_metrics.

## R2. Zoom bounce, second guard

ConPTY's post-resize repaint can land after the 300 ms mute. Two layers:
- Mute window raised to 500 ms (`GLIDE_MUTE_AFTER_RESIZE`).
- Size guard, timing-independent: `|scrolled_up| > OVERSCAN_ROWS` cannot be
  organic streaming (≤ a few rows/frame) or wheel (3 rows/notch) — snap
  instead of glide (`scroll_reset()` + no bump). ConPTY repaints shift
  ~a screenful, so they always snap.

## R3. Active tab title in the title bar

Centered `TextBlock` inside the `TitleBar` grid showing the active tab's
title. Updates on active-tab change and on OSC title change (same paths
that update the sidebar tab list). Foreground follows the theme fg (set in
`UpdatePanelTint`). Hidden when empty.

## R4. Files tree always opens the editor

`FilesTree_ItemInvoked` and `FilesList_DoubleTapped` currently open a file
in the editor only when `_editorMode` is already on; otherwise click does
nothing / double-click shell-opens. Fix: file activation always calls
`OpenInEditor(path)` (it already flips to editor mode). `ShellOpen`
fallback is removed from the double-tap path.

## R5. Editor tab pills

WinUI's `ListViewItemPresenter` ignores style `CornerRadius` → sharp
corners. Move the pill into the item template:
- `Border` with `CornerRadius="6"`, `Padding="14,5"`, background bound to a
  new `EditorFileVM.IsSelected` (INotifyPropertyChanged) — `#33FFFFFF`
  selected, transparent otherwise (pointer-over handled by presenter still
  being hit-testable is dropped; selection is the only highlight).
- Presenter brushes (`ListViewItemBackground*`) all transparent.
- `EditorTabs_SelectionChanged` sets `IsSelected` on the affected VMs.

## R6. Ctrl+J bottom terminal in editor mode

- `EditorHost` gains row 2: height 220 px, `Visibility=Collapsed`,
  containing a `SwapChainPanel` (`EditorTermPanel`) inside a transparent
  focusable `Grid` (`EditorTermHost`) — same focus pattern as `PaneHost`.
- First Ctrl+J: `velo_pane_new(Terminal)` → bind swapchain to the panel,
  `velo_pane_bind(pane, active session)`, `velo_pane_resize` on
  SizeChanged. Toggle: show/hide row, `velo_pane_focus` between editor
  pane and terminal pane, XAML focus follows.
- Keyboard: `EditorTermHost` KeyDown/CharacterReceived forward to
  `velo_key`/`velo_char` (they route to the focused pane's session).
  Wheel → `velo_mouse` path used by PaneHost (reuse existing handlers'
  pattern with the new pane id).
- Ctrl+J works from both the editor pane and the terminal panel.
- Bound session = active tab's session at first open; re-bind on active
  tab change while visible.

## R7. Reveal button fix

`InfoReveal_Click`:
- Convert `/home/...` (and any non-/mnt absolute WSL path) via
  `\\wsl.localhost\<distro>` using the default distro from `wsl.exe -l -q`
  (first line), cached. `/mnt/<d>/...` conversion unchanged.
- `Directory.Exists(win)` check before launching; if missing, log and do
  nothing (no explorer fallback to Documents).
- `Log.Write` the raw cwd and resolved path for diagnosis.

## Verification

- `cargo test` workspace (new: overscan shift preserves row content;
  oversized bump snaps; clamp = overscan).
- `cargo check -p velo_core` on Linux (both `Pane` initializers!); Windows
  build by user.
- Windows manual: wheel + claude streaming glide (no gaps top/bottom in
  editor and terminal, no stutter), zoom in/out (no bounce), titlebar
  title, single-click file opens editor, tab pills rounded/padded, Ctrl+J
  terminal toggle + typing works, Reveal opens correct folder.
