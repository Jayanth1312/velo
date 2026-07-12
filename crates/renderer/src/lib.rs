//! renderer: wgpu glyph atlas + instanced-quad pipeline.
//!
//! The whole visible grid is drawn in a single draw call: every cell owns a
//! fixed run of [`SLOTS_PER_CELL`] instances (fill, glyph, underline,
//! strikeout) in a persistent instance buffer. Each frame only the *damaged rows* are rebuilt
//! and re-uploaded (`queue.write_buffer` on contiguous sub-ranges); unchanged
//! rows keep their GPU bytes. Glyphs are rasterized on demand into an RGBA atlas
//! (mask glyphs stored as white + coverage-in-alpha, color/emoji as straight
//! RGBA) via a shelf allocator; the cache key carries weight, slant, and a
//! sub-pixel x bin for crisp fractional positioning.

use std::collections::HashMap;

use anyhow::Result;
use term_core::{palette, CursorShape, Frame, FrameDamage, RenderCell, UnderlineKind};

const ATLAS_SIZE: u32 = 2048;
/// Instances per cell: 0 = background/selection/cursor-block fill, 1 = glyph,
/// 2 = underline, 3 = strikeout (also where the bar/underline cursor overlay
/// is drawn, so it doesn't collide with the text underline).
const SLOTS_PER_CELL: usize = 4;
/// Number of sub-pixel x bins (matches cosmic-text's `SubpixelBin`).
const SUBPX_BINS: u32 = 4;
/// UV of the reserved opaque white texel at atlas (0,0); used for solid quads.
const WHITE_UV: [f32; 4] = {
    let c = 0.5 / ATLAS_SIZE as f32;
    [c, c, c, c]
};

/// Instance kinds (the `kind` vertex attribute).
const KIND_SOLID: u32 = 0; // solid/mask: tint by `color`, coverage from atlas alpha
const KIND_COLOR: u32 = 1; // color glyph: use atlas rgba directly
const KIND_DOUBLE: u32 = 2; // double underline: two lines split by a gap
const KIND_CURLY: u32 = 3; // curly underline: sine-ish wave
const KIND_DOTTED: u32 = 4; // dotted underline: on/off dots along x
const KIND_DASHED: u32 = 5; // dashed underline: dashes along x

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct Instance {
    /// x, y, w, h in pixels.
    rect: [f32; 4],
    /// u0, v0, u1, v1.
    uv: [f32; 4],
    /// rgba, 0..1.
    color: [f32; 4],
    /// `KIND_SOLID`, `KIND_COLOR`, or a `KIND_DOUBLE`/`KIND_CURLY`/
    /// `KIND_DOTTED`/`KIND_DASHED` procedural decoration pattern.
    kind: u32,
    _pad: [u32; 3],
}

const ZERO_INSTANCE: Instance = Instance {
    rect: [0.0; 4],
    uv: WHITE_UV,
    color: [0.0; 4],
    kind: KIND_SOLID,
    _pad: [0; 3],
};

#[derive(Clone, Copy)]
struct Glyph {
    uv: [f32; 4],
    w: f32,
    h: f32,
    left: f32,
    top: f32,
    color: bool,
}

/// Glyph cache key: base char + zero-width attachments + bold + italic +
/// sub-pixel x bin.
type GlyphKey = (char, [char; 2], bool, bool, u8);

/// Symbol chars eligible for ligature run shaping. Letters/digits stay
/// per-cell (Otty-style: ligatures fire only on symbol runs).
fn is_lig_char(c: char) -> bool {
    matches!(
        c,
        '=' | '<'
            | '>'
            | '!'
            | '&'
            | '|'
            | '+'
            | '-'
            | '~'
            | '^'
            | '?'
            | ':'
            | '/'
            | '\\'
            | '*'
            | '%'
            | '#'
            | '.'
            | '_'
    )
}

/// Smooth-scroll easing: a pixel displacement applied to the whole grid in the
/// vertex shader, eased to 0 with a fixed-duration cubic-out. Fixed duration
/// (not decay) so every wheel notch settles at the same perceived speed;
/// each bump restarts the clock from the new displacement.
#[derive(Default)]
pub struct ScrollAnim {
    /// Current visual y displacement in px (positive = grid drawn lower).
    pub px: f32,
    /// Displacement when the current ease started.
    start_px: f32,
    /// Elapsed ms since the current ease started.
    t_ms: f32,
}

/// Extra slot rows kept above AND below the viewport. On a scroll bump the
/// departing edge rows shift into overscan and stay visible during the glide,
/// so no blank gap opens at the top/bottom. Displacement cap: bumps beyond this
/// snap via scroll_bump, now rare.
pub const OVERSCAN_ROWS: u16 = 24;
/// Glide duration per bump (ms).
const SCROLL_DUR_MS: f32 = 140.0;

/// Shift slot rows vertically by `rows_up` (positive = content moved up),
/// patching each instance's y so it keeps its on-screen position; vacated
/// rows are zeroed. This replays the grid scroll inside the instance buffer,
/// which is what lets departing rows glide through the overscan area.
fn shift_slot_rows(slots: &mut [Instance], row_slots: usize, rows_up: i32, cell_h: f32) {
    if row_slots == 0 || slots.is_empty() || rows_up == 0 {
        return;
    }
    let total_rows = slots.len() / row_slots;
    let n = rows_up.unsigned_abs() as usize;
    if n >= total_rows {
        slots.fill(ZERO_INSTANCE);
        return;
    }
    let dy = rows_up as f32 * cell_h;
    if rows_up > 0 {
        // Row r takes old row r+n, drawn n cells higher.
        slots.copy_within(n * row_slots.., 0);
        for s in &mut slots[..(total_rows - n) * row_slots] {
            s.rect[1] -= dy;
        }
        slots[(total_rows - n) * row_slots..].fill(ZERO_INSTANCE);
    } else {
        // Row r takes old row r-n, drawn n cells lower.
        slots.copy_within(..(total_rows - n) * row_slots, n * row_slots);
        for s in &mut slots[n * row_slots..] {
            s.rect[1] -= dy; // dy is negative: y grows
        }
        slots[..n * row_slots].fill(ZERO_INSTANCE);
    }
}

