# Velo — Progress

## Phase 0 — Workspace scaffold

- [x] cargo build succeeds (Linux dev env, all crates)
- [ ] cargo build succeeds on Windows (x86_64-pc-windows-msvc)
- [ ] `cargo run -p app` opens a window and clears it to a solid color
- [ ] stdout prints `backend: Dx12` (or `backend: Vulkan` fallback)
- [x] PROGRESS.md created

### Deviations from plan

- wgpu-29 API spelling (struct fields, per plan's "adapt to 29.x API" note):
  - `Instance::new` takes `InstanceDescriptor` by value; `InstanceDescriptor`
    has no `Default` (non-`Copy` `display` field), so it is built with
    `..wgpu::InstanceDescriptor::new_without_display_handle()`.
  - `Surface::get_current_texture()` returns the `CurrentSurfaceTexture` enum
    (not `Result`); render matches `Success`/`Suboptimal` and reconfigures on
    `Outdated`/`Lost`.
  - `RenderPassDescriptor` requires the `multiview_mask: None` field.
  - `RenderPassColorAttachment` requires `depth_slice: None`; `SurfaceConfiguration`
    requires `desired_maximum_frame_latency`.
- pollster pinned at `0.4` resolved fine; no fallback to `0.3` needed.
- No `license` field added (no license chosen yet).

## Phase 1 — Working single-tab terminal

- [x] `text`: cosmic-text monospace metrics + per-glyph R8 rasterization
- [x] `term-core`: alacritty_terminal wrapper, ANSI palette, render `Frame`
- [x] `term-core`: unit tests for palette + INVERSE swap (5 pass on Linux)
- [x] `pty-win`: ConPTY backend (spawn/resize/read/write + clean-close Drop)
- [x] `renderer`: wgpu glyph atlas + instanced-quad pipeline
- [x] `app`: ConPTY/terminal/renderer wiring, `ControlFlow::Wait`, key mapping
- [x] cargo build + clippy clean on Linux (whole workspace)
- [x] `cargo check --target x86_64-pc-windows-gnu` clean (full Windows typecheck)
- [ ] Windows functional DoD (run `cargo run -p app`: type/output, pager, resize,
      clean close, idle ~0% CPU) — pending a real Windows box; ConPTY can't run on Linux

### Deviations from plan (Phase 1)

- `TermSize` is test-only in alacritty_terminal 0.26 (`term::test`); used a local
  `WinSize` implementing `grid::Dimensions` (no scrollback) instead.
- Cursor visibility derived from `RenderableContent.cursor.shape != CursorShape::Hidden`
  rather than probing `TermMode::SHOW_CURSOR` directly.
- `EventLoopProxy` is created in `main` and passed into `App` — `ActiveEventLoop`
  has no `create_proxy` in winit 0.30.13 (only `EventLoop` does).
- `Renderer::new` takes `&queue` so it can write the reserved opaque white atlas
  texel (used for background/cursor quads) at construction.
- windows 0.62 API specifics (verified by compiling against the crate):
  - `InitializeProcThreadAttributeList`/`CreateProcessW` take `Option`-wrapped args;
    `lpcommandline` is `Some(PWSTR(..))`.
  - `PipelineLayoutDescriptor` has `immediate_size` (no `push_constant_ranges`);
    `bind_group_layouts: &[Option<&BindGroupLayout>]`.
  - `queue.write_texture` uses `TexelCopyTextureInfo`/`TexelCopyBufferLayout`.
- Windows code is compile-verified via the `x86_64-pc-windows-gnu` target, which
  pulls the real `windows` crate and caught every signature mismatch. Functional
  ConPTY behavior still needs a Windows run (verification tier 2).

## Phase 2 — Fast & correct rendering

### Done

- Default cell font size raised to 20pt (`app::FONT_SIZE_PT`).
- Glyph correctness (symbols / emoji / special chars):
  - `text`: primary monospace + curated symbol/emoji/CJK fallback faces (system
    scan only as a backstop) so any codepoint shapes via the fallback chain.
  - Color glyphs (emoji) rasterize as straight RGBA instead of being dropped;
    mask glyphs unchanged. Bold/italic select real font variants. A sub-pixel x
    bin (0..4) is threaded into the swash cache key + glyph cache key.
  - `renderer`: single **RGBA8** atlas. Mask glyphs stored as white + coverage
    in alpha (tinted by cell fg); color glyphs drawn as-is. A per-instance
    `kind` selects the blend in the shader.
- Single draw call for the whole grid (instanced unit quad; fixed
  `SLOTS_PER_CELL = 3` per cell: fill / glyph / decoration).
- Damage / dirty-row tracking: term-core exposes `FrameDamage` from
  alacritty's `Term::damage()` (selection change forces full). The renderer keeps
  a persistent instance buffer and `write_buffer`s only the damaged rows
  (contiguous runs coalesced) — unchanged rows keep their GPU bytes.
- Attributes: true-color fg/bg (already resolved in `palette`), bold (variant +
  named-color brighten), italic, underline (decoration quad), reverse (fg/bg
  swap), selection highlight (`palette::SELECTION_BG`), cursor shapes
  block / bar / underline.
- Mouse selection wired (left press = start, drag = extend) → `Term::selection`;
  copy text available via `selection_text()`.
- Present path: prefer `Mailbox` → `Immediate` (DX12 `ALLOW_TEARING`) → `Fifo`,
  `desired_maximum_frame_latency = 1`.
- Benchmark harness: `cargo run -p app --release -- bench`
  (`crates/app/src/bench.rs`).

### Definition-of-Done numbers

The DoD's four targets are **end-to-end Windows numbers** (keypress→pixel via a
real window, `cat` over ConPTY, on-screen present FPS, idle CPU of the running
app). They cannot be produced on this Linux dev host: ConPTY is Windows-only and
there is no GPU (WSL exposes only llvmpipe software Vulkan). The harness is in
place and will emit them on a Windows box; below are the host-measurable proxies
actually run here (`x86_64-unknown-linux-gnu`, release, llvmpipe).

