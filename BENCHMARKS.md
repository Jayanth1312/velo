# Velo performance benchmarks

Harness: `cargo run -p app --release -- bench` (`crates/app/src/bench.rs`).
Cross-platform micro-benchmarks of the hot path: ANSI parse + grid resolution
(term-core) and grid build + GPU encode/submit (renderer, headless).

Measurement host: WSL2 Linux, `wgpu` falls back to **llvmpipe (CPU raster)** —
the `[render]` FPS numbers measure CPU rasterization, not a real GPU, and are
only useful for relative CPU-side comparisons. End-to-end Windows numbers
(keypress-to-pixel, `cat` throughput via ConPTY, present FPS) must be measured
on a Windows host; see "Not measured here" below.

## Baseline (2026-07-17, commit 738e27f + bench instrumentation)

Grid 120x40, 100k colored lines (5.8 MiB) fed in 4 KiB chunks with a frame
resolve per chunk (mirrors the PTY drain loop):

| Metric | Baseline |
|---|---|
| parse+frame throughput | 24.6 MiB/s (235 ms total) |
| `advance` (parse) alone | 86 ms — 67.6 MiB/s |
| `frame()` (grid→cells) alone | 148 ms — 0.100 ms/frame |
| typing path (1 char + frame) | **0.121 ms/frame** |
| flood render, llvmpipe 1440x960 | 17.5 ms/frame |
| idle render, llvmpipe | 16.7 ms/frame |

Finding: `frame()` was 63% of the CPU pipeline. It materialized every viewport
cell on every call — including one-row-damage frames (typing, cursor blink) —
while the renderer then used only the dirty rows' cells.

## Fix 1 — damage-scoped frame materialization (commit 098d56d)

`Terminal::frame()` now emits cells only for damaged rows (±1 dilation for
tall-glyph bleed, moved from the renderer so there is one source of truth),
pre-sizes the cells Vec from the previous frame, and exposes
`request_full_frame()` for host-side invalidation (theme change, pane rebind).

| Metric | Before | After | Δ |
|---|---|---|---|
| typing path | 0.121 ms/frame | **0.022 ms/frame** | **5.5× faster** |
| flood parse+frame | 0.100 ms/frame | 0.106 ms/frame | unchanged (flood frames are full-damage by nature; delta is run noise) |
| tests | 86 pass | 86 pass | — |

## Fix 2 — blink tick repaints cursor row only (commit 22643be)

The 530 ms cursor-blink timer forced a full repaint of every pane: a full
frame resolve (0.106 ms vs 0.022 ms, measured above) plus a full instance
buffer re-upload (~2 MB at 120×40, more at larger grids) twice per second per
pane, at idle. Alacritty re-damages the cursor row every frame, so a plain
render already repaints exactly the cursor cell. That guarantee is pinned by
the `idle_frame_damage_always_contains_cursor_row` test.

Impact (derived from Fix 1 measurements): idle blink cost drops from a
full-grid resolve + ~2 MB upload to a 3-row resolve + ~100 KB upload per tick.

## Measured and left alone

- **`advance` (parser) at ~66 MiB/s**: isolating the OSC tee scan (temporary
  env-var bypass) moved `advance` from 88–98 ms to 77–95 ms — inside
  run-to-run noise. The time is alacritty's parser + grid writes (SGR-heavy
  truecolor input), upstream code. 66 MiB/s comfortably outruns ConPTY
  delivery rates; not a bottleneck.
- **Renderer llvmpipe FPS**: flood 57→57, idle 60→62 across fixes — CPU
  rasterizer bound, not meaningful. CPU-side renderer work (rebuild_row,
  write_buffer) is damage-scoped and was already sub-millisecond.

## Not measured here (needs a Windows host), ranked by estimated impact

1. **Flood render pacing** (`ffi::on_pty_data` → render per drain): during a
   `cat` flood the UI thread loops drain→parse→render→`Present(0)`;
   `DXGI_SWAP_EFFECT_FLIP_DISCARD` with sync interval 0 never blocks, so
   render rate is bounded only by CPU/GPU — likely hundreds of wasted frames/s
   beyond the ~60 Hz the compositor shows. Plan: coalesce renders to a minimum
   interval (~6–8 ms) inside `on_pty_data` (keep the parse per drain, defer
   the render via a short timer when the last present was <8 ms ago). Typing
   latency is unaffected (keystroke renders are far below the threshold).
   Measure with: renders/s counter + CPU% during `cat` of a 100 MB file.
2. **Ligature run cache churn** (`renderer::run`): cache lookup allocates
   `text.to_string()` and hits clone the whole `Vec<(f32, Option<Glyph>)>`
   per run per rebuilt row. Full-damage frames of symbol-heavy content
   (diffs, code) pay it per row per frame. Fix: raw-entry / hashbrown lookup
   by `&str` + `Rc<[..]>` values. Blocked on a Windows-host profile to prove
   it matters — llvmpipe swamps the signal here.
3. **Instance buffer draws all slots** including zeroed overscan
   (~42k instances at 120×40, grows with grid): trivial for a real GPU's
   vertex stage, but measurable on weak GPUs. Fix would need slot compaction;
   only worth it if a Windows GPU profile shows vertex-bound frames.
4. **Full-buffer upload per scroll bump** (`shift_slot_rows` sets
   `full_upload`): every wheel notch re-uploads the whole instance buffer
   (~2 MB at 120×40, ~8 MB at 300×80). Fine at 60 Hz on PCIe, but worth a
   dirty-range union if scroll CPU shows up in a Windows profile.

## Known non-perf issue found while auditing

- Two panes bound to the same session: `render_session` calls
  `terminal.frame()` once per pane, but alacritty's damage is consumed by the
  first call — the second pane only ever sees cursor-row damage and shows
  stale content until a full invalidation. Pre-existing (unchanged by Fix 1,
  which keeps cells and damage consistent with each other). Fix would be a
  damage broadcast (resolve once, render N panes from one Frame).

## Repro

```sh
cargo run -p app --release -- bench          # all numbers above
cargo test --workspace --release             # 87 tests
```

Caveat: the Rust `ffi` crate's Windows-only module (`#[cfg(windows)] mod imp`)
cannot be compile-checked on this Linux host (MSVC cross-check dies in
tree-sitter's C build; no MinGW installed). The two ffi hunks (Fix 1's
`request_full_frame` call, Fix 2's blink handler) need a Windows build to
confirm.