impl ScrollAnim {
    pub fn bump(&mut self, rows_up: i32, cell_h: f32, max_px: f32) {
        self.px = (self.px + rows_up as f32 * cell_h).clamp(-max_px, max_px);
        self.start_px = self.px;
        self.t_ms = 0.0;
    }

    /// Advance by `dt_ms`. Returns true while still moving.
    pub fn tick(&mut self, dt_ms: f32) -> bool {
        self.t_ms += dt_ms.max(0.0);
        let t = (self.t_ms / SCROLL_DUR_MS).min(1.0);
        let ease = 1.0 - (1.0 - t).powi(3); // cubic-out
        self.px = self.start_px * (1.0 - ease);
        if t >= 1.0 || self.px.abs() < 0.5 {
            self.px = 0.0;
        }
        self.px != 0.0
    }
}

pub struct Renderer {
    pipeline: wgpu::RenderPipeline,
    atlas: wgpu::Texture,
    bind_group: wgpu::BindGroup,
    uniform: wgpu::Buffer,
    instances: wgpu::Buffer,
    instance_cap: u64,
    /// CPU mirror of the instance buffer; one [`SLOTS_PER_CELL`] run per cell,
    /// row-major. Only damaged rows are rewritten + uploaded each frame.
    slots: Vec<Instance>,
    grid_cols: u16,
    grid_rows: u16,
    glyphs: HashMap<GlyphKey, Option<Glyph>>,
    /// Shaped symbol runs (programming ligatures): run text + style ->
    /// positioned glyphs (x offset from run origin, atlas glyph).
    runs: HashMap<(String, bool, bool), Vec<(f32, Option<Glyph>)>>,
    /// Shape consecutive symbol cells as one run so font ligatures apply.
    ligatures: bool,
    shelf_x: u32,
    shelf_y: u32,
    shelf_h: u32,
    cell_w: f32,
    cell_h: f32,
    ascent: f32,
    /// Inner padding (physical px) between the surface edge and the cell grid.
    /// The clear fills the whole surface with bg; cells/glyphs are inset by this,
    /// so the padding reads as part of the terminal, not a margin around it.
    pad_x: f32,
    pad_y: f32,
    /// Surface clear color (the "terminal background"). Defaults to
    /// `palette::DEFAULT_BG`; settable so the shell can match the chrome tint.
    bg: [u8; 3],
    /// Clear alpha (0 = fully transparent → blur shows through; 1 = opaque).
    bg_a: f32,
    /// Smooth-scroll displacement state (applied in the vertex shader).
    scroll: ScrollAnim,
    /// Upload the whole instance buffer next draw (set after a slot-row shift,
    /// an overscan wipe, or a metrics change — anything not row-damage-shaped).
    full_upload: bool,
}

fn srgb(c: [u8; 3]) -> [f32; 3] {
    [
        c[0] as f32 / 255.0,
        c[1] as f32 / 255.0,
        c[2] as f32 / 255.0,
    ]
}

/// sRGB-normalized [0,1] -> linear, for the surface clear color (the sRGB
/// surface re-encodes on write, so the clear value must be linear).
fn srgb_to_linear(s: f64) -> f64 {
    if s <= 0.04045 {
        s / 12.92
    } else {
        ((s + 0.055) / 1.055).powf(2.4)
    }
}

/// Per-row `[start, end)` ranges into a row-major-sorted `cells` slice, one
/// pass, indexed by row number. Rows with no cells get an empty `(i, i)`
/// range. `cells` must already be sorted by `row` ascending (true of
/// [`Frame::cells`], which comes from alacritty's row-ordered
/// `renderable_content`).
fn row_ranges(cells: &[RenderCell], rows: u16) -> Vec<(usize, usize)> {
    let mut ranges = vec![(0usize, 0usize); rows as usize];
    let mut current_row: u16 = 0;
    let mut i = 0;
    while i < cells.len() {
        let row = cells[i].row;
        if row as usize >= rows as usize {
            break;
        }
        while current_row < row {
            ranges[current_row as usize] = (i, i);
            current_row += 1;
        }
        let start = i;
        while i < cells.len() && cells[i].row == row {
            i += 1;
        }
        ranges[row as usize] = (start, i);
        current_row = row + 1;
    }
    while (current_row as usize) < rows as usize {
        ranges[current_row as usize] = (cells.len(), cells.len());
        current_row += 1;
    }
    ranges
}