| DoD target                                             | Status       | Measured proxy (this host)                                                                                                                                                                                     |
| ------------------------------------------------------ | ------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| keypress→pixel < ~33 ms (beat conhost)                 | Windows-only | not measurable (no window/ConPTY)                                                                                                                                                                              |
| `cat` 100k lines, beat WT ~5 s, ConPTY-capped ~2 MiB/s | Windows-only | parse+frame **~53 MiB/s, ~900k lines/s** — ~25× the ~2 MiB/s ConPTY cap, so `cat` is ConPTY-bound, not parser-bound                                                                                            |
| 120 FPS under flood, no input-latency spikes           | Windows-only | headless flood (full-damage every frame), 120×40 / 1441×960px: **~95–130 FPS on a CPU rasterizer** (llvmpipe); a real GPU draws this in sub-ms                                                                 |
| idle CPU ~0                                            | Windows-only | event loop is `ControlFlow::Wait` + redraw only on PTY data; incremental frame re-uploads one row. (llvmpipe idle≈flood because it repaints the whole framebuffer regardless; GPU hardware would show the gap) |

Reproduce the proxies: `cargo run -p app --release -- bench`.

### Deviations / ceilings (Phase 2)

- wgpu drives the DX12 flip-model swap chain + frame latency internally and does
  not expose the raw DXGI `FRAME_LATENCY_WAITABLE_OBJECT`; low latency is via
  `Immediate` + `desired_maximum_frame_latency = 1`. Reaching the literal
  waitable handle would require bypassing wgpu through wgpu-hal — not done.
- Per-cell slot budget is 3: a cell that is simultaneously the bar/underline
  cursor **and** carries a text underline shows the cursor (cursor wins slot 2).
- Selection is character-granularity (`SelectionType::Simple`); word/line and
  block selection not wired. Selection change repaints fully (alacritty damage
  excludes selection).
