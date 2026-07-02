# Velo Plan 1 — Performance + correctness fixes


Ordered by felt impact. Tasks 1–4 = "instant feel". Tasks 5–7 = terminal correctness for CLI/TUI tools. Task 8 = hygiene.

### Task 1: Kill hot-path logging (biggest felt win)
- `crates/ffi/src/lib.rs:1376,1389` — delete `dbglog!` calls in `velo_key` / `velo_char` (each opens+appends+closes a file per keystroke, resolves `current_dir` every call).
- Keep `dbglog` for rare events (attach, spawn, panic, EOF) but cache the path in a `OnceLock<PathBuf>`.
- `csharp/Velo.App/MainWindow.xaml.cs` — remove/gate per-keystroke `Log.Write` in `Panel_KeyDown` (:1216), `Panel_CharacterReceived` (:1286), `Root_KeyDown` (:1189), and the log in `FocusTerminal`.
- Gate remaining C# logging: `Log.Enabled` static bool from env var `VELO_DEBUG=1`, checked before formatting (avoid string interpolation cost when off).

### Task 2: PTY flood coalescing
- `crates/pty-win/src/lib.rs:165` — reader buffer 4096 → 65536.
- `crates/ffi/src/lib.rs` — `Session` gains `wakeup_pending: Arc<AtomicBool>`. Reader posts `MSG_PTY_DATA` only when it flips false→true; `on_pty_data` clears it before draining the inbox. Collapses hundreds of queued render passes during `cat`/agent streaming into one per pump.
- `on_pty_data`: early-return when drained bytes empty (skip parse+render no-op).

### Task 3: Renderer row-indexed rebuild
- `crates/renderer/src/lib.rs` — `rebuild_row` (:462) filters ALL `frame.cells` per dirty row → full repaint is O(rows × cells). In `draw()`, build per-row ranges once (cells are row-major sorted): `Vec<(start,end)>` indexed by row; pass `&frame.cells[range]` slice to `rebuild_row`. `apply_cursor` (:519) uses the cursor row's range instead of linear `find`.

### Task 4: Cheap zoom/DPI — stop reloading the font stack
- `crates/text/src/lib.rs` — `Font::new` copies 2MB bundled NF + reloads fallback font files + rebuilds FontSystem every call. Add `Font::set_size(&mut self, font_size: f32)`: reuse existing `font_system`, rebuild `Buffer` metrics, re-measure "M", update `cell_w/cell_h/ascent`.
- `crates/renderer/src/lib.rs` — add `Renderer::set_font_metrics(cell_w, cell_h, ascent)`: clear `glyphs` map, reset shelf (`shelf_x=1, shelf_y=0, shelf_h=1`), keep pipeline/atlas/buffers.
- `crates/ffi/src/lib.rs` `Engine::rebuild_font` — call `font.set_size(...)` + per-pane `renderer.set_font_metrics(...)` instead of `text::Font::new` + `build_renderer`.

### Task 5: Scrollback + mouse wheel
- `crates/term-core/src/lib.rs` — `WinSize::total_lines` currently == screen_lines (no history). Use alacritty `Config { scrolling_history: 10_000, .. }` (check field name on the vendored version). Add:
  - `Terminal::scroll_display(delta_lines: i32)` → `term.scroll_display(Scroll::Delta(n))`
  - `Terminal::display_offset()` accessor; `viewport_point` must account for display offset when scrolled.
  - `frame()` already renders via `renderable_content()` (viewport-relative) — verify line coords stay viewport-relative with an offset; scrolling forces `FrameDamage::Full` (track last offset like `last_selection`).
- New FFI: `velo_pane_scroll(eng, pane, delta)` → scrolls the pane's session, renders. When `TermMode::ALT_SCREEN` active, translate wheel to arrow-key sequences (3 per notch) instead of scrolling history.
- C# `MainWindow.xaml.cs` — `PointerWheelChanged` handler on the SwapChainPanels/PaneHost → `Native.velo_pane_scroll` with wheel delta / 120 * 3 lines.
- `Interop.cs` — add the P/Invoke.