impl Renderer {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        cell_w: f32,
        cell_h: f32,
        ascent: f32,
    ) -> Self {
        let atlas = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("glyph atlas"),
            size: wgpu::Extent3d {
                width: ATLAS_SIZE,
                height: ATLAS_SIZE,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        // Reserve texel (0,0) = opaque white for solid (background/cursor) quads.
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &atlas,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &[255u8, 255, 255, 255],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        let atlas_view = atlas.create_view(&wgpu::TextureViewDescriptor::default());
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("atlas sampler"),
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let uniform = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("screen uniform"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let instance_cap = 4096u64;
        let instances = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("instances"),
            size: instance_cap * std::mem::size_of::<Instance>() as u64,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let bind_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("renderer bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("renderer bg"),
            layout: &bind_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&atlas_view),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
            ],
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("cell shader"),
            source: wgpu::ShaderSource::Wgsl(SHADER.into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("renderer pl"),
            bind_group_layouts: &[Some(&bind_layout)],
            immediate_size: 0,
        });
        let instance_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<Instance>() as u64,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &wgpu::vertex_attr_array![
                0 => Float32x4, 1 => Float32x4, 2 => Float32x4, 3 => Uint32
            ],
        };
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("cell pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[instance_layout],
            },
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleStrip,
                ..Default::default()
            },
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    // Premultiplied output so the frame composites correctly
                    // over a DirectComposition transparent surface.
                    blend: Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });

        Self {
            pipeline,
            atlas,
            bind_group,
            uniform,
            instances,
            instance_cap,
            slots: Vec::new(),
            grid_cols: 0,
            grid_rows: 0,
            glyphs: HashMap::new(),
            runs: HashMap::new(),
            ligatures: true,
            shelf_x: 1, // texel 0 is reserved white
            shelf_y: 0,
            shelf_h: 1,
            cell_w,
            cell_h,
            ascent,
            pad_x: 0.0,
            pad_y: 0.0,
            bg: palette::DEFAULT_BG,
            bg_a: 1.0,
            scroll: ScrollAnim::default(),
            full_upload: false,
        }
    }

    /// Set the inner grid padding (physical px). Takes effect next frame (the
    /// caller forces a full repaint on change).
    pub fn set_pad(&mut self, pad_x: f32, pad_y: f32) {
        self.pad_x = pad_x.max(0.0).round();
        self.pad_y = pad_y.max(0.0).round();
    }

    /// Set the surface clear color (terminal background). Takes effect next frame
    /// (the caller forces a full repaint on change).
    pub fn set_bg(&mut self, bg: [u8; 3]) {
        self.bg = bg;
    }

    /// Set the background clear alpha (0 = transparent for blur-through .. 1 = opaque).
    pub fn set_bg_alpha(&mut self, a: f32) {
        self.bg_a = a.clamp(0.0, 1.0);
    }

    /// Content moved `rows_up` rows since the last frame: start the glide.
    /// The departing rows are shifted into overscan so they stay visible while
    /// the displacement eases home. A bump the overscan can't cover snaps.
    pub fn scroll_bump(&mut self, rows_up: i32) {
        let max_px = OVERSCAN_ROWS as f32 * self.cell_h;
        let new_px = self.scroll.px + rows_up as f32 * self.cell_h;
        if new_px.abs() > max_px {
            self.scroll_reset();
            return;
        }
        let row_slots = self.grid_cols as usize * SLOTS_PER_CELL;
        shift_slot_rows(&mut self.slots, row_slots, rows_up, self.cell_h);
        self.full_upload = true;
        self.scroll.bump(rows_up, self.cell_h, max_px);
    }

    /// Kill any live glide and wipe the overscan rows (they may hold content
    /// that no longer matches — e.g. after a resize repaint or a huge jump).
    /// Viewport rows are left alone; frame damage keeps them correct.
    pub fn scroll_reset(&mut self) {
        self.scroll.px = 0.0;
        let ov = OVERSCAN_ROWS as usize * self.grid_cols as usize * SLOTS_PER_CELL;
        let len = self.slots.len();
        if ov > 0 && len >= 2 * ov {
            self.slots[..ov].fill(ZERO_INSTANCE);
            self.slots[len - ov..].fill(ZERO_INSTANCE);
            self.full_upload = true;
        }
    }

    /// Advance the glide. Returns true while a redraw is still needed.
    pub fn scroll_tick(&mut self, dt_ms: f32) -> bool {
        self.scroll.tick(dt_ms)
    }

    pub fn scroll_active(&self) -> bool {
        self.scroll.px != 0.0
    }

    /// Adopt new cell metrics (font size/DPI change) without recreating the
    /// pipeline/atlas texture/buffers. Old glyphs are keyed to the old size, so
    /// the cache is dropped and the shelf allocator reset to reclaim the atlas
    /// space (stale pixels are simply overwritten as glyphs are re-rasterized).
    pub fn set_font_metrics(&mut self, cell_w: f32, cell_h: f32, ascent: f32) {
        self.cell_w = cell_w;
        self.cell_h = cell_h;
        self.ascent = ascent;
        // Old displacement/overscan is in old-cell units; zoom mid-glide snaps.
        self.scroll_reset();
        self.glyphs.clear();
        self.runs.clear();
        self.shelf_x = 1; // texel 0 is reserved white
        self.shelf_y = 0;
        self.shelf_h = 1;
    }

    /// Rasterize a (char, bold, italic, sub-pixel bin) into the atlas if absent.
    fn glyph(
        &mut self,
        queue: &wgpu::Queue,
        font: &mut text::Font,
        key: GlyphKey,
    ) -> Option<Glyph> {
        if let Some(g) = self.glyphs.get(&key) {
            return *g;
        }
        let (c, zw, bold, italic, xbin) = key;
        let offset = xbin as f32 / SUBPX_BINS as f32;
        let raster = if zw[0] == '\0' {
            font.rasterize(c, bold, italic, offset)
        } else {
            // Cluster: base + zero-width attachments (combining marks, VS16)
            // shaped as one unit.
            let mut s = String::with_capacity(12);
            s.push(c);
            for z in zw {
                if z != '\0' {
                    s.push(z);
                }
            }
            font.rasterize_str(&s, bold, italic, offset)
        };
        let g = raster.and_then(|r| self.alloc(queue, &r));
        self.glyphs.insert(key, g);
        g
    }

    /// Enable/disable ligature run shaping. Caller forces a full redraw.
    pub fn set_ligatures(&mut self, on: bool) {
        self.ligatures = on;
    }

    /// Shape a symbol run (so ligatures like `=>` collapse) and cache the
    /// positioned atlas glyphs under the run text + style.
    fn run(
        &mut self,
        queue: &wgpu::Queue,
        font: &mut text::Font,
        text: &str,
        bold: bool,
        italic: bool,
    ) -> Vec<(f32, Option<Glyph>)> {
        let key = (text.to_string(), bold, italic);
        if let Some(v) = self.runs.get(&key) {
            return v.clone();
        }
        let v: Vec<(f32, Option<Glyph>)> = font
            .shape_run(text, bold, italic)
            .into_iter()
            .map(|(gx, r)| (gx, self.alloc(queue, &r)))
            .collect();
        self.runs.insert(key, v.clone());
        v
    }

    /// Place a rasterized glyph into the RGBA atlas via the shelf allocator.
    fn alloc(&mut self, queue: &wgpu::Queue, r: &text::Raster) -> Option<Glyph> {
        if r.width > ATLAS_SIZE {
            return None;
        }
        if self.shelf_x + r.width > ATLAS_SIZE {
            self.shelf_x = 0;
            self.shelf_y += self.shelf_h;
            self.shelf_h = 0;
        }
        if self.shelf_y + r.height > ATLAS_SIZE {
            // ponytail: shelf allocator, 2048² atlas; ASCII+box+common emoji fit.
            // On overflow log+skip; grow/repack the atlas if exhaustion shows up.
            log::warn!("glyph atlas full; skipping glyph");
            return None;
        }
        let x = self.shelf_x;
        let y = self.shelf_y;
        self.shelf_x += r.width;
        if r.height > self.shelf_h {
            self.shelf_h = r.height;
        }

        // Atlas is RGBA: mask glyphs become white with coverage in alpha.
        let rgba: Vec<u8> = if r.color {
            r.coverage.clone()
        } else {
            let mut v = Vec::with_capacity(r.coverage.len() * 4);
            for &cov in &r.coverage {
                v.extend_from_slice(&[255, 255, 255, cov]);
            }
            v
        };

        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &self.atlas,
                mip_level: 0,
                origin: wgpu::Origin3d { x, y, z: 0 },
                aspect: wgpu::TextureAspect::All,
            },
            &rgba,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(r.width * 4),
                rows_per_image: Some(r.height),
            },
            wgpu::Extent3d {
                width: r.width,
                height: r.height,
                depth_or_array_layers: 1,
            },
        );

        let s = ATLAS_SIZE as f32;
        Some(Glyph {
            uv: [
                x as f32 / s,
                y as f32 / s,
                (x + r.width) as f32 / s,
                (y + r.height) as f32 / s,
            ],
            w: r.width as f32,
            h: r.height as f32,
            left: r.left as f32,
            top: r.top as f32,
            color: r.color,
        })
    }

    /// Index of the first slot of viewport cell (row, col). Slot rows are
    /// offset by [`OVERSCAN_ROWS`]: the buffer holds overscan rows above and
    /// below the viewport for gap-free scroll glides.
    #[inline]
    fn slot_base(&self, row: u16, col: u16) -> usize {
        ((row + OVERSCAN_ROWS) as usize * self.grid_cols as usize + col as usize) * SLOTS_PER_CELL
    }

    fn glyph_instance(&self, g: &Glyph, x: f32, y: f32, color: [u8; 3]) -> Instance {
        let (rgba, kind) = if g.color {
            ([1.0, 1.0, 1.0, 1.0], KIND_COLOR)
        } else {
            let c = srgb(color);
            ([c[0], c[1], c[2], 1.0], KIND_SOLID)
        };
        Instance {
            rect: [x, y, g.w, g.h],
            uv: g.uv,
            color: rgba,
            kind,
            _pad: [0; 3],
        }
    }

    fn solid(&self, x: f32, y: f32, w: f32, h: f32, color: [u8; 3]) -> Instance {
        let c = srgb(color);
        Instance {
            rect: [x, y, w, h],
            uv: WHITE_UV,
            color: [c[0], c[1], c[2], 1.0],
            kind: KIND_SOLID,
            _pad: [0; 3],
        }
    }

    /// Quad whose pattern is drawn procedurally in the fragment shader
    /// (`KIND_DOUBLE`/`KIND_CURLY`/`KIND_DOTTED`/`KIND_DASHED`). `uv` is
    /// repurposed to carry the quad's pixel size — pattern kinds never sample
    /// the atlas.
    fn pattern(&self, x: f32, y: f32, w: f32, h: f32, color: [u8; 3], kind: u32) -> Instance {
        let c = srgb(color);
        Instance {
            rect: [x, y, w, h],
            uv: [w, h, 0.0, 0.0],
            color: [c[0], c[1], c[2], 1.0],
            kind,
            _pad: [0; 3],
        }
    }

    /// Rebuild every slot of `row` from `cells` (just this row's slice, per
    /// `row_ranges`) and the cursor, if it sits on this row.
    fn rebuild_row(
        &mut self,
        queue: &wgpu::Queue,
        font: &mut text::Font,
        frame: &Frame,
        row: u16,
        cells: &[RenderCell],
    ) {
        // Clear the row's slots.
        let start = self.slot_base(row, 0);
        let end = start + self.grid_cols as usize * SLOTS_PER_CELL;
        for slot in &mut self.slots[start..end] {
            *slot = ZERO_INSTANCE;
        }

        let cursor_here = frame.cursor_shape != CursorShape::Hidden && frame.cursor_row == row;

        // Ligature pass: mark maximal runs of adjacent same-style symbol cells;
        // their glyph slots are filled from one shaped run below instead of
        // per-cell rasters.
        let mut in_run = vec![false; cells.len()];
        let mut lig_runs: Vec<(usize, usize)> = Vec::new(); // [i, j) index ranges
        if self.ligatures {
            let mut i = 0;
            while i < cells.len() {
                if !is_lig_char(cells[i].c) || cells[i].zw[0] != '\0' {
                    i += 1;
                    continue;
                }
                let mut j = i + 1;
                while j < cells.len()
                    && cells[j].col == cells[j - 1].col + 1
                    && is_lig_char(cells[j].c)
                    && cells[j].zw[0] == '\0'
                    && cells[j].bold == cells[i].bold
                    && cells[j].italic == cells[i].italic
                    && cells[j].fg == cells[i].fg
                {
                    j += 1;
                }
                if j - i >= 2 {
                    for k in i..j {
                        in_run[k] = true;
                    }
                    lig_runs.push((i, j));
                }
                i = j;
            }
        }

        for (idx, cell) in cells.iter().enumerate() {
            if cell.col >= self.grid_cols {
                continue;
            }
            let base = self.slot_base(row, cell.col);
            let x = self.pad_x + cell.col as f32 * self.cell_w;
            let y = self.pad_y + row as f32 * self.cell_h;

            // Slot 0: background / selection fill.
            let fill = if cell.selected {
                Some(palette::selection())
            } else if cell.bg != palette::DEFAULT_BG {
                Some(cell.bg)
            } else {
                None
            };
            if let Some(bg) = fill {
                self.slots[base] = self.solid(x, y, self.cell_w, self.cell_h, bg);
            }

            // Slot 1: glyph (ligature-run members are drawn after this loop).
            if cell.c != '\0' && !in_run[idx] {
                let pen_x = x;
                let xi = pen_x.floor();
                let frac = pen_x - xi;
                let xbin = ((frac * SUBPX_BINS as f32).round() as u32 % SUBPX_BINS) as u8;
                if let Some(g) = self.glyph(queue, font, (cell.c, cell.zw, cell.bold, cell.italic, xbin)) {
                    let gx = xi + g.left;
                    let gy = y + self.ascent - g.top;
                    self.slots[base + 1] = self.glyph_instance(&g, gx, gy, cell.fg);
                }
            }

            // Slot 2: underline (kind decides rect vs procedural pattern).
            if cell.underline != UnderlineKind::None {
                let thick = (self.cell_h * 0.07).max(1.0);
                let uy = y + self.ascent + (self.cell_h - self.ascent) * 0.4;
                let uc = cell.underline_color;
                self.slots[base + 2] = match cell.underline {
                    UnderlineKind::Single => self.solid(x, uy, self.cell_w, thick, uc),
                    UnderlineKind::Double => {
                        // One quad tall enough for both lines; shader draws the split.
                        self.pattern(x, uy - thick, self.cell_w, thick * 3.0, uc, KIND_DOUBLE)
                    }
                    UnderlineKind::Curly => {
                        self.pattern(x, uy - thick, self.cell_w, thick * 3.0, uc, KIND_CURLY)
                    }
                    UnderlineKind::Dotted => {
                        self.pattern(x, uy, self.cell_w, thick, uc, KIND_DOTTED)
                    }
                    UnderlineKind::Dashed => {
                        self.pattern(x, uy, self.cell_w, thick, uc, KIND_DASHED)
                    }
                    UnderlineKind::None => unreachable!(),
                };
            }

            // Slot 3: strikeout.
            if cell.strike {
                let thick = (self.cell_h * 0.07).max(1.0);
                let sy = y + self.ascent * 0.65;
                self.slots[base + 3] = self.solid(x, sy, self.cell_w, thick, cell.fg);
            }
        }

        // Draw each ligature run: shaped glyphs land in the run's cells' glyph
        // slots in visual order (a collapsed `=>` produces one glyph in the
        // first cell; the second cell's slot stays empty).
        for &(i, j) in &lig_runs {
            let text: String = cells[i..j].iter().map(|c| c.c).collect();
            let run_x = self.pad_x + cells[i].col as f32 * self.cell_w;
            let y = self.pad_y + row as f32 * self.cell_h;
            let shaped = self.run(queue, font, &text, cells[i].bold, cells[i].italic);
            for (n, (gx_off, g)) in shaped.into_iter().enumerate() {
                // More glyphs than cells can't happen for a mono font; guard anyway.
                let Some(cell) = cells.get(i + n).filter(|_| i + n < j) else {
                    break;
                };
                if cell.col >= self.grid_cols {
                    continue;
                }
                if let Some(g) = g {
                    let base = self.slot_base(row, cell.col);
                    let gx = (run_x + gx_off).floor() + g.left;
                    let gy = y + self.ascent - g.top;
                    self.slots[base + 1] = self.glyph_instance(&g, gx, gy, cells[i].fg);
                }
            }
        }

        if cursor_here {
            self.apply_cursor(queue, font, frame, cells);
        }
    }

    /// Write the cursor into its cell's slots (already-cleared/rebuilt row).
    /// `row_cells` is the cursor row's slice (from `row_ranges`).
    fn apply_cursor(
        &mut self,
        queue: &wgpu::Queue,
        font: &mut text::Font,
        frame: &Frame,
        row_cells: &[RenderCell],
    ) {
        let col = frame.cursor_col;
        let row = frame.cursor_row;
        if col >= self.grid_cols || row >= self.grid_rows {
            return;
        }
        let base = self.slot_base(row, col);
        let x = self.pad_x + col as f32 * self.cell_w;
        let y = self.pad_y + row as f32 * self.cell_h;
        let cursor_color = palette::cursor();
        let cell = row_cells.iter().find(|c| c.col == col).copied();

        match frame.cursor_shape {
            CursorShape::Block => {
                // Filled block in cursor color; glyph (if any) redrawn in the
                // cell bg so it reads against the block.
                self.slots[base] = self.solid(x, y, self.cell_w, self.cell_h, cursor_color);
                self.slots[base + 1] = ZERO_INSTANCE;
                if let Some(c) = cell {
                    if c.c != '\0' {
                        let xi = x.floor();
                        let frac = x - xi;
                        let xbin = ((frac * SUBPX_BINS as f32).round() as u32 % SUBPX_BINS) as u8;
                        if let Some(g) = self.glyph(queue, font, (c.c, c.zw, c.bold, c.italic, xbin)) {
                            let gx = xi + g.left;
                            let gy = y + self.ascent - g.top;
                            // Color glyphs keep their own color; mask glyphs use bg.
                            self.slots[base + 1] = self.glyph_instance(&g, gx, gy, c.bg);
                        }
                    }
                }
            }
            CursorShape::Bar => {
                let w = (self.cell_w * 0.12).max(1.0);
                self.slots[base + 3] = self.solid(x, y, w, self.cell_h, cursor_color);
            }
            CursorShape::Underline => {
                let h = (self.cell_h * 0.12).max(2.0);
                self.slots[base + 3] =
                    self.solid(x, y + self.cell_h - h, self.cell_w, h, cursor_color);
            }
            CursorShape::Hidden => {}
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub fn draw(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        view: &wgpu::TextureView,
        screen_w: f32,
        screen_h: f32,
        frame: &Frame,
        font: &mut text::Font,
    ) -> Result<()> {
        // (Re)allocate the persistent buffer when the grid size changes; this
        // also forces a full rebuild this frame.
        let resized = frame.cols != self.grid_cols || frame.rows != self.grid_rows;
        if resized {
            self.grid_cols = frame.cols;
            self.grid_rows = frame.rows;
            let total = self.grid_cols as usize
                * (self.grid_rows + 2 * OVERSCAN_ROWS) as usize
                * SLOTS_PER_CELL;
            self.slots = vec![ZERO_INSTANCE; total];
            self.full_upload = true;
            let needed = total as u64;
            if needed > self.instance_cap {
                let cap = needed.next_power_of_two().max(4096);
                self.instances = device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("instances"),
                    size: cap * std::mem::size_of::<Instance>() as u64,
                    usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                self.instance_cap = cap;
            }
        }

        // Which rows to rebuild this frame.
        let full = resized || matches!(frame.damage, FrameDamage::Full);
        let mut dirty: Vec<u16> = if full {
            (0..self.grid_rows).collect()
        } else if let FrameDamage::Rows(rows) = &frame.damage {
            // ponytail: dilate damage by ±1 row. A tall glyph (emoji, some icons)
            // rasterizes past its cell and bleeds into the neighbor row; redrawing
            // only the changed row leaves that overflow behind (the backspace
            // "ghost"). Repaint the touching rows too. Upgrade to true per-glyph
            // bounds tracking if this overdraw ever shows up in profiles.
            let mut r: Vec<u16> = rows
                .iter()
                .flat_map(|&row| [row.saturating_sub(1), row, row + 1])
                .filter(|&r| r < self.grid_rows)
                .collect();
            r.sort_unstable();
            r.dedup();
            r
        } else {
            Vec::new()
        };

        // Skip the range pass entirely on no-damage frames.
        let ranges = if dirty.is_empty() {
            Vec::new()
        } else {
            row_ranges(&frame.cells, self.grid_rows)
        };
        for &row in &dirty {
            let (start, end) = ranges[row as usize];
            self.rebuild_row(queue, font, frame, row, &frame.cells[start..end]);
        }

        // Upload: whole buffer after a shift/wipe, else only dirty rows,
        // coalescing contiguous runs into one write. Dirty-row offsets are in
        // slot space (viewport row + OVERSCAN_ROWS).
        let stride = std::mem::size_of::<Instance>() as u64;
        let row_slots = self.grid_cols as usize * SLOTS_PER_CELL;
        if self.full_upload {
            if !self.slots.is_empty() {
                queue.write_buffer(&self.instances, 0, bytemuck::cast_slice(&self.slots));
            }
            self.full_upload = false;
        } else if !self.slots.is_empty() && !dirty.is_empty() {
            let mut i = 0;
            while i < dirty.len() {
                let run_start = dirty[i];
                let mut run_end = run_start;
                while i + 1 < dirty.len() && dirty[i + 1] == run_end + 1 {
                    run_end = dirty[i + 1];
                    i += 1;
                }
                i += 1;
                let first_slot = (run_start + OVERSCAN_ROWS) as usize * row_slots;
                let last_slot = ((run_end + OVERSCAN_ROWS) as usize + 1) * row_slots;
                let byte_off = first_slot as u64 * stride;
                queue.write_buffer(
                    &self.instances,
                    byte_off,
                    bytemuck::cast_slice(&self.slots[first_slot..last_slot]),
                );
            }
        }
        dirty.clear();

        queue.write_buffer(
            &self.uniform,
            0,
            bytemuck::cast_slice(&[screen_w, screen_h, 0.0, self.scroll.px]),
        );

        let total_instances = self.slots.len() as u32;
        // Premultiplied clear so a translucent terminal background composites
        // correctly over the DirectComposition surface behind it. Premultiply in
        // gamma (sRGB) space — NOT linear — so the terminal's translucency reads
        // identically to XAML's straight-alpha sidebar/titlebar brush over the
        // same backdrop. (Linear-space premult composited lighter, leaving a
        // visible seam between body and chrome at 30-50% opacity.) The stored
        // sRGB value is (bg/255)*a; we feed wgpu the linear that re-encodes to it.
        let a = self.bg_a as f64;
        let g = |c: u8| srgb_to_linear((c as f64 / 255.0) * a);
        let bg = [g(self.bg[0]), g(self.bg[1]), g(self.bg[2])];
        let mut encoder =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("cells"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: bg[0],
                            g: bg[1],
                            b: bg[2],
                            a,
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                    depth_slice: None,
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            if total_instances > 0 {
                // Always clip to the grid rect: overscan rows sit outside the
                // viewport and must never leak into the padding or the
                // leftover strip below the last row (the clear ignores
                // scissor, so the padding stays filled with bg either way).
                let sx = self.pad_x.max(0.0) as u32;
                let sy = self.pad_y.max(0.0) as u32;
                let sw = ((self.grid_cols as f32 * self.cell_w).ceil() as u32)
                    .min((screen_w as u32).saturating_sub(sx));
                let sh = ((self.grid_rows as f32 * self.cell_h).ceil() as u32)
                    .min((screen_h as u32).saturating_sub(sy));
                if sw > 0 && sh > 0 {
                    pass.set_scissor_rect(sx, sy, sw, sh);
                }
                pass.set_pipeline(&self.pipeline);
                pass.set_bind_group(0, &self.bind_group, &[]);
                pass.set_vertex_buffer(0, self.instances.slice(..));
                pass.draw(0..4, 0..total_instances);
            }
        }
        queue.submit([encoder.finish()]);
        Ok(())
    }
}

const SHADER: &str = r#"
struct Uniforms { screen: vec2<f32>, scroll: vec2<f32> };
@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var atlas: texture_2d<f32>;
@group(0) @binding(2) var samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) @interpolate(flat) kind: u32,
    // Position within the quad in px (0..size), and the quad's px size.
    // Only meaningful for kind >= 2 (procedural decoration patterns), which
    // never sample the atlas.
    @location(3) local: vec2<f32>,
    @location(4) @interpolate(flat) size: vec2<f32>,
};