- The four end-to-end DoD numbers remain **unmeasured** pending a Windows host
  (same tier-3 constraint as Phase 1's functional DoD).

## Phase 3 — Native multi-session terminal with native Win32 vertical tabs

### Done

- Dropped `winit` + the empty `ui` crate dep from `app`; the app now owns its
  own Win32 window + blocking `GetMessageW` loop (`crates/app/src/win32.rs`).
- `term-core`: captures OSC window titles (`Event::Title`/`ResetTitle`) into a
  shared `Arc<Mutex<Option<String>>>`, exposed via `Terminal::title()`. Only
  additive change; existing tests untouched (10 pass).
- Native chrome: real `SysTabControl32` vertical strip (`TCS_VERTICAL`) on the
  left, `+`/`x` push buttons, OS dark caption via
  `DWMWA_USE_IMMERSIVE_DARK_MODE` (real min/max/close + drag kept). Render
  surface and strip are non-overlapping sibling child HWNDs with a main-window
  splitter gap between them.
- N independent ConPTY shells, one per tab (`pty_win::spawn` per tab). Reader
  threads append to a per-session inbox and wake the loop with `PostMessageW`
  (`MSG_PTY_DATA`/`MSG_PTY_EOF`, `wParam` = stable session id). Background tabs
  keep draining + parsing; only the active tab repaints (forced full upload on
  switch via `Frame.damage = Full`, no renderer change).
- Live OSC-driven per-tab titles; per-monitor-V2 DPI (`WM_DPICHANGED` rebuilds
  font + renderer + image list and relays out). Collapse to icons-only
  (Ctrl+Shift+B) and a draggable splitter, both persisted to
  `%APPDATA%\velo\ui.cfg`.
- Keybinds: Ctrl+Shift+T new, Ctrl+Shift+W close, Ctrl+Tab / Ctrl+Shift+Tab
  switch, Ctrl+Shift+B collapse. Shift on T/W/B keeps plain Ctrl+letter control
  codes reaching the shell. Mouse selection handled in the render child
  (`WM_MOUSEACTIVATE` → `MA_NOACTIVATE` so it never steals keyboard focus).
- Sessions keyed by a stable-id slot vector (`Vec<Option<Box<Session>>>`) so
  closing a tab never shifts ids under in-flight posted messages.

### Verified on this host

- `cargo build` (whole workspace) + `cargo test -p term-core` (10 pass) +
  `cargo clippy --all-targets` clean on Linux.
- `cargo check`/`cargo clippy --target x86_64-pc-windows-gnu` clean — typechecks
  the full `#[cfg(windows)]` `win32.rs`, every `windows` 0.62 call, and the wgpu
  raw-HWND surface path. This is the binding correctness gate here (same as
  Phases 1–2); the on-screen DoD is a Windows-host run (tier-3, cannot run on
  this Linux/ConPTY-less/llvmpipe box).

### Deviations from plan (Phase 3)

- Lock types use `parking_lot::Mutex` (workspace `rs-parking-lot` rule) instead
  of `std::sync::Mutex` for the title cell and per-session inbox, since both
  immediately unwrap the guard.
- `WM_COMMAND` handlers call `SetFocus(hwnd_main)` after a button click so the
  push buttons don't retain keyboard focus (not spelled out in the plan).
- `MoveWindow`'s repaint arg is `bool` (not `BOOL`) and `SendMessageW` takes
  `Option`-wrapped `wParam`/`lParam` in windows 0.62 — adapted at every call.

### Ceilings (Phase 3)

- The raw-pointer-in-`GWLP_USERDATA` `&mut App` idiom can briefly alias under
  re-entrant messages (e.g. `SetWindowPos` in `WM_DPICHANGED` dispatching a
  nested `WM_SIZE`); single-threaded on the message loop, standard Win32-in-Rust
  idiom, accepted.
- Collapsed tabs share one stock `IDI_APPLICATION` icon; per-shell icons would
  add an `HIMAGELIST` entry per tab (`iImage` is already set per item).
- Clipboard copy of a selection remains out of scope (future phase).

## Phase 4 — Hybrid Rust core + C# WinUI 3 shell

Native WinUI support in Rust is minimal, so the UI chrome moves to C#. The Rust
terminal core (term-core + pty-win + renderer + wgpu) stays; the Win32 shell is
replaced by a WinUI 3 shell with a proper collapsible vertical-tab pane
(NavigationView), a custom title bar, and Mica.

### Done

- New `crates/ffi` cdylib (`velo_core.dll`): a C ABI over the core. Reuses
  `renderer`/`term-core`/`pty-win`/`text` unchanged. Exposes
  `velo_attach`/`velo_set_callbacks`/`velo_tab_new`/`velo_tab_close`/
  `velo_tab_set_active`/`velo_set_scale`/`velo_paste_utf16`/`velo_shutdown`, plus
  a `#[repr(C)] VeloCallbacks` (title-changed, tab-closed, active-changed, and the
  new/close/switch tab-keybind requests).
- **Child-HWND airspace** seam: C# owns the window + chrome and creates one child
  HWND for the content region; the core renders wgpu into it via the existing
  `make_surface` (HWND path, unchanged) and **subclasses** it (`SetWindowSubclass`)
  for input / WM_PAINT / PTY wakeups. PTY readers `PostMessageW` to the content
  HWND; the WinUI dispatcher pumps them to the subclass proc.
- Tab-management chords (Ctrl+Shift+T/W, Ctrl+Tab) are detected in the subclass
  proc and **call back into C#** (the terminal child HWND holds raw Win32 focus,
  so WinUI accelerators won't fire there); C# stays the single source of truth for
  the tab list. Copy/paste + nav keys stay terminal-local in Rust.
- Old `crates/app/src/win32.rs` chrome (GDI sidebar, splitter, caption/Mica DWMWA,
  `ui.cfg`, the `GetMessageW` loop) **deleted**; `app` is now the `bench` harness
  only.
- C# WinUI 3 shell `csharp/Velo.App` (unpackaged, .NET 8, Windows App SDK 1.6):
  `ExtendsContentIntoTitleBar` + `MicaBackdrop` custom title bar; `NavigationView`
  vertical pane (collapse toggle, AddTabButton, per-item title + badge + close);
  `Interop.cs` P/Invoke (`LibraryImport`) + `[UnmanagedCallersOnly]` callbacks
  marshalled onto the dispatcher; child-HWND create/track-to-bounds + DPI via
  `XamlRoot.RasterizationScale` → `velo_set_scale`. MSBuild target copies
  `target/release/velo_core.dll` next to the exe.

### Verified on this host (Linux)

- `cargo check`/`cargo clippy --workspace --target x86_64-pc-windows-gnu` clean —
  full `#[cfg(windows)]` cdylib (every `windows` 0.62 call, the subclass proc, the
  raw-HWND wgpu surface). Same binding-correctness gate as Phases 1–3.
- `cargo build --workspace` clean on Linux (cdylib compiles to an empty lib off
  Windows; `app` bench unaffected).

### Pending (Windows host)

- `cargo build --release -p velo_core` (msvc) → `velo_core.dll`; `dotnet build
csharp/Velo.App` → run. Functional DoD: Mica + custom titlebar, `≡` pane
  collapse, AddTab spawns a shell, live OSC tab titles, tab switch, close button +
  shell-exit remove tabs, typing/output/selection/copy-paste, resize + DPI reflow,
  idle ~0% CPU. WinUI + ConPTY + GPU are all Windows-only — unbuildable here.

### Settings (Phase 4)

- C# settings dialog (gear in the title bar) for **font size**, **shell**,
  **terminal background color**, and **backdrop/blur kind**. Persisted to
  `%APPDATA%\velo\settings.json` (`Settings.cs`), applied on startup + on save.
- Core ABI added: `velo_set_font_size` (rebuilds font + renderer, reflows),
  `velo_set_shell` (future tabs), `velo_set_bg` (renderer surface clear color, new
  `Renderer::set_bg`).
- Backdrop kind {Mica, Mica Alt, Acrylic, None} via the built-in `SystemBackdrop`
  classes ("blur intensity" as discrete kinds).

### Blur-through via DirectComposition (Phase 4)

The terminal now composites with transparency so Mica/acrylic blurs through it
(matching the reference image), replacing the earlier opaque child-HWND swapchain.

- `velo_core` drops wgpu's opaque HWND `Surface`. `init_composition` makes a
  **DXGI composition swapchain** (`CreateSwapChainForComposition`,
  `R8G8B8A8_UNORM`, `DXGI_ALPHA_MODE_PREMULTIPLIED`, flip-discard, 2 buffers) on
  the **D3D12 command queue pulled out of wgpu** via
  `queue.as_hal::<Dx12>().as_raw()`. It binds the swapchain to an
  `IDCompositionVisual` on the content HWND (`CreateTargetForHwnd` + `SetContent`
  - `Commit`).
- Back buffers are imported into wgpu as textures
  (`wgpu::hal::dx12::Device::texture_from_raw` → `create_texture_from_hal`) wrapped
  as an `Rgba8UnormSrgb` view (sRGB RTV over the UNORM flip buffer). The renderer
  draws straight into the current back buffer; `render` calls
  `swapchain.Present`. Resize releases the wrapped textures, `device.poll(Wait)`,
  `ResizeBuffers`, re-wrap.
- Renderer switched to **premultiplied-alpha** output (blend +
  shader `rgb *= a`) and a settable **clear alpha** (`Renderer::set_bg_alpha`), so
  a translucent terminal background composites correctly. New ABI
  `velo_set_bg_alpha` (0 = transparent/full blur .. 1 = opaque).
- C# creates the content child HWND with **`WS_EX_NOREDIRECTIONBITMAP`** (required
  for DComp transparency), sets `TerminalHost.Background` transparent, and adds a
  **"Terminal background opacity"** slider to the settings dialog → `velo_set_bg_alpha`.
  Default opacity 1.0 (opaque); lower it to reveal the blur.

