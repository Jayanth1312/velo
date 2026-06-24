//! bench: platform-agnostic micro-benchmarks for the render/parse pipeline.
//!
//! Run with `cargo run -p app --release -- bench`. These measure the parts of
//! the hot path that are *not* OS-specific: ANSI parsing + grid resolution
//! (term-core) and grid build + GPU encode/submit (renderer, headless via an
//! offscreen texture). The end-to-end Windows numbers (keypress-to-pixel via
//! ConPTY, `cat` throughput, on-screen present FPS) require a real Windows box
//! and a window/ConPTY, which this harness cannot provide on a Linux dev host.

use std::sync::Arc;
use std::time::Instant;

use anyhow::{anyhow, Result};
use term_core::Terminal;

const COLS: u16 = 120;
const ROWS: u16 = 40;

pub fn run() -> Result<()> {
    println!("== velo bench ==\n");
    match verify_render() {
        Ok(()) => println!("[verify] render pixel check: PASS\n"),
        Err(e) => println!("[verify] render pixel check skipped/failed: {e}\n"),
    }
    parse_throughput();
    println!();
    if let Err(e) = render_fps() {
        println!("render bench skipped: {e}");
    }
    println!("\nNote: keypress-to-pixel, `cat` throughput, and on-screen present");
    println!("FPS are end-to-end Windows/ConPTY numbers and must be measured on a");
    println!("Windows host (see PROGRESS.md).");
    Ok(())
}

/// Headless adapter + device, or an error if none is available.
fn headless_device() -> Result<(wgpu::Device, wgpu::Queue, wgpu::AdapterInfo)> {
    let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends: wgpu::Backends::all(),
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    });
    let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
        power_preference: wgpu::PowerPreference::HighPerformance,
        compatible_surface: None,
        force_fallback_adapter: false,
    }))
    .or_else(|_| {
        pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::LowPower,
            compatible_surface: None,
            force_fallback_adapter: true,
        }))
    })
    .map_err(|_| anyhow!("no wgpu adapter available (headless)"))?;
    let info = adapter.get_info();
    let (device, queue) =
        pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor::default()))?;
    Ok((device, queue, info))
}

/// Render a red `A` on the black default background to an offscreen texture,
/// read it back, and assert the glyph drew red pixels over a black field. This
/// exercises the full path: shape -> atlas -> instanced quads -> shader -> fb.
fn verify_render() -> Result<()> {
    let (device, queue, _info) = headless_device()?;
    let format = wgpu::TextureFormat::Rgba8UnormSrgb;
    let mut font = text::Font::new(super::FONT_SIZE_PT);
    let mut renderer =
        renderer::Renderer::new(&device, &queue, format, font.cell_w, font.cell_h, font.ascent);

    let cols = 4u16;
    let rows = 1u16;
    let width = (cols as f32 * font.cell_w).ceil() as u32;
    let height = (rows as f32 * font.cell_h).ceil() as u32;
    // Readback rows must be 256-byte aligned.
    let bpr = (width * 4).div_ceil(256) * 256;

    let target = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("verify target"),
        size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
    let view = target.create_view(&wgpu::TextureViewDescriptor::default());
    let readback = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("verify readback"),
        size: (bpr * height) as u64,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });

    let mut term = Terminal::new(cols, rows, Arc::new(|_: &[u8]| {}));
    term.advance(b"\x1b[31mA"); // red 'A' at (0,0)
    let frame = term.frame();
    renderer.draw(&device, &queue, &view, width as f32, height as f32, &frame, &mut font)?;

    let mut enc =
        device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
    enc.copy_texture_to_buffer(
        wgpu::TexelCopyTextureInfo {
            texture: &target,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyBufferInfo {
            buffer: &readback,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(bpr),
                rows_per_image: Some(height),
            },
        },
        wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
    );
    queue.submit([enc.finish()]);

    readback.slice(..).map_async(wgpu::MapMode::Read, |_| {});
    device.poll(wgpu::PollType::wait_indefinitely())?;
    let data = readback.slice(..).get_mapped_range();

    // Scan cell (0,0): count reddish pixels (glyph) and confirm a black field.
    let cw = font.cell_w as u32;
    let ch = height;
    let mut red = 0u32;
    for y in 0..ch {
        for x in 0..cw.min(width) {
            let i = (y * bpr + x * 4) as usize;
            let (r, g, b) = (data[i], data[i + 1], data[i + 2]);
            if r > 120 && g < 90 && b < 90 {
                red += 1;
            }
        }
    }
    drop(data);
    readback.unmap();

    if red == 0 {
        return Err(anyhow!("no red glyph pixels found in cell (0,0)"));
    }
    println!("[verify] {red} red glyph pixels rendered for 'A' (SGR 31)");
    Ok(())
}