@vertex
fn vs_main(
    @builtin(vertex_index) vi: u32,
    @location(0) rect: vec4<f32>,
    @location(1) uvr: vec4<f32>,
    @location(2) color: vec4<f32>,
    @location(3) kind: u32,
) -> VsOut {
    var corners = array<vec2<f32>, 4>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, 1.0),
    );
    let corner = corners[vi];
    let pos_px = rect.xy + corner * rect.zw + vec2<f32>(0.0, u.scroll.y);
    let ndc = vec2<f32>(
        pos_px.x / u.screen.x * 2.0 - 1.0,
        1.0 - pos_px.y / u.screen.y * 2.0,
    );
    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.uv = mix(uvr.xy, uvr.zw, corner);
    out.color = color;
    out.kind = kind;
    out.local = corner * rect.zw;
    out.size = rect.zw;
    return out;
}

// sRGB -> linear. Our color inputs (palette bytes, emoji atlas pixels) are sRGB;
// the surface is an sRGB format that re-encodes on write, so we must hand it
// linear values or everything comes out washed out (most visible on emoji).
fn s2l(c: vec3<f32>) -> vec3<f32> {
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3<f32>(2.4));
    return select(hi, lo, c <= vec3<f32>(0.04045));
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    if (in.kind >= 2u) {
        // Procedural decoration patterns: no atlas sample, `in.local` is the
        // px position within the quad and `in.size` its px extent.
        if (in.kind == 2u) {
            // Double underline: two lines (top third / bottom third), gap
            // in the middle third.
            let fy = in.local.y / in.size.y;
            if (fy > 0.33 && fy < 0.67) {
                discard;
            }
        } else if (in.kind == 3u) {
            // Curly underline: distance to a sine wave, one period per ~4x
            // the quad height, amplitude filling the quad.
            let period = in.size.y * 4.0;
            let mid = in.size.y * 0.5 * (1.0 + sin(in.local.x * 6.2832 / period));
            if (abs(in.local.y - mid) > in.size.y * 0.25) {
                discard;
            }
        } else if (in.kind == 4u) {
            // Dotted underline: 50% duty, pitch ~2x the quad height.
            let pitch = in.size.y * 2.0;
            if (fract(in.local.x / pitch) > 0.5) {
                discard;
            }
        } else if (in.kind == 5u) {
            // Dashed underline: 2/3 duty, ~9px period.
            if (fract(in.local.x / 9.0) > 0.667) {
                discard;
            }
        }
        let a = in.color.a;
        return vec4<f32>(s2l(in.color.rgb) * a, a);
    }
    let texel = textureSample(atlas, samp, in.uv);
    if (in.kind == 1u) {
        // Color glyph: straight RGBA from the atlas, decoded to linear.
        // Premultiply for composition-correct output.
        return vec4<f32>(s2l(texel.rgb) * texel.a, texel.a);
    }
    // Solid/mask: tint by instance color (decoded), coverage from atlas alpha.
    let a = in.color.a * texel.a;
    return vec4<f32>(s2l(in.color.rgb) * a, a);
}
"#;