### Deviations / ceilings (Phase 4)

- Status badges (`TabVM.Badge`) are plumbed through but always idle (""); the badge
  _source_ (OSC 9/777 or a shell hook) is a follow-up.
- sRGB-RTV-over-UNORM relies on the DXGI flip-model allowance; if a driver rejects
  it the fallback is to drop the sRGB view format and sRGB-encode in the shader.
- Continuous backdrop tint-opacity (a chrome blur slider) still needs a manual
  `DesktopAcrylicController`; backdrop kind stays discrete {Mica, Mica Alt,
  Acrylic, None}. Terminal blur is now continuous via the opacity slider.
- Airspace limitation remains for _opaque_ overlays: XAML still can't paint over
  the terminal region (the DComp visual owns those pixels); chrome surrounds it.
- The MSBuild copy assumes a `--release` (msvc) core build at
  `target/release/velo_core.dll`; switch the path for a debug/cross build.

## UI_PLAN Phase 3 — Shell integration (cwd + command marks)

### Done

- **Split view blocked** (typing fix): the drag-to-split entry points are removed
  (`PaneHost` `AllowDrop`/drag handlers + `TabList` `CanDragItems`/drag handlers
  in `MainWindow.xaml`). The app is pinned to a single pane, so `_focusedPane`
  stays 0 and input always routes through the verified pane-0 path. The split
  C#/core machinery stays in place but is now unreachable (dead code).