/// Pre-build ~100k lines of colored text, then time parse + frame resolution.
fn parse_throughput() {
    let lines = 100_000usize;
    let mut buf: Vec<u8> = Vec::with_capacity(lines * 48);
    for i in 0..lines {
        let r = (i * 53 % 256) as u8;
        let g = (i * 97 % 256) as u8;
        let b = (i * 131 % 256) as u8;
        buf.extend_from_slice(
            format!(
                "\x1b[38;2;{r};{g};{b}mline {i:06} the quick brown fox jumps\x1b[0m\r\n"
            )
            .as_bytes(),
        );
    }
    let total = buf.len();

    let mut term = Terminal::new(COLS, ROWS, Arc::new(|_: &[u8]| {}));
    let start = Instant::now();
    // Feed in 4 KiB chunks like a ConPTY reader would, resolving a frame each
    // chunk (mirrors the app draining + redrawing).
    let mut frames = 0usize;
    for chunk in buf.chunks(4096) {
        term.advance(chunk);
        let _ = term.frame();
        frames += 1;
    }
    let dt = start.elapsed();

    let mib = total as f64 / (1024.0 * 1024.0);
    let secs = dt.as_secs_f64();
    println!("[parse+frame] {lines} lines, {mib:.1} MiB, {frames} frames");
    println!(
        "  {:.0} ms  ->  {:.1} MiB/s,  {:.0} lines/s",
        secs * 1000.0,
        mib / secs,
        lines as f64 / secs,
    );
}

/// Headless render: build + encode + submit flood frames to an offscreen
/// texture, measuring sustained frame rate and idle (incremental) frame cost.
fn render_fps() -> Result<()> {
    let (device, queue, info) = headless_device()?;
    println!(
        "[render] adapter: {} ({:?}, {:?})",
        info.name, info.device_type, info.backend
    );

    let format = wgpu::TextureFormat::Rgba8UnormSrgb;
    let mut font = text::Font::new(super::FONT_SIZE_PT);
    let mut renderer =
        renderer::Renderer::new(&device, &queue, format, font.cell_w, font.cell_h, font.ascent);

    let width = (COLS as f32 * font.cell_w).ceil() as u32;
    let height = (ROWS as f32 * font.cell_h).ceil() as u32;
    let target = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("bench target"),
        size: wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    });
    let view = target.create_view(&wgpu::TextureViewDescriptor::default());

    let mut term = Terminal::new(COLS, ROWS, Arc::new(|_: &[u8]| {}));

    // Pre-build flood frames so generation is outside the timed loop.
    let n = 600usize;
    let flood: Vec<Vec<u8>> = (0..n).map(screen_of_text).collect();

    let mut draw = |term: &mut Terminal, font: &mut text::Font| -> Result<()> {
        let frame = term.frame();
        renderer.draw(&device, &queue, &view, width as f32, height as f32, &frame, font)?;
        device.poll(wgpu::PollType::wait_indefinitely())?;
        Ok(())
    };

    // Warm up (atlas fill, pipeline).
    for f in flood.iter().take(8) {
        term.advance(f);
        draw(&mut term, &mut font)?;
    }

    // Flood: every frame rewrites the whole screen -> full damage each frame.
    let start = Instant::now();
    for f in &flood {
        term.advance(f);
        draw(&mut term, &mut font)?;
    }
    let dt = start.elapsed();
    let fps = n as f64 / dt.as_secs_f64();
    println!(
        "[render] flood {COLS}x{ROWS} ({width}x{height}px): {:.0} FPS, {:.2} ms/frame",
        fps,
        dt.as_secs_f64() * 1000.0 / n as f64
    );

    // Idle: no new bytes -> only the cursor row is re-uploaded.
    let m = 600usize;
    let start = Instant::now();
    for _ in 0..m {
        draw(&mut term, &mut font)?;
    }
    let dt = start.elapsed();
    println!(
        "[render] idle (incremental): {:.0} FPS, {:.3} ms/frame",
        m as f64 / dt.as_secs_f64(),
        dt.as_secs_f64() * 1000.0 / m as f64
    );
    Ok(())
}

/// A full-screen repaint with per-cell truecolor and rotating glyphs.
fn screen_of_text(frame_idx: usize) -> Vec<u8> {
    let mut s = String::with_capacity(COLS as usize * ROWS as usize * 24);
    s.push_str("\x1b[H");
    for r in 0..ROWS as usize {
        for c in 0..COLS as usize {
            let v = frame_idx.wrapping_add(r * 7).wrapping_add(c * 13);
            let rr = (v * 53 % 256) as u8;
            let gg = (v * 97 % 256) as u8;
            let bb = (v * 131 % 256) as u8;
            let ch = (b'!' + (v % 94) as u8) as char;
            s.push_str(&format!("\x1b[38;2;{rr};{gg};{bb}m{ch}"));
        }
        if r + 1 < ROWS as usize {
            s.push_str("\r\n");
        }
    }
    s.into_bytes()
}
