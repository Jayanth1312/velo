# Velo UI Plan — Otty parity

Goal: bring Otty's design + feature set to Velo (Windows / WinUI 3 + Rust core).
Reference screenshots: `otty-scs/`.

## Layout decision (locked)

- **Tabs panel → RIGHT** (keep current `SidebarSurface`, already hover-reveal).
- **Details panel → LEFT** (new `DetailsSurface`, mirror image of the right one).
  Holds Info / Outline / Git / Files, switched by an icon row at its top.
- **Titlebar**: thin, transparent. Hover reveals both panel toggles (left toggle
  for details, right toggle for tabs). No toggle visible at rest.
- **Command palette** overlay (Otty screenshot 8) + **Theme picker** (VS Code
  themes: Dracula, GitHub, Nord, etc.).
- Shell integration: **full**, auto-inject OSC 7 (cwd) + OSC 133 (command marks)
  into the user's PowerShell/pwsh/bash profile.
- Sequencing: **chrome first**, then wire panel data.

Otty has tabs left / details right; we mirror it (tabs right / details left) to
reuse Velo's existing right-side tab pane.

---

## Phase 1 — Left details panel scaffold + dual hover-reveal chrome

Pure C#/XAML. No data yet — placeholders.

- Add `DetailsSurface` (left-anchored `Border`, width 280, `TranslateTransform`,
  slide animation) mirroring `SidebarSurface`. Hairline divider on its right edge.
- Add a left `ToggleHost` + `PaneToggle` in the titlebar's left edge; second
  `KeyboardAccelerator` (e.g. `Alt+J`) toggles it. Generalize `SetSidebar` into
  `SetPane(side, open)` so both panels share the slide logic.
- `ContentRoot.Margin` now driven by both panels (left + right insets).
- Details panel top: icon row **Info · Outline · Git · Files** + a collapse
  glyph, styled like `IconButtonStyle` (hover-only chrome). Selected tab gets the
  filled pill (Otty screenshots 4–7). Body = `Frame`/`ContentControl` swapping 4
  placeholder `UserControl`s.
- Hover behavior: extend existing titlebar-hover logic to fade both toggles in on
  pointer-over the 40px strip, out otherwise.
- **Check**: both panels slide independently, toggles appear only on hover,
  terminal reflows once (no gap/flash), Alt+B / Alt+J work.

Files: `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml` (styles).

---

## Phase 2 — Command palette + Theme picker

- **Palette overlay**: centered `Popup` (not ContentDialog — needs the Otty
  borderless look), search `TextBox` + filtered `ListView`. Commands: Toggle Tabs
  Panel, Toggle Details Panel, Details: Info/Outline/Git/Files, Theme…, Clear
  Screen. Show right-aligned shortcut hints. Bind `Ctrl+Shift+P` (and `Ctrl+P`).
- **Theme picker**: "Theme…" opens a second palette page listing bundled themes.
- **Themes as data**: `Themes/*.json` = 16 ANSI colors + bg + fg + cursor +
  selection. Ship Dracula, GitHub Dark/Light, Nord, Monokai, Solarized Dark/Light,
  One Dark. Persist chosen theme in `Settings`.
- **FFI add** (Rust `crates/ffi`): `velo_set_palette(eng, *const u32[20])` (or a
  `#[repr(C)]` Theme struct) → renderer applies full palette + fg + cursor. Wire
  through `term-core`/`renderer`. Chrome surfaces (`ContentRoot`, both sidebars)
  tint from the theme bg so everything reads as one surface.
- **Check**: picking Dracula recolors terminal text *and* chrome live; survives
  restart.

Files: `MainWindow.xaml(.cs)`, new `CommandPalette.xaml`, `Themes/*.json`,
`Settings.cs`, `Interop.cs`, Rust `ffi` + `renderer`.

---

## Phase 3 — Shell integration (cwd + command marks)

Unlocks Info/Files/Git/Outline. Core + profile work, no new panels.