#[cfg(test)]
mod shader_tests {
    use super::SHADER;

    /// Parse + validate the inline WGSL with `naga` (wgpu's own frontend, via
    /// its `pub use naga` re-export) so a syntax/type error is caught here
    /// instead of only at pipeline-creation time on a machine with a GPU.
    #[test]
    fn shader_wgsl_parses_and_validates() {
        let module = wgpu::naga::front::wgsl::parse_str(SHADER).expect("WGSL parse");
        let mut validator = wgpu::naga::valid::Validator::new(
            wgpu::naga::valid::ValidationFlags::all(),
            wgpu::naga::valid::Capabilities::empty(),
        );
        validator.validate(&module).expect("WGSL validate");
    }
}

#[cfg(test)]
mod row_ranges_tests {
    use super::*;

    fn cell(row: u16, col: u16) -> RenderCell {
        RenderCell {
            col,
            row,
            c: 'x',
            zw: ['\0'; 2],
            fg: [0, 0, 0],
            bg: [0, 0, 0],
            bold: false,
            italic: false,
            underline: UnderlineKind::None,
            underline_color: [0, 0, 0],
            strike: false,
            selected: false,
        }
    }

    #[test]
    fn empty_input_yields_all_empty_ranges() {
        let ranges = row_ranges(&[], 3);
        assert_eq!(ranges, vec![(0, 0), (0, 0), (0, 0)]);
    }

