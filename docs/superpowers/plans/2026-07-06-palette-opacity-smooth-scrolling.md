# Palette Opacity Fix + Smooth GPU Scrolling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Command palette follows the app opacity setting, and terminal content glides pixel-smooth (kitty/neovide style) during streaming output and wheel scrollback instead of jumping whole rows.

**Architecture:** term-core's `Frame` reports how many rows the viewport content moved since the last frame (`scrolled_up`). The renderer keeps a per-pane pixel offset that gets bumped by that amount and decays to zero (exponential ease); the offset is applied in the vertex shader via the existing screen uniform's spare floats — no instance rebuilds. The ffi engine exposes `velo_tick(dt_ms)` and a new `on_anim` callback; C# hooks `CompositionTarget.Rendering` only while an animation is in flight.

**Tech Stack:** Rust (alacritty_terminal vendored, wgpu, WGSL), C# WinUI3 (`LibraryImport` P/Invoke).

## Global Constraints

- Tests must run on Linux: `cargo test -p term-core -p renderer` (ffi is Windows-only — verify with `cargo check --target x86_64-pc-windows-gnu -p velo_core`).
- No new dependencies.
- Follow existing code style: `///` doc comments explaining *why*, `ponytail:` comments for deliberate ceilings.
- ABI changes must be mirrored exactly between `crates/ffi/src/lib.rs` and `csharp/Velo.App/Interop.cs` (append-only on `VeloCallbacks`).
- Commit messages end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Command palette follows app opacity

**Files:**
- Modify: `csharp/Velo.App/MainWindow.xaml:886-895` (palette card Border)
- Modify: `csharp/Velo.App/MainWindow.xaml.cs:2397-2405` (`UpdatePanelTint`)

**Interfaces:**
- Consumes: `_settings.BackgroundRgb()`, `_settings.BackgroundOpacity` (existing).
- Produces: nothing new — visual fix only.

- [ ] **Step 1: Name the palette card and drop its hardcoded background**

In `MainWindow.xaml`, the palette card is the `Border` inside `PaletteOverlay` (line ~886). Change:

```xml
            <Border Width="560"
                    VerticalAlignment="Center"
                    HorizontalAlignment="Center"
                    Margin="0,-48,0,0"
                    Padding="8"
                    Background="#F21D1F23"
```

to:

```xml
            <Border x:Name="PaletteCard"
                    Width="560"
                    VerticalAlignment="Center"
                    HorizontalAlignment="Center"
                    Margin="0,-48,0,0"
                    Padding="8"
```

(Keep `BorderBrush`, `BorderThickness`, `CornerRadius`, `Tapped` as they are. The background now comes from code so it always matches the app tint.)

- [ ] **Step 2: Drive the card from the same surface brush as the panels**

In `MainWindow.xaml.cs`, `UpdatePanelTint()` (line ~2397) currently ends with:

```csharp
        TitleBar.Background = surface;
        SidebarSurface.Background = surface;
        DetailsSurface.Background = surface;
```

Add the palette card, slightly more opaque than the chrome so the list stays readable over the dim overlay, but pinned to the same color and following the user's opacity (at opacity 1.0 it is fully opaque):

```csharp
        TitleBar.Background = surface;
        SidebarSurface.Background = surface;
        DetailsSurface.Background = surface;
        // Palette card: same tint as the chrome, but floored so text stays
        // readable over the dim overlay at very low opacity settings.
        byte pa = (byte)Math.Round(Math.Max(_settings.BackgroundOpacity, 0.85) * 255);
        PaletteCard.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(pa, r, g, b));
```

- [ ] **Step 3: Verify it compiles (no test runner for XAML)**

Run: `cargo check -p velo_core 2>/dev/null; echo "C# builds only on Windows — visual verify: palette at opacity 100 must be fully opaque, at 50 translucent like the sidebar"`

C# cannot build on this Linux box; flag for Windows manual verify in the task report.

- [ ] **Step 4: Commit**

