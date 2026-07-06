# Velo — Bug-fix round: zoom bounce, scroll stutter, emoji, editor polish

Approved design (brainstormed 2026-07-07). Phase A of three: A = these fixes,
B = terminal features (ligatures, rich styles, images, links, text editing),
C = push notifications. B and C get their own specs later.

## A1. Zoom/resize glide bounce

Symptom: Ctrl+± zoom (or window resize) makes the terminal body dip and
glide back up — a "bounce".

Cause: font-size change → grid resize → ConPTY asynchronously repaints the
screen. term-core re-baselines `last_history` inside its own `resize()`, but
the shell's redraw lands several frames later; the history delta reads as
content motion → `scroll_bump` starts a glide.

Fix: per-session glide mute. `reflow_session` / `set_font_size` stamp
`glide_mute_until = now + 300ms`; `render_pane` skips `scroll_bump` while
muted. Wheel scrollback while muted still works (it goes through the same
bump path — acceptable: 300ms window, imperceptible).

## A2. Smooth-scroll stutter (wheel scrollback)

Symptom: scrolling back through history sometimes glides, sometimes jumps.

Cause: `ScrollAnim::bump` clamps accumulated displacement to
`MAX_SCROLL_CELLS = 3` cells. One wheel notch = 3 rows — the first notch
saturates the clamp; motion from further notches inside the decay window is
dropped, so content teleports (no glide) until the animation drains.

Fix (renderer):
- Raise clamp: `min(viewport_rows, 12)` cells — needs the current row count,
  so clamp uses a `max_px` computed from grid rows at bump time.
- Tau 50ms → 65ms so successive notches merge into one continuous glide.
- Values are feel-tuned constants; user verifies on Windows and we adjust.

## A3. Emoji paste / backspace artifacts

Symptom: pasting 😀 shows emoji + gap; backspace leaves `�`.

Findings: paste path (UTF-8, bracketed, surrogate-aware) is correct. The
gap is the wide-char's second column — correct for 2-col glyphs. The `�`
is PSReadLine echoing a broken surrogate half after backspace (known
PSReadLine/ConPTY behavior, shell-side).

Scope for velo:
- Verify wide-char handling in term-core frame extraction: spacer cells
  must not emit glyphs; leading wide cell renders the emoji; cursor lands
  after the spacer. Fix anything wrong there.
- Verify renderer doesn't double-draw or mis-place wide glyphs.
- If the backspace `�` still reproduces identically in Windows Terminal,
  document as PSReadLine issue — no hack in velo.

## A4. Editor polish

### Tab-strip blend
`EditorTabs` ListView uses stock WinUI item brushes → light pills that
ignore the terminal theme. Fix: `EditorHost` + tab strip take the same
surface tint as the chrome (`UpdatePanelTint` brush); tab foreground uses
theme fg; selected tab = slightly lighter/darker variant of the surface
tint; close button/dirty dot inherit.

### Live theme for open files
`Highlighter::lines` caches spans with resolved RGB, keyed by `Document::rev`
only — theme change never invalidates. Fix: `term_core::palette` gains a
global epoch counter bumped by `set_theme`; highlight cache key becomes
`(rev, epoch)`. Open files recolor on the next repaint (engine already
repaints all panes on theme change).

### Ctrl+W closes editor tab
In `EditorPanel_KeyDown`: Ctrl+W → close focused file (same path as tab ×
button, save-on-close semantics preserved). Editor mode only.

## Verification

- `cargo test` workspace (new tests: glide mute after resize; bump clamp
  respects viewport; highlight cache invalidates on palette epoch).
- `cargo check --target x86_64-pc-windows-gnu`.
- Windows manual (user): zoom in/out — no bounce; wheel through a long
  Claude session — continuous glide; paste emoji + backspace in pwsh and
  Windows Terminal comparison; theme swap with files open — instant recolor;
  Ctrl+W closes tab; tab strip matches theme.