    #[test]
    fn rows_with_cells_and_empty_rows_mixed() {
        // Row 0: 2 cells, row 1: none, row 2: 1 cell.
        let cells = vec![cell(0, 0), cell(0, 1), cell(2, 0)];
        let ranges = row_ranges(&cells, 3);
        assert_eq!(ranges, vec![(0, 2), (2, 2), (2, 3)]);
        assert!(ranges[1].0 == ranges[1].1);
    }

    #[test]
    fn all_rows_populated() {
        let cells = vec![cell(0, 0), cell(1, 0), cell(1, 1), cell(2, 0)];
        let ranges = row_ranges(&cells, 3);
        assert_eq!(ranges, vec![(0, 1), (1, 3), (3, 4)]);
        for (start, end) in &ranges {
            assert!(start <= end);
        }
    }

    #[test]
    fn trailing_rows_beyond_last_cell_are_empty() {
        let cells = vec![cell(0, 0)];
        let ranges = row_ranges(&cells, 4);
        assert_eq!(ranges, vec![(0, 1), (1, 1), (1, 1), (1, 1)]);
    }
}


#[cfg(test)]
mod overscan_tests {
    use super::{shift_slot_rows, Instance, ZERO_INSTANCE};

    fn marked(y: f32) -> Instance {
        let mut i = ZERO_INSTANCE;
        i.rect = [1.0, y, 8.0, 16.0];
        i.color = [1.0, 1.0, 1.0, 1.0];
        i
    }