- **Profile injection** (C#): on first run, idempotently append integration
  snippets to `$PROFILE` (PowerShell/pwsh) and `~/.bashrc` (wsl):
  - OSC 7 on prompt → emit `cwd`.
  - OSC 133 `A`/`B` (prompt start/end), `C` (command start, with command text),
    `D;<exit>` (command end). Marker block guarded by `# >>> velo integration`.
  - Setting toggle to disable; never double-inject.
- **Rust**: have `alacritty_terminal`'s OSC handler capture 7 + 133; store
  per-session `cwd`, current command, last exit, timing. New callbacks:
  `OnCwdChanged(ctx,id,utf16,len)`, `OnCommand(ctx,id,phase,exit,dur_ms,utf16,len)`.
- **C#**: extend `TabVM` with `Cwd` + `ObservableCollection<CommandEntry>`.
- **Check**: `cd` updates a debug readout; running a command records start/exit/
  duration per tab.

Files: new `ShellIntegration.cs`, `Interop.cs`, `TabVM.cs`, Rust `term-core`/`ffi`.

---

## Phase 4 — Info panel (Otty screenshot 4)

- **Working Directory**: path + Copy Path, Reveal in Explorer (`explorer /select`),
  Open in VS Code (`code <path>`).
- **Process**: shell name, PID, uptime. PID from the ConPTY child handle
  (`velo_tab_pid` FFI add); uptime from a DispatcherTimer.
- **Ports**: listening ports owned by the shell's process subtree
  (`GetExtendedTcpTable` filtered by PID tree). "No listening ports" empty state.
- Refresh on cwd change, command end, and panel focus.
- **Check**: matches Otty layout; Copy/Reveal/Open work; ports show a real
  `python -m http.server`.

Files: `InfoPanel.xaml(.cs)`, `Interop.cs`, Rust `ffi` (`velo_tab_pid`).

---

## Phase 5 — Files panel (Otty screenshot 7)

Pure C# (no core changes).

- `TreeView` rooted at the active tab's cwd. Find box (filter), up/refresh/hidden
  toggles (top-right icons). Folder/file glyphs.
- `FileSystemWatcher` for live updates; re-root on cwd change.
- Double-click: file → `code`/default open; folder → expand. Optional "type
  `cd <path>`" into the terminal.
- **Check**: tree reflects cwd, Find filters, hidden toggle, live add/remove.

Files: `FilesPanel.xaml(.cs)`.

---

## Phase 6 — Git panel (Otty screenshot 6)

- In a repo (cwd walk-up to `.git`): branch, ahead/behind, staged / unstaged /
  untracked groups via `git status --porcelain=v2 --branch`. Else "Not a Git
  repository".
- Shell out to `git.exe` async; refresh on cwd change, command end, focus.
- (Later) stage/unstage/discard actions.
- **Check**: real repo shows branch + changes; non-repo shows empty state.

Files: `GitPanel.xaml(.cs)`, small `Git.cs` porcelain parser.

---

## Phase 7 — Outline panel (Otty screenshot 5)

- Per-tab command history from Phase 3 marks: `~/<dir>` + timestamp header, then
  the list of commands run, newest grouped by directory.
- Click a command → scroll terminal to that output (needs a scrollback anchor in
  core; **defer** scroll-to if costly — list-only first).
- **Check**: commands appear with dir + relative time as they run.

Files: `OutlinePanel.xaml(.cs)`, (optional) Rust scrollback anchor FFI.

---

## Phase 8 — Polish

- **Tab grouping menu** (Otty screenshot 2): No Grouping / By Project / By Date,
  order Created/Updated, Insert/Remove Dividers — wire the existing `FilterButton`
  flyout.
- Persist panel open/closed + last details tab + theme in `Settings`.
- Map Otty's ⌘ shortcuts to Ctrl equivalents; show in tab badges (`Ctrl+1`…).
- Empty/error states, focus-return correctness, drag-region updates for the new
  left toggle.

---

## FFI additions summary (Rust `crates/ffi`)

| Function / callback | Phase | Purpose |
|---|---|---|
| `velo_set_palette` | 2 | full theme palette + fg/cursor |
| `OnCwdChanged` | 3 | OSC 7 cwd |
| `OnCommand` | 3 | OSC 133 command start/exit/timing |
| `velo_tab_pid` | 4 | shell PID for Process/Ports |
| scrollback anchor (opt) | 7 | Outline scroll-to |

## Risks / unknowns

- OSC 133 command-text capture depends on the profile hook quality per shell.
- Ports-by-PID-subtree needs the full child tree (ConPTY → shell → grandchildren).
- Reading another process's cwd on Windows is unreliable → OSC 7 is the source of
  truth (hence full shell integration).