- **Rust `term-core`**: an `OscTee` tees the PTY byte stream (read-only, carries
  state across reads) and decodes OSC 7 (cwd) + OSC 133 A/C/D into a
  `ShellEvent` queue, drained via `take_shell_events()`. Duration is the
  Instant delta between C and D. 2 new unit tests (12 pass).
- **Rust `ffi`**: `VeloCallbacks` gains `on_cwd_changed` + `on_command`; the
  `on_pty_data` drain forwards each decoded event to C# with the session id
  (UTF-16 marshalled like titles).
- **C#**: `TabVM` gains `Cwd`, `RunningCommand`, and `ObservableCollection<CommandEntry>`;
  `OnCwdChanged`/`OnCommand` `[UnmanagedCallersOnly]` handlers update the tab on
  the dispatcher. `ShellIntegration.cs` idempotently injects marker-fenced OSC
  7/133 emitters into the PowerShell (`$PROFILE`) or bash (`~/.bashrc`) profile
  on startup, gated by `Settings.ShellIntegration` + `VELO_SHELL_INTEGRATION=0`.

### Verified on this host (Linux)

- `cargo test -p term-core` (12 pass), `cargo build -p velo_core`,
  `cargo clippy -p velo_core --target x86_64-pc-windows-gnu` clean.
- C# is WinUI/Windows-only (no `dotnet` here) — same tier-3 gate as Phases 1–4;
  needs a Windows run to confirm `cd` updates `TabVM.Cwd` and commands record
  start/exit/duration.

### Ceilings (Phase 3)

- PowerShell command-text (OSC 133 C) needs PSReadLine's Enter handler; without
  PSReadLine, prompt-driven cwd + A/D marks still work but command text is empty.
- bash DEBUG trap can emit a stray C for PROMPT_COMMAND's own commands.

## Future phases

- Scrollback, word/line/block selection, configurable font size +
  per-tab/configurable shell, real per-tab status badges.