    #[test]
    fn shift_up_moves_rows_and_patches_y() {
        let row_slots = 2;
        let mut slots = vec![ZERO_INSTANCE; 4 * row_slots]; // 4 rows
        slots[2 * row_slots] = marked(40.0); // row 2, first slot
        shift_slot_rows(&mut slots, row_slots, 1, 20.0);
        // Row 2 content now lives in row 1, drawn one cell higher.
        assert_eq!(slots[row_slots].rect[1], 20.0);
        assert_eq!(slots[row_slots].color[3], 1.0);
        // Vacated bottom row is zeroed.
        assert_eq!(slots[3 * row_slots].color[3], 0.0);
        assert_eq!(slots[2 * row_slots].color[3], 0.0);
    }

    #[test]
    fn shift_down_mirrors() {
        let row_slots = 2;
        let mut slots = vec![ZERO_INSTANCE; 4 * row_slots];
        slots[row_slots] = marked(20.0); // row 1
        shift_slot_rows(&mut slots, row_slots, -2, 20.0);
        // Row 1 content now lives in row 3, drawn two cells lower.
        assert_eq!(slots[3 * row_slots].rect[1], 60.0);
        assert_eq!(slots[3 * row_slots].color[3], 1.0);
        // Vacated top rows zeroed.
        assert_eq!(slots[row_slots].color[3], 0.0);
    }