### Task 6: Key encoding (F-keys, modifiers, DECCKM, Alt)
- `crates/ffi/src/lib.rs` `nav_seq` (:1113) → replace with `key_seq(vk, ctrl, shift, alt, app_cursor) -> Option<Vec<u8>>`:
  - F1–F4: `ESC OP..OS`; F5–F12: `CSI 15~,17~..24~` (with modifier param when mods held).
  - Arrows/Home/End: `ESC O A..` when DECCKM (app cursor) set and no mods; `CSI 1;{1+shift+2*alt+4*ctrl} A` with mods.
  - PgUp/PgDn/Del/Ins: `CSI n;m ~` with modifier param.
- `crates/term-core` — add `Terminal::app_cursor(&self) -> bool` (`TermMode::APP_CURSOR`).
- Alt+printable: in `on_key`, when alt && !ctrl, prefix next char path — simplest: `on_char` checks a `pending_alt` flag set by `on_key` (mods bit2 already arrives; currently ignored) and writes `ESC` before the UTF-8 bytes. C# already passes bit2.
- Shift+PgUp/PgDn (no alt-screen) → scroll history one page (ties into Task 5).

### Task 7: App mouse reporting (SGR 1006)
- `crates/term-core` — expose `mouse_mode()` (TermMode MOUSE_* bits + SGR flag).
- `crates/ffi` `on_mouse_pane` — when the session's mouse mode active (and not shift-held override for selection), encode `CSI < b;col;row M/m` and write to PTY instead of driving selection. Wheel (Task 5) sends buttons 64/65 when reporting active.

### Task 8: Hygiene / small wins
- `csharp/Velo.App/TabVM.cs` — cap `CommandHistory` at 500 entries (drop oldest).
- `MainWindow` ctor — move `ShellIntegration.Ensure(_settings)` to `Task.Run` (must finish before first prompt renders, not before ctor; profile injection is idempotent).
- `csharp/Velo.App/Velo.App.csproj` — add `<PublishReadyToRun>true</PublishReadyToRun>`.
- `.gitignore` — add `build.txt`, `*.binlog`, `velo-debug.log`; `git rm --cached csharp/msbuild.binlog build.txt` if tracked.
- Resize coalescing (`Panel_SizeChanged` → defer to one `velo_pane_resize` per dispatcher pass at Low priority, latest size wins) — do LAST, only if drag-resize still feels heavy after 1–4.

### Execution mode (per user request: subagent-driven)
Follow `superpowers:subagent-driven-development`. Dispatch one implementer subagent per task (Tasks 1–8), sequentially where files overlap:
- Independent groups: {Task 1}, {Task 2}, {Task 8} can go first/parallel-ish; {Task 3, Task 4} both touch renderer — sequence; {Task 5, 6, 7} touch term-core+ffi — sequence (5 → 6 → 7).
- Each subagent: full file paths + exact change spec from this plan; must run `cargo test -p term-core -p renderer -p text` (Linux-runnable) and `cargo check` before reporting.
- Main thread reviews each diff before next dispatch.

### Verification
1. `cargo test` (workspace, Linux) — term-core tests must pass incl. new tests: scrollback scroll/offset, key_seq encodings (F-keys, modified arrows, DECCKM), SGR mouse encoding, damage-partial frame.
2. `cargo check --target x86_64-pc-windows-msvc -p velo_core` if target installed; else flag for Windows build.
3. On Windows (user): `cargo build --release -p velo_core`, `dotnet build csharp/Velo.App`, then manual: typing latency (no velo-debug.log growth), `type` a large file (smooth, responsive input during flood), Ctrl+= zoom (instant), wheel scrollback, F-keys + arrows in vim/less, mouse in htop under WSL tab.