```bash
git add csharp/Velo.App/MainWindow.xaml csharp/Velo.App/MainWindow.xaml.cs
git commit -m "fix: command palette follows the app background tint + opacity

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: term-core — `Frame.scrolled_up`

**Files:**
- Modify: `crates/term-core/src/lib.rs` (struct `Terminal` fields ~line 206-241, `resize` ~line 320, `frame()` ~line 423-540, `pub struct Frame` ~line 544, tests module ~line 570)

**Interfaces:**
- Consumes: vendored alacritty `self.term.grid().history_size()`, existing `display_offset()`, `TermMode::ALT_SCREEN`.
- Produces: `Frame.scrolled_up: i32` — rows the viewport content moved **up** since the last `frame()` call (positive = content moved up, e.g. new output; negative = moved down, e.g. wheel scroll back; 0 on/around the alt screen). Task 4 consumes this exact field name.

- [ ] **Step 1: Write the failing tests**

Append to the existing `mod tests` in `crates/term-core/src/lib.rs` (it already has `fn term()` building a 4x2 terminal):

```rust
    #[test]
    fn scrolled_up_counts_lines_pushed_to_history() {
        let mut t = term(); // 4 cols x 2 rows
        let _ = t.frame(); // settle initial state
        // 4 newlines on a 2-row screen -> lines pushed into history.
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let f = t.frame();
        assert!(f.scrolled_up > 0, "expected positive shift, got {}", f.scrolled_up);
        // Second frame with no new data: no motion.
        assert_eq!(t.frame().scrolled_up, 0);
    }

    #[test]
    fn scrolled_up_negative_on_scrollback() {
        let mut t = term();
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let _ = t.frame();
        t.scroll_display(2); // toward history -> content moves down
        assert_eq!(t.frame().scrolled_up, -2);
        t.scroll_to_bottom(); // back to present -> content moves up
        assert_eq!(t.frame().scrolled_up, 2);
    }

    #[test]
    fn scrolled_up_zero_on_alt_screen() {
        let mut t = term();
        let _ = t.frame();
        t.advance(b"\x1b[?1049h"); // enter alt screen
        t.advance(b"x\r\ny\r\nz\r\nw");
        assert_eq!(t.frame().scrolled_up, 0);
        t.advance(b"\x1b[?1049l"); // leave alt screen: transition frame also 0
        assert_eq!(t.frame().scrolled_up, 0);
    }

    #[test]
    fn scrolled_up_zero_after_resize() {
        let mut t = term();
        t.advance(b"a\r\nb\r\nc\r\nd\r\ne");
        let _ = t.frame();
        t.resize(6, 3); // reflow may change history size; must not read as motion
        assert_eq!(t.frame().scrolled_up, 0);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cargo test -p term-core scrolled_up`
Expected: FAIL to compile — `Frame` has no field `scrolled_up`.

- [ ] **Step 3: Implement**

3a. Add to `pub struct Frame` (after `pub damage: FrameDamage,`):

```rust
    /// Rows the viewport content moved *up* since the last frame (new output
    /// scrolls content up; scrolling back into history moves it down =
    /// negative). 0 on the alt screen and across alt-screen transitions.
    /// Drives the renderer's smooth-scroll offset.
    pub scrolled_up: i32,
```

3b. Add fields to `struct Terminal` next to `last_display_offset: usize` (~line 209):

```rust
    /// History length at the last frame, to derive content motion.
    last_history: usize,
    /// Whether the last frame was on the alt screen (suppresses the motion
    /// signal across screen switches, where history_size jumps to/from 0).
    last_alt: bool,
```

and to the initializer next to `last_display_offset: 0,` (~line 241):

```rust
            last_history: 0,
            last_alt: false,
```

3c. In `resize()` (~line 320), after the existing resize call, re-baseline so a reflowed history doesn't read as motion:

```rust
        self.last_history = self.term.grid().history_size();
        self.last_display_offset = self.term.grid().display_offset();
```

3d. In `frame()`: after `let display_offset = content.display_offset;` (~line 440) the borrow of `content` is still live — so compute the inputs *before* `renderable_content()` instead. Right at the top of `frame()` (after `let rows = ...`):

```rust
        let history = self.term.grid().history_size();
        let alt = self.term.mode().contains(TermMode::ALT_SCREEN);
```

Then, where `last_selection`/`last_display_offset` are written back at the end of `frame()` (~line 521), compute and store:

```rust
        // Content motion = change of (history above viewport top). New lines
        // grow history (content up); scrolling back grows the offset (down);
        // lines arriving while scrolled back grow both and cancel out.
        let scrolled_up = if alt || self.last_alt {
            0
        } else {
            ((history as i64 - display_offset as i64)
                - (self.last_history as i64 - self.last_display_offset as i64))
                .clamp(i32::MIN as i64, i32::MAX as i64) as i32
        };
        self.last_selection = selection;
        self.last_display_offset = display_offset;
        self.last_history = history;
        self.last_alt = alt;
```

and set `scrolled_up` in the `Frame { ... }` constructor at the end of `frame()`.

3e. `renderer/src/lib.rs` has a test helper constructing `RenderCell` but tests may construct `Frame` elsewhere — grep for `Frame {` across crates and add `scrolled_up: 0` to any other literal constructions (ffi's tests, if any).

- [ ] **Step 4: Run tests**

Run: `cargo test -p term-core`
Expected: all pass, including the 4 new tests. If `history_size()` doesn't exist on the vendored grid, use `self.term.grid().history_size()`'s actual vendored equivalent (check `crates/term-core` vendored alacritty version's `Grid` API — `history_size()` is present in alacritty_terminal ≥ 0.20).

- [ ] **Step 5: Full-workspace check + commit**

Run: `cargo test -p term-core -p renderer -p text && cargo check --target x86_64-pc-windows-gnu -p velo_core`
(The windows check will fail to compile until Task 4 only if ffi constructs `Frame` literals — fix by adding `scrolled_up: 0` there too.)

```bash
git add crates/term-core/src/lib.rs
git commit -m "feat: Frame.scrolled_up reports viewport content motion per frame

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: renderer — smooth-scroll offset + easing

**Files:**
- Modify: `crates/renderer/src/lib.rs` (struct `Renderer` ~line 68-97, ctor ~line 312, `set_font_metrics` ~line 358, `draw()` ~line 698-746, `SHADER` ~line 751, new test module at end)

**Interfaces:**
- Consumes: nothing new.
- Produces (Task 4 uses these exact names):
  - `Renderer::scroll_bump(&mut self, rows_up: i32)` — content moved `rows_up` rows up; grid starts visually displaced by `rows_up * cell_h` px and eases back.
  - `Renderer::scroll_tick(&mut self, dt_ms: f32) -> bool` — advance the ease; returns `true` while still animating.
  - `Renderer::scroll_active(&self) -> bool`.

- [ ] **Step 1: Write the failing tests**

The easing math is pure — testable without a GPU. Append to `crates/renderer/src/lib.rs`:

```rust
#[cfg(test)]
mod scroll_anim_tests {
    // Test the pure state machine through a headless Renderer stand-in: the
    // fields + methods don't touch wgpu, so exercise them via a tiny struct
    // mirror is NOT needed — construct the logic through ScrollAnim directly.
    use super::ScrollAnim;

    #[test]
    fn bump_sets_offset_and_tick_decays_to_zero() {
        let mut a = ScrollAnim::default();
        a.bump(3, 20.0); // 3 rows up at 20px cells -> +60px
        assert_eq!(a.px, 60.0);
        let mut ticks = 0;
        while a.tick(16.0) {
            ticks += 1;
            assert!(ticks < 100, "must settle");
        }
        assert_eq!(a.px, 0.0);
        assert!(ticks > 2, "should take several frames, not snap");
    }

    #[test]
    fn bump_is_clamped() {
        let mut a = ScrollAnim::default();
        a.bump(500, 20.0); // cat flood: don't fly the grid off-screen
        assert!(a.px <= 3.0 * 20.0 + f32::EPSILON);
        a.bump(-500, 20.0);
        a.bump(-500, 20.0);
        assert!(a.px >= -(3.0 * 20.0) - f32::EPSILON);
    }

    #[test]
    fn opposite_bumps_cancel() {
        let mut a = ScrollAnim::default();
        a.bump(2, 20.0);
        a.bump(-2, 20.0);
        assert_eq!(a.px, 0.0);
        assert!(!a.tick(16.0));
    }

    #[test]
    fn decay_is_monotonic() {
        let mut a = ScrollAnim::default();
        a.bump(2, 20.0);
        let mut prev = a.px;
        while a.tick(16.0) {
            assert!(a.px < prev && a.px >= 0.0);
            prev = a.px;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cargo test -p renderer scroll_anim`
Expected: FAIL to compile — `ScrollAnim` not defined.

- [ ] **Step 3: Implement `ScrollAnim`**

Add near the top of `crates/renderer/src/lib.rs` (after the `Glyph` struct):

```rust
/// Smooth-scroll easing state: a pixel displacement applied to the whole grid
/// in the vertex shader, decaying exponentially to 0. Content that jumped N
/// rows starts drawn N*cell_h px from its final position and glides in.
#[derive(Default)]
pub struct ScrollAnim {
    /// Current visual y displacement in px (positive = grid drawn lower).
    pub px: f32,
}

/// Never displace more than this many cells — a `cat` flood should read as a
/// fast glide, not launch the grid off-screen.
const MAX_SCROLL_CELLS: f32 = 3.0;
/// Exponential decay time constant (ms); ~4*tau ≈ 200ms to visually settle.
const SCROLL_TAU_MS: f32 = 50.0;

impl ScrollAnim {
    /// Content moved `rows_up` rows up (negative = down) on a grid with
    /// `cell_h`-px rows: displace so it starts where it was and eases home.
    pub fn bump(&mut self, rows_up: i32, cell_h: f32) {
        let max = MAX_SCROLL_CELLS * cell_h;
        self.px = (self.px + rows_up as f32 * cell_h).clamp(-max, max);
    }

    /// Advance by `dt_ms`. Returns true while still moving.
    pub fn tick(&mut self, dt_ms: f32) -> bool {
        self.px *= (-dt_ms.max(0.0) / SCROLL_TAU_MS).exp();
        if self.px.abs() < 0.5 {
            self.px = 0.0;
        }
        self.px != 0.0
    }
}
```

- [ ] **Step 4: Run the anim tests**

Run: `cargo test -p renderer scroll_anim`
Expected: PASS (4 tests).

- [ ] **Step 5: Wire into Renderer + shader**

5a. Add field to `Renderer` (after `bg_a: f32,`):

```rust
    /// Smooth-scroll displacement state (applied in the vertex shader).
    scroll: ScrollAnim,
```

and `scroll: ScrollAnim::default(),` in `Renderer::new`'s `Self { ... }`.

5b. Public surface on `impl Renderer` (next to `set_bg_alpha`):

```rust
    /// Content moved `rows_up` rows since the last frame: start the glide.
    pub fn scroll_bump(&mut self, rows_up: i32) {
        self.scroll.bump(rows_up, self.cell_h);
    }

    /// Advance the glide. Returns true while a redraw is still needed.
    pub fn scroll_tick(&mut self, dt_ms: f32) -> bool {
        self.scroll.tick(dt_ms)
    }

    pub fn scroll_active(&self) -> bool {
        self.scroll.px != 0.0
    }
```

In `set_font_metrics`, add `self.scroll.px = 0.0;` (old displacement is in old-cell units; zoom mid-glide just snaps).

5c. In `draw()`, replace the uniform write (~line 698):

```rust
        queue.write_buffer(
            &self.uniform,
            0,
            bytemuck::cast_slice(&[screen_w, screen_h, 0.0, self.scroll.px]),
        );
```

5d. In the WGSL `SHADER`: change the uniform struct and vertex position:

```wgsl
struct Uniforms { screen: vec2<f32>, scroll: vec2<f32> };
```

and in `vs_main` replace `let pos_px = rect.xy + corner * rect.zw;` with:

```wgsl
    let pos_px = rect.xy + corner * rect.zw + vec2<f32>(0.0, u.scroll.y);
```

5e. Clip the glide to the grid area so rows don't slide over the inner padding (clear still fills everything — `LoadOp::Clear` ignores scissor). Inside the render pass, in the `if total_instances > 0` block, before `pass.draw`:

```rust
                if self.scroll.px != 0.0 {
                    let py = self.pad_y.max(0.0) as u32;
                    let h = (screen_h as u32).saturating_sub(py * 2);
                    if h > 0 && py * 2 < screen_h as u32 {
                        pass.set_scissor_rect(0, py, screen_w as u32, h);
                    }
                }
```

- [ ] **Step 6: Run all renderer tests + commit**

Run: `cargo test -p renderer && cargo check -p renderer`
Expected: PASS (row_ranges + scroll_anim suites).

```bash
git add crates/renderer/src/lib.rs
git commit -m "feat: renderer smooth-scroll displacement with eased decay

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: ffi — bump wiring, `velo_tick`, `on_anim` callback

**Files:**
- Modify: `crates/ffi/src/lib.rs` (`VeloCallbacks` ~line 98-140, `render_pane` ~line 514, `on_pty_data` ~line 908, `pane_scroll` ~line 1290-1310, exports section ~line 1700+)

**Interfaces:**
- Consumes: `Frame.scrolled_up` (Task 2), `Renderer::scroll_bump/scroll_tick/scroll_active` (Task 3).
- Produces (Task 5 uses these):
  - ABI export `velo_tick(eng: *mut Engine, dt_ms: f32) -> i32` — advances all pane glides + re-renders; returns 1 while any pane still animates, else 0.
  - `VeloCallbacks.on_anim: Option<extern "C" fn(*mut c_void)>` — appended as the LAST struct field; fired (on the UI thread, like every other callback) when a glide starts, so C# can hook its frame pump.

- [ ] **Step 1: Append the callback field**

In `pub struct VeloCallbacks` after `on_command`:

```rust
        /// (ctx) — a smooth-scroll glide started; the shell should drive
        /// `velo_tick` from its compositor frame callback until it returns 0.
        pub on_anim: Option<extern "C" fn(*mut c_void)>,
```

and `on_anim: None,` in `impl Default`.

- [ ] **Step 2: Bump the glide in `render_pane` and notify**

In `render_pane` (~line 514), after the `if pane.force_full { ... }` block and before the backbuffer fetch, add:

```rust
            if frame.scrolled_up != 0 {
                pane.renderer.scroll_bump(frame.scrolled_up);
            }
```

At the very end of `render_pane` (after the `Present`), capture whether a glide is live and notify (the pane borrow has ended by then):

```rust
            let animating = matches!(self.panes.get(idx), Some(Some(p)) if p.renderer.scroll_active());
            if animating {
                if let Some(f) = self.cb.on_anim {
                    f(self.cb.ctx);
                }
            }
```

(C#'s handler is idempotent — hooking an already-hooked pump is a no-op — so no debounce state is needed here.)

- [ ] **Step 3: Engine::tick + export**

Add to `impl Engine` (near `render_all`):

```rust
        /// Advance every pane's smooth-scroll glide by `dt_ms` and repaint the
        /// ones that moved. Returns true while any pane still needs frames.
        fn tick(&mut self, dt_ms: f32) -> bool {
            let mut any = false;
            for i in 0..self.panes.len() {
                let moved = match self.panes.get_mut(i) {
                    Some(Some(p)) if p.renderer.scroll_active() => {
                        p.renderer.scroll_tick(dt_ms);
                        true
                    }
                    _ => false,
                };
                if moved {
                    self.render_pane(i);
                    if matches!(self.panes.get(i), Some(Some(p)) if p.renderer.scroll_active()) {
                        any = true;
                    }
                }
            }
            any
        }
```

Note: `render_pane` inside `tick` calls `terminal.frame()` again; a glide frame normally has no damage so it only rewrites the uniform and re-submits — cheap. It can also *re-bump* if new PTY data landed between ticks, which is exactly the desired flood behavior.

Add the export next to `velo_pane_scroll` (~line 1721):

```rust
    /// Advance smooth-scroll animations by `dt_ms` and repaint animating panes.
    /// Returns 1 while more frames are needed, 0 when everything settled.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tick(eng: *mut Engine, dt_ms: f32) -> i32 {
        match eng.as_mut() {
            Some(e) => e.tick(dt_ms) as i32,
            None => 0,
        }
    }
```

- [ ] **Step 4: Check it compiles for Windows**

Run: `cargo check --target x86_64-pc-windows-gnu -p velo_core && cargo test -p term-core -p renderer -p text`
Expected: clean check, all tests pass. Fix any `Frame { ... }` literal in ffi missing `scrolled_up`.

- [ ] **Step 5: Commit**

```bash
git add crates/ffi/src/lib.rs
git commit -m "feat: velo_tick + on_anim callback drive smooth-scroll glides

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: C# — frame pump + ABI mirror

**Files:**
- Modify: `csharp/Velo.App/Interop.cs` (`VeloCallbacks` struct, new `velo_tick` import)
- Modify: `csharp/Velo.App/MainWindow.xaml.cs` (callback registration site — grep `velo_set_callbacks` / `OnCommand` for the existing pattern; new pump methods)

**Interfaces:**
- Consumes: `velo_tick`, `VeloCallbacks.on_anim` (Task 4).
- Produces: nothing further — end of the chain.

- [ ] **Step 1: Mirror the ABI**

In `Interop.cs`, append to `struct VeloCallbacks` (order must match Rust — last field):

```csharp
        public IntPtr OnAnim;             // (ctx) — start driving velo_tick
```

Add the import (next to `velo_render`):

```csharp
    /// Advance smooth-scroll animations; returns 1 while more frames are needed.
    [LibraryImport(Core)]
    internal static partial int velo_tick(IntPtr eng, float dtMs);
```

- [ ] **Step 2: Register the callback**

In `MainWindow.xaml.cs`, find where `Native.VeloCallbacks` is populated (grep `OnCommand =` — every callback is a `[UnmanagedCallersOnly]` static resolving the window from the GCHandle ctx). Mirror that pattern exactly:

```csharp
    [UnmanagedCallersOnly]
    private static void OnAnimCb(void* ctx)
    {
        if (FromCtx(ctx) is { } w)   // use the SAME ctx->instance helper the other callbacks use
            w.StartAnimPump();
    }
```

and add to the struct population: `OnAnim = (IntPtr)(delegate* unmanaged<void*, void>)&OnAnimCb,`
(Adapt the resolve-instance idiom to whatever the neighboring callbacks actually do — copy it verbatim from `OnActiveChanged`'s handler.)

- [ ] **Step 3: The pump**

Add to `MainWindow`:

```csharp
    private bool _animPumping;
    private long _animLastTicks;

    /// Drive velo_tick from the compositor while a scroll glide is live.
    /// Idempotent: the Rust side fires on_anim on every animating render.
    private void StartAnimPump()
    {
        if (_animPumping) return;
        _animPumping = true;
        _animLastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += AnimPump_Rendering;
    }

    private void AnimPump_Rendering(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dtMs = (float)((now - _animLastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        _animLastTicks = now;
        if (Native.velo_tick(_engine, dtMs) == 0)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= AnimPump_Rendering;
            _animPumping = false;
        }
    }
```

(Match the file's existing `using` style — it likely already imports `Microsoft.UI.Xaml.Media`; drop the qualifiers if so. `_engine` is the existing engine handle field — confirm its actual name at the registration site.)

- [ ] **Step 4: Verify + commit**

Run: `cargo check --target x86_64-pc-windows-gnu -p velo_core`
C# builds only on Windows. Windows manual verify (user): `type` a large file and run `claude` — output glides instead of stepping; wheel scrollback glides; vim/htop (alt screen) unaffected; zoom mid-glide doesn't misplace rows; palette opacity follows the slider.

```bash
git add csharp/Velo.App/Interop.cs csharp/Velo.App/MainWindow.xaml.cs
git commit -m "feat: C# compositor pump drives smooth-scroll glides via velo_tick

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