    #[test]
    fn shift_larger_than_buffer_clears() {
        let row_slots = 2;
        let mut slots = vec![marked(0.0); 4 * row_slots];
        shift_slot_rows(&mut slots, row_slots, 99, 20.0);
        assert!(slots.iter().all(|s| s.color[3] == 0.0));
    }
}

#[cfg(test)]
mod scroll_anim_tests {
    use super::ScrollAnim;

    #[test]
    fn bump_sets_offset_and_tick_decays_to_zero() {
        let mut a = ScrollAnim::default();
        a.bump(3, 20.0, 600.0); // 3 rows up at 20px cells -> +60px
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
        a.bump(500, 20.0, 240.0); // cat flood: don't fly the grid off-screen
        assert!(a.px <= 240.0 + f32::EPSILON);
        a.bump(-500, 20.0, 240.0);
        a.bump(-500, 20.0, 240.0);
        assert!(a.px >= -240.0 - f32::EPSILON);
    }

    #[test]
    fn bump_clamp_is_caller_supplied() {
        let mut a = ScrollAnim::default();
        a.bump(10, 20.0, 240.0); // 12-cell cap: 10 rows * 20px fits
        assert_eq!(a.px, 200.0);
        a.bump(10, 20.0, 240.0); // accumulates but clamps at 240
        assert_eq!(a.px, 240.0);
    }

    #[test]
    fn opposite_bumps_cancel() {
        let mut a = ScrollAnim::default();
        a.bump(2, 20.0, 600.0);
        a.bump(-2, 20.0, 600.0);
        assert_eq!(a.px, 0.0);
        assert!(!a.tick(16.0));
    }

    #[test]
    fn decay_is_monotonic() {
        let mut a = ScrollAnim::default();
        a.bump(2, 20.0, 600.0);
        let mut prev = a.px;
        while a.tick(16.0) {
            assert!(a.px < prev && a.px >= 0.0);
            prev = a.px;
        }
    }

    #[test]
    fn glide_settles_in_fixed_duration() {
        let mut a = ScrollAnim::default();
        a.bump(4, 20.0, 480.0); // 80 px displacement
        let mut t = 0.0;
        while a.tick(16.0) {
            t += 16.0;
            assert!(t < 200.0, "glide must settle within ~140ms, still moving at {t}ms");
        }
        assert_eq!(a.px, 0.0);
    }

    #[test]
    fn glide_speed_decreases_monotonically() {
        // cubic-out: displacement magnitude strictly decreases each tick
        let mut a = ScrollAnim::default();
        a.bump(4, 20.0, 480.0);
        let mut prev = a.px.abs();
        while a.tick(16.0) {
            assert!(a.px.abs() < prev, "|px| must shrink every tick");
            prev = a.px.abs();
        }
    }

    #[test]
    fn rebump_restarts_clock() {
        let mut a = ScrollAnim::default();
        a.bump(4, 20.0, 480.0);
        for _ in 0..6 { a.tick(16.0); } // ~96ms in
        a.bump(4, 20.0, 480.0); // new notch mid-glide
        // Must take close to a full duration again, not settle in the remaining ~44ms
        let mut t = 0.0;
        while a.tick(16.0) { t += 16.0; }
        assert!(t > 100.0, "re-bump must restart the ease, settled after only {t}ms");
    }

    #[test]
    fn bump_clamps_to_max() {
        let mut a = ScrollAnim::default();
        a.bump(100, 20.0, 480.0);
        assert_eq!(a.px, 480.0);
    }
}
