//! velo_core: C ABI over the Rust terminal core (term-core + pty-win + wgpu
//! renderer) for the C# WinUI 3 shell.
//!
//! The C# side owns the window, the custom title bar and the vertical-tab pane.
//! It hosts the terminal in a WinUI `SwapChainPanel`: `velo_attach` builds a
//! DX12 device + a composition swapchain, `velo_get_swapchain` hands the
//! swapchain to the panel (`ISwapChainPanelNative.SetSwapChain`), and C# forwards
//! input / size via `velo_key` / `velo_char` / `velo_mouse` / `velo_resize`.
//! Render is synchronous (`velo_render` + self-render on PTY data). PTY reader
//! threads wake the UI thread through an internal message-only window owned by
//! the engine. We call back into C# for tab events the terminal originates (OSC
//! titles, shell exit, tab keybinds).
//!
//! Split view (Phase 1): a `Pane` owns its own composition swapchain, renderer,
//! back buffers and cell grid, plus the id of the `Session` it currently shows.
//! `Engine` holds `Vec<Option<Pane>>`; many sessions, several panes, each pane
//! rendering one session. Pane 0 is created at `velo_attach` and reuses the
//! original swapchain, so the single-pane C# host keeps working unchanged. The
//! C# split UI (Phase 2) creates extra panes via `velo_pane_new`, binds tabs to
//! them with `velo_pane_bind`, and sizes/focuses them per panel.
//!
//! Non-Windows builds expose no symbols (ConPTY/wgpu-on-HWND are Windows-only);
//! the crate still compiles for the Linux dev workspace.

/// Default cell font size, in points (mirrors the old `app::FONT_SIZE_PT`).
/// Multiplied by the per-monitor DPI scale before building the font.
#[cfg(windows)]
const FONT_SIZE_PT: f32 = 13.0;

/// Inner padding (logical px) between the surface edge and the cell grid. Scaled
/// by DPI at use. Gives the terminal breathing room inside its own surface
/// instead of a margin around it.
const PAD_LOGICAL_PX: f32 = 10.0;

#[cfg(windows)]
mod host_client;

#[cfg(windows)]
mod imp {
    use std::ffi::c_void;
    use std::sync::Arc;

    use anyhow::{anyhow, Result};
    use parking_lot::Mutex;

    use windows::Win32::Foundation::{
        HANDLE, HGLOBAL, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM,
    };
    use windows::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, GetClipboardData, OpenClipboard, SetClipboardData,
    };
    use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::Input::KeyboardAndMouse::{VK_NEXT, VK_PRIOR, VK_TAB};
    use windows::Win32::UI::Shell::{
        DefSubclassProc, RemoveWindowSubclass, SetWindowSubclass,
    };
    use windows::core::{Interface, PCWSTR};
    use windows::Win32::Graphics::Direct3D12::ID3D12Resource;
    use windows::Win32::Graphics::Dxgi::Common::{
        DXGI_ALPHA_MODE_PREMULTIPLIED, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_UNKNOWN,
        DXGI_SAMPLE_DESC,
    };
    use windows::Win32::Graphics::Dxgi::{
        CreateDXGIFactory2, IDXGIFactory2, IDXGISwapChain3, DXGI_CREATE_FACTORY_FLAGS,
        DXGI_MATRIX_3X2_F,
        DXGI_PRESENT, DXGI_SCALING_STRETCH, DXGI_SWAP_CHAIN_DESC1, DXGI_SWAP_CHAIN_FLAG,
        DXGI_SWAP_EFFECT_FLIP_DISCARD, DXGI_USAGE_RENDER_TARGET_OUTPUT,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, KillTimer, PostMessageW, RegisterClassW,
        SetTimer, HMENU, HWND_MESSAGE, WINDOW_EX_STYLE, WINDOW_STYLE, WM_TIMER, WNDCLASSW,
    };

    use super::{FONT_SIZE_PT, PAD_LOGICAL_PX};

    /// Reader-thread -> message-loop wakeups (above `WM_APP`). `wParam` carries
    /// the stable session id. We post these to the engine's internal message-only
    /// window; its wndproc drains the inbox + renders on the UI thread.
    const WM_APP: u32 = 0x8000;
    const MSG_PTY_DATA: u32 = WM_APP + 1;
    const MSG_PTY_EOF: u32 = WM_APP + 2;
    /// Win32 timer id on the wakeup window driving cursor blink.
    const BLINK_TIMER_ID: usize = 0xB111;
    /// Blink half-period (ms); matches the Windows default caret rate.
    const BLINK_MS: u32 = 530;

    /// Subclass id for the wakeup-window hook (any stable per-proc constant).
    const SUBCLASS_ID: usize = 1;

    /// Window class for the engine's message-only wakeup window.
    const WAKEUP_CLASS: PCWSTR = windows::core::w!("VeloWakeupWindow");

    /// `CF_UNICODETEXT` clipboard format (UTF-16).
    const CF_UNICODETEXT: u32 = 13;

    /// Sentinel for "this pane shows no session".
    const NO_SESSION: usize = usize::MAX;

    /// Lines-per-wheel-notch the C# side scales `delta_lines` by (matches
    /// `PaneHost_PointerWheelChanged`'s `delta * 3 / 120`). Used to convert a
    /// `delta_lines` batch back into one SGR wheel event per physical notch.
    const NOTCH_LINES: i32 = 3;

    /// Function pointers the Rust core calls to notify the C# shell. All optional;
    /// `ctx` is the C# side's opaque handle (passed back unchanged). Invoked only
    /// on the UI thread (inside the subclass proc), never from a PTY reader thread.
    #[repr(C)]
    #[derive(Clone, Copy)]
    pub struct VeloCallbacks {
        pub ctx: *mut c_void,
        /// (ctx, session_id, utf16_ptr, utf16_len) — live OSC tab title changed.
        pub on_title_changed: Option<extern "C" fn(*mut c_void, u32, *const u16, usize)>,
        /// (ctx, session_id) — the shell exited (EOF); C# should remove the row.
        pub on_tab_closed: Option<extern "C" fn(*mut c_void, u32)>,
        /// (ctx, session_id) — the core changed the active session (e.g. after a
        /// close); C# should sync its pane selection.
        pub on_active_changed: Option<extern "C" fn(*mut c_void, u32)>,
        /// (ctx) — Ctrl+Shift+T from the focused terminal.
        pub on_new_tab_requested: Option<extern "C" fn(*mut c_void)>,
        /// (ctx, session_id) — Ctrl+Shift+W from the focused terminal.
        pub on_close_tab_requested: Option<extern "C" fn(*mut c_void, u32)>,
        /// (ctx, forward) — Ctrl+Tab / Ctrl+Shift+Tab from the focused terminal.
        pub on_switch_tab_requested: Option<extern "C" fn(*mut c_void, bool)>,
        /// (ctx, session_id, utf16_ptr, utf16_len) — OSC 7 working directory.
        pub on_cwd_changed: Option<extern "C" fn(*mut c_void, u32, *const u16, usize)>,
        /// (ctx, session_id, phase, exit, dur_ms, utf16_ptr, utf16_len) — OSC 133
        /// command mark. phase: 0 prompt, 1 command-start (text), 2 command-end.
        pub on_command:
            Option<extern "C" fn(*mut c_void, u32, u8, i32, u64, *const u16, usize)>,
        /// (ctx) — a smooth-scroll glide started; the shell should drive
        /// `velo_tick` from its compositor frame callback until it returns 0.
        pub on_anim: Option<extern "C" fn(*mut c_void)>,
        /// (ctx, file_id, dirty) — an editor buffer's dirty state flipped.
        pub on_editor_dirty: Option<extern "C" fn(*mut c_void, u32, u8)>,
        /// (ctx, over) — pointer is hovering a link (1) or left it (0); host sets the cursor.
        pub on_link_hover: Option<extern "C" fn(*mut c_void, u8)>,
        /// (ctx, utf16_ptr, utf16_len) — open a link target. URLs -> browser, paths -> reveal.
        pub on_open_link: Option<extern "C" fn(*mut c_void, *const u16, usize)>,
    }

    impl Default for VeloCallbacks {
        fn default() -> Self {
            Self {
                ctx: std::ptr::null_mut(),
                on_title_changed: None,
                on_tab_closed: None,
                on_active_changed: None,
                on_new_tab_requested: None,
                on_close_tab_requested: None,
                on_switch_tab_requested: None,
                on_cwd_changed: None,
                on_command: None,
                on_anim: None,
                on_editor_dirty: None,
                on_link_hover: None,
                on_open_link: None,
            }
        }
    }

    /// A tab's PTY: in-process ConPTY, or a session owned by the detached
    /// velo-pty-host process (survives the UI closing — session recovery).
    enum PtyHandle {
        Local(pty_win::Pty),
        Remote(crate::host_client::RemotePty),
    }

    /// Clonable stdin writer over either backend (mirrors `pty_win::PtyWriter`).
    #[derive(Clone)]
    enum HandleWriter {
        Local(pty_win::PtyWriter),
        Remote(Arc<std::sync::Mutex<std::fs::File>>),
    }

    impl HandleWriter {
        fn write_all(&self, b: &[u8]) -> std::io::Result<()> {
            match self {
                HandleWriter::Local(w) => w.write_all(b),
                HandleWriter::Remote(f) => {
                    use std::io::Write;
                    let mut g = f.lock().expect("remote stdin mutex poisoned");
                    g.write_all(b)?;
                    g.flush()
                }
            }
        }
    }

    impl PtyHandle {
        fn resize(&self, cols: u16, rows: u16) -> Result<()> {
            match self {
                PtyHandle::Local(p) => p.resize(cols, rows),
                PtyHandle::Remote(r) => r.resize(cols, rows),
            }
        }

        fn writer(&self) -> HandleWriter {
            match self {
                PtyHandle::Local(p) => HandleWriter::Local(p.writer()),
                PtyHandle::Remote(r) => HandleWriter::Remote(r.writer()),
            }
        }

        /// Host session id when remote (persisted by C# for reattach).
        fn host_id(&self) -> Option<u32> {
            match self {
                PtyHandle::Local(_) => None,
                PtyHandle::Remote(r) => Some(r.id),
            }
        }

        /// End the child. Local drops kill implicitly; remote must ask the
        /// host (a plain drop DETACHES and leaves it running).
        fn kill(&self) {
            if let PtyHandle::Remote(r) = self {
                r.kill();
            }
        }
    }

    /// One terminal tab: an independent shell + parser, plus the inbox the reader
    /// thread fills and the message loop drains.
    struct Session {
        terminal: term_core::Terminal,
        pty: PtyHandle,
        /// Reader appends, the loop drains via `mem::take`.
        inbox: Arc<Mutex<Vec<u8>>>,
        /// Coalesces PTY-flood wakeups: the reader only posts `MSG_PTY_DATA` on a
        /// false->true flip, so hundreds of 4K/64K reads collapse into one drain
        /// + render pass per pump instead of one per read.
        wakeup_pending: Arc<std::sync::atomic::AtomicBool>,
        /// Live tab label (from OSC title), to debounce title callbacks.
        tab_title: String,
    }

    /// How long after a resize/zoom to suppress scroll glides (ConPTY repaints
    /// arrive asynchronously, several frames later).
    const GLIDE_MUTE_AFTER_RESIZE: std::time::Duration = std::time::Duration::from_millis(500);

    /// Double-buffered flip swapchain.
    const BACKBUFFER_COUNT: u32 = 2;
    /// wgpu view format over the `R8G8B8A8_UNORM` swapchain buffers (sRGB RTV
    /// over a UNORM flip-model buffer — the one sRGB-over-UNORM case DXGI allows).
    const SWAP_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8UnormSrgb;

    /// One on-screen surface. Owns a transparent composition swapchain (bound to
    /// a WinUI SwapChainPanel on the C# side), its back buffers, an independent
    /// renderer (so per-grid damage tracking stays correct across panes), its cell
    /// grid, and the id of the session it currently displays (`NO_SESSION` if
    /// empty).
    /// What a pane draws: a terminal session's frames, or the editor
    /// workspace's. Editor panes reuse the whole render path (renderer,
    /// glide, bg/alpha/zoom) — only the frame source differs.
    #[derive(PartialEq, Clone, Copy)]
    enum PaneKind {
        Terminal,
        Editor,
    }

    struct Pane {
        kind: PaneKind,
        swapchain: IDXGISwapChain3,
        backbuffers: Vec<(wgpu::Texture, wgpu::TextureView)>,
        renderer: renderer::Renderer,
        width: u32,
        height: u32,
        cols: u16,
        rows: u16,
        /// Session this pane shows (`NO_SESSION` = none).
        session: usize,
        /// Force a full repaint next frame (set on bind / resize / theme change).
        force_full: bool,
        /// Glides are suppressed until this instant. Resize/zoom triggers an
        /// async ConPTY repaint whose history churn would otherwise read as
        /// content motion and fire a bogus bounce glide.
        glide_mute_until: std::time::Instant,
    }

    impl Pane {
        /// Wrap the swapchain's back buffers as wgpu textures + render-target
        /// views. Call after create + every `ResizeBuffers`.
        fn wrap_backbuffers(&mut self, device: &wgpu::Device) {
            self.backbuffers.clear();
            for i in 0..BACKBUFFER_COUNT {
                let res: ID3D12Resource = match unsafe { self.swapchain.GetBuffer(i) } {
                    Ok(r) => r,
                    Err(e) => {
                        log::error!("swapchain GetBuffer({i}) failed: {e}");
                        return;
                    }
                };
                let hal_tex = unsafe {
                    wgpu::hal::dx12::Device::texture_from_raw(
                        res,
                        SWAP_FORMAT,
                        wgpu::TextureDimension::D2,
                        wgpu::Extent3d {
                            width: self.width,
                            height: self.height,
                            depth_or_array_layers: 1,
                        },
                        1,
                        1,
                    )
                };
                let tex = unsafe {
                    device.create_texture_from_hal::<wgpu::hal::api::Dx12>(
                        hal_tex,
                        &wgpu::TextureDescriptor {
                            label: Some("backbuffer"),
                            size: wgpu::Extent3d {
                                width: self.width,
                                height: self.height,
                                depth_or_array_layers: 1,
                            },
                            mip_level_count: 1,
                            sample_count: 1,
                            dimension: wgpu::TextureDimension::D2,
                            format: SWAP_FORMAT,
                            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
                            view_formats: &[],
                        },
                    )
                };
                let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
                self.backbuffers.push((tex, view));
            }
        }

        /// The SwapChainPanel lays out in DIPs, but our back buffers are physical
        /// pixels (DIP * dpi_scale). The inverse-scale matrix tells DXGI the buffer
        /// is already at native res, so it composites 1:1 (crisp at fractional DPI).
        fn apply_transform(&self, dpi_scale: f32) {
            let inv = if dpi_scale > 0.0 { 1.0 / dpi_scale } else { 1.0 };
            let m = DXGI_MATRIX_3X2_F {
                _11: inv,
                _22: inv,
                ..Default::default()
            };
            if let Err(e) = unsafe { self.swapchain.SetMatrixTransform(&m) } {
                log::error!("SetMatrixTransform failed: {e}");
            }
        }

        /// Resize the swapchain to `w`x`h` physical px and re-wrap. No-op if
        /// unchanged and the back buffers already exist.
        fn apply_size(&mut self, device: &wgpu::Device, w: u32, h: u32, dpi_scale: f32) {
            let rw = w.max(1);
            let rh = h.max(1);
            if rw != self.width || rh != self.height || self.backbuffers.is_empty() {
                self.width = rw;
                self.height = rh;
                // Release the wgpu textures referencing the old buffers, flush the
                // GPU, then resize and re-wrap. Only wait when there were textures to
                // release — a freshly created pane has none, and an indefinite wait on
                // the shared device there risks a hang.
                let had_buffers = !self.backbuffers.is_empty();
                self.backbuffers.clear();
                if had_buffers {
                    let _ = device.poll(wgpu::PollType::wait_indefinitely());
                }
                if let Err(e) = unsafe {
                    self.swapchain.ResizeBuffers(
                        BACKBUFFER_COUNT,
                        rw,
                        rh,
                        DXGI_FORMAT_UNKNOWN,
                        DXGI_SWAP_CHAIN_FLAG(0),
                    )
                } {
                    log::error!("swapchain ResizeBuffers failed: {e}");
                }
                self.wrap_backbuffers(device);
                self.apply_transform(dpi_scale);
            }
        }

        /// Recompute this pane's cell grid for its current pixel size + the given
        /// font/DPI, updating the renderer padding. Returns the new (cols, rows).
        fn recompute_grid(&mut self, font: &text::Font, dpi_scale: f32) -> (u16, u16) {
            // Inset the grid by DPI-scaled padding; the renderer offsets cells by
            // the same amount so the bg clear fills the padding (inner, not margin).
            let pad = (PAD_LOGICAL_PX * dpi_scale).round();
            self.renderer.set_pad(pad, pad);
            let grid_w = (self.width as f32 - 2.0 * pad).max(font.cell_w);
            let grid_h = (self.height as f32 - 2.0 * pad).max(font.cell_h);
            let (cols, rows) = grid_size(grid_w as u32, grid_h as u32, font.cell_w, font.cell_h);
            self.cols = cols;
            self.rows = rows;
            self.force_full = true;
            (cols, rows)
        }
    }

    /// All core state. Lives behind a raw pointer carried as the subclass
    /// `dwRefData`; the subclass proc is single-threaded on the UI message loop.
    pub struct Engine {
        /// Internal message-only window: PTY reader threads `PostMessage` wakeups
        /// here; its wndproc drains + renders on the UI thread. Also the clipboard
        /// owner. Not the render surface (the SwapChainPanel composites that).
        wakeup_hwnd: HWND,

        // GPU + render state shared across all panes/sessions.
        device: wgpu::Device,
        queue: wgpu::Queue,
        font: text::Font,
        dpi_scale: f32,
        /// Base font size in points (pre-DPI). Settable at runtime.
        font_pt: f32,
        /// Shell command line new tabs spawn (default `powershell.exe`).
        shell: String,
        /// Spawn sessions in the detached velo-pty-host so they survive app
        /// close (falls back to in-process ConPTY when the host won't start).
        recovery: bool,
        /// Terminal background (surface clear) color.
        bg: [u8; 3],
        /// Terminal background alpha (0 = transparent → blur-through, 1 = opaque).
        bg_a: f32,
        /// Ligature run shaping (applies to every pane; new panes inherit it).
        ligatures: bool,
        /// Cursor shape override (`None` = follow the shell / DECSCUSR).
        cursor_style: Option<term_core::CursorShape>,
        /// Blink the cursor (Win32 timer on the wakeup window).
        cursor_blink: bool,
        /// Current blink phase (true = cursor visible).
        blink_on: bool,

        // Panes keyed by stable id (index); `None` = freed slot so ids stay stable.
        // Pane 0 is the primary surface created at `velo_attach`.
        panes: Vec<Option<Pane>>,
        /// Pane that receives keyboard input / legacy mouse + resize.
        focused_pane: usize,

        // Sessions keyed by stable id (index); `None` = freed slot so ids never
        // shift under in-flight posted messages.
        sessions: Vec<Option<Box<Session>>>,
        /// render order -> session id (for neighbor pick on close).
        tab_order: Vec<usize>,

        /// Editor workspace: engine-owned so open files survive view switches.
        workspace: editor::Workspace,
        /// Index into `panes` of the editor pane (`NO_SESSION` until attached).
        editor_pane: usize,
        /// Dirty file ids at the last `on_editor_dirty` notification pass.
        last_dirty: Vec<u32>,

        mouse_down: bool,
        /// SGR button code of the pressed button (0/1/2) for the gesture in flight.
        mouse_button: u8,
        /// Whether the gesture in flight went to the SGR path. Decided once at
        /// press so a mid-drag Shift change can't send the app a release it
        /// never saw a press for (or vice versa).
        sgr_gesture: bool,
        /// Drop the `WM_CHAR` that follows an app keybind so it never reaches a PTY.
        swallow_next_char: bool,
        /// Buffered lone high surrogate awaiting its low surrogate.
        pending_high: Option<u16>,

        /// Pointer-press origin (physical px) for click-vs-drag discrimination.
        press_pos: Option<(f32, f32)>,
        /// Whether the pointer is currently over a link (dedupes hover callbacks).
        link_hover: bool,

        cb: VeloCallbacks,
    }

    /// Composition presentation handles, created once per attach.
    struct Composition {
        device: wgpu::Device,
        queue: wgpu::Queue,
        swapchain: IDXGISwapChain3,
    }

    /// Create a transparent (premultiplied-alpha) composition swapchain on the
    /// D3D12 command queue backing `queue`. Each pane gets one of these.
    unsafe fn create_comp_swapchain(queue: &wgpu::Queue) -> Result<IDXGISwapChain3> {
        // The D3D12 command queue backing wgpu's queue is the "device" arg to
        // CreateSwapChainForComposition.
        let cmd_queue = {
            let hal_queue = queue
                .as_hal::<wgpu::hal::api::Dx12>()
                .ok_or_else(|| anyhow!("wgpu queue is not DX12"))?;
            hal_queue.as_raw().clone()
        };
        let factory: IDXGIFactory2 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0))?;
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: 1,
            Height: 1,
            Format: DXGI_FORMAT_R8G8B8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: BACKBUFFER_COUNT,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags: 0,
        };
        let swapchain1 = factory.CreateSwapChainForComposition(&cmd_queue, &desc, None)?;
        Ok(swapchain1.cast()?)
    }

    /// DX12 device/queue + the primary composition swapchain. Vulkan is
    /// intentionally excluded — the composition swapchain needs the underlying
    /// D3D12 command queue.
    fn init_composition() -> Result<Composition> {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
            backends: wgpu::Backends::DX12,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        });
        let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::HighPerformance,
            compatible_surface: None,
            force_fallback_adapter: false,
        }))
        .map_err(|e| anyhow!("no DX12 adapter: {e:?}"))?;
        println!("backend: {:?}", adapter.get_info().backend);
        let (device, queue) =
            pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor::default()))?;

        let swapchain = unsafe { create_comp_swapchain(&queue)? };
        Ok(Composition {
            device,
            queue,
            swapchain,
        })
    }

    /// Build a renderer pre-configured with the current font + background.
    fn build_renderer(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        font: &text::Font,
        bg: [u8; 3],
        bg_a: f32,
    ) -> renderer::Renderer {
        let mut r = renderer::Renderer::new(
            device,
            queue,
            SWAP_FORMAT,
            font.cell_w,
            font.cell_h,
            font.ascent,
        );
        r.set_bg(bg);
        r.set_bg_alpha(bg_a);
        r
    }

    /// Bare class wndproc for the wakeup window (just defers to `DefWindowProcW`);
    /// the real handling is installed via `SetWindowSubclass(wakeup_proc)`.
    unsafe extern "system" fn wakeup_class_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        DefWindowProcW(hwnd, msg, wparam, lparam)
    }

    /// Create the engine's message-only wakeup window. PTY reader threads post
    /// `MSG_PTY_DATA`/`MSG_PTY_EOF` here; the C# WinUI dispatcher pumps them to
    /// `wakeup_proc` on the UI thread. Returns `HWND(null)` on failure.
    unsafe fn create_wakeup_window() -> HWND {
        let hmodule = match GetModuleHandleW(None) {
            Ok(h) => h,
            Err(_) => return HWND(std::ptr::null_mut()),
        };
        let hinst = HINSTANCE(hmodule.0);
        // Idempotent: RegisterClassW fails harmlessly if already registered.
        let cls = WNDCLASSW {
            lpfnWndProc: Some(wakeup_class_proc),
            hInstance: hinst,
            lpszClassName: WAKEUP_CLASS,
            ..Default::default()
        };
        let _ = RegisterClassW(&cls);
        CreateWindowExW(
            WINDOW_EX_STYLE(0),
            WAKEUP_CLASS,
            PCWSTR::null(),
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            Some(HWND_MESSAGE),
            None::<HMENU>,
            Some(hinst),
            None,
        )
        .unwrap_or(HWND(std::ptr::null_mut()))
    }

    /// Grid columns/rows that fit a surface of `width`x`height` px.
    fn grid_size(width: u32, height: u32, cell_w: f32, cell_h: f32) -> (u16, u16) {
        let cols = (width as f32 / cell_w).floor().max(1.0) as u16;
        let rows = (height as f32 / cell_h).floor().max(1.0) as u16;
        (cols, rows)
    }

    impl Engine {
        // ---- Pane / session lookup helpers --------------------------------

        /// Session id shown by the focused pane (`NO_SESSION` if none).
        fn focused_session(&self) -> usize {
            self.panes
                .get(self.focused_pane)
                .and_then(|o| o.as_ref())
                .map(|p| p.session)
                .unwrap_or(NO_SESSION)
        }

        /// Focused pane's session, mutably.
        fn active_session(&mut self) -> Option<&mut Session> {
            let sid = self.focused_session();
            if sid == NO_SESSION {
                return None;
            }
            self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut())
        }

        fn pane_cols_rows(&self, idx: usize) -> Option<(u16, u16)> {
            self.panes
                .get(idx)
                .and_then(|o| o.as_ref())
                .map(|p| (p.cols, p.rows))
        }

        // ---- Rendering -----------------------------------------------------

        /// Draw the session bound to pane `idx` into that pane's swapchain and
        /// present it. No-op for an empty/unsized pane.
        fn render_pane(&mut self, idx: usize) {
            let (kind, sid, dims) = match self.panes.get(idx) {
                Some(Some(p)) => (p.kind, p.session, (p.cols, p.rows)),
                _ => return,
            };
            // Snapshot the frame first (ends the session/workspace borrow before
            // the pane's mutable borrow + back-buffer view borrow below).
            let mut frame = match kind {
                PaneKind::Editor => match self.workspace.frame(dims.0, dims.1) {
                    Some(f) => f,
                    // No files open: draw an empty grid (bg clear only).
                    None => term_core::Frame {
                        cols: dims.0,
                        rows: dims.1,
                        cells: Vec::new(),
                        cursor_col: 0,
                        cursor_row: 0,
                        cursor_shape: term_core::CursorShape::Hidden,
                        damage: term_core::FrameDamage::Full,
                        scrolled_up: 0,
                    },
                },
                PaneKind::Terminal => {
                    if sid == NO_SESSION {
                        return;
                    }
                    match self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                        Some(s) => s.terminal.frame(),
                        None => return,
                    }
                }
            };
            // User cursor override + blink phase (terminal panes only; the
            // editor draws its own caret).
            if matches!(kind, PaneKind::Terminal)
                && frame.cursor_shape != term_core::CursorShape::Hidden
            {
                if let Some(s) = self.cursor_style {
                    frame.cursor_shape = s;
                }
                if self.cursor_blink && !self.blink_on {
                    frame.cursor_shape = term_core::CursorShape::Hidden;
                }
            }
            // Disjoint field borrows: `self.panes` (mut) vs `self.device`,
            // `self.queue`, `self.font` (the renderer draw args).
            let Some(Some(pane)) = self.panes.get_mut(idx) else {
                return;
            };
            if pane.backbuffers.is_empty() {
                return;
            }
            if pane.force_full {
                frame.damage = term_core::FrameDamage::Full;
                pane.force_full = false;
            }
            if frame.scrolled_up != 0 {
                // Shifts a wheel/stream can produce are small; anything bigger
                // is a resize/ConPTY repaint — snap, don't glide. Snapping also
                // wipes stale overscan content.
                let organic = frame.scrolled_up.unsigned_abs() <= renderer::OVERSCAN_ROWS as u32;
                if organic && std::time::Instant::now() >= pane.glide_mute_until {
                    pane.renderer.scroll_bump(frame.scrolled_up);
                } else {
                    pane.renderer.scroll_reset();
                }
            }
            let bb = unsafe { pane.swapchain.GetCurrentBackBufferIndex() } as usize;
            let Some((_, view)) = pane.backbuffers.get(bb) else {
                return;
            };
            if let Err(e) = pane.renderer.draw(
                &self.device,
                &self.queue,
                view,
                pane.width as f32,
                pane.height as f32,
                &frame,
                &mut self.font,
            ) {
                log::error!("render error: {e}");
                dbglog(&format!("render error: {e}"));
            }
            unsafe {
                let _ = pane.swapchain.Present(0, DXGI_PRESENT(0)).ok();
            }
            // A glide is live: tell C# to start driving velo_tick. Its handler
            // is idempotent (hooking an already-hooked pump is a no-op).
            let animating =
                matches!(self.panes.get(idx), Some(Some(p)) if p.renderer.scroll_active());
            if animating {
                if let Some(f) = self.cb.on_anim {
                    f(self.cb.ctx);
                }
            }
        }

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
                    // Re-renders with the advanced offset; if new PTY data
                    // landed between ticks this also re-bumps (flood glide).
                    self.render_pane(i);
                    if matches!(self.panes.get(i), Some(Some(p)) if p.renderer.scroll_active()) {
                        any = true;
                    }
                }
            }
            any
        }

        /// Render every pane (used for initial paint + theme changes).
        fn render_all(&mut self) {
            for i in 0..self.panes.len() {
                self.render_pane(i);
            }
        }

        /// Render every pane currently showing session `sid`.
        fn render_session(&mut self, sid: usize) {
            for i in 0..self.panes.len() {
                if matches!(self.panes.get(i), Some(Some(p)) if p.session == sid) {
                    self.render_pane(i);
                }
            }
        }

        // ---- Sizing / DPI / theme -----------------------------------------

        /// Resize the bound session's terminal + PTY to a new grid.
        fn reflow_session(&mut self, sid: usize, cols: u16, rows: u16) {
            if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                s.terminal.resize(cols, rows);
                if let Err(e) = s.pty.resize(cols, rows) {
                    log::error!("pty resize failed: {e}");
                }
            }
        }

        /// Resize pane `idx` to `w`x`h` physical px, recompute its grid, reflow the
        /// session it shows, and repaint it.
        fn resize_pane(&mut self, idx: usize, w: u32, h: u32) {
            let dpi = self.dpi_scale;
            let bound = {
                let Some(Some(p)) = self.panes.get_mut(idx) else {
                    return;
                };
                p.apply_size(&self.device, w, h, dpi);
                let (cols, rows) = p.recompute_grid(&self.font, dpi);
                p.glide_mute_until = std::time::Instant::now() + GLIDE_MUTE_AFTER_RESIZE;
                (p.session, cols, rows)
            };
            let (sid, cols, rows) = bound;
            if sid != NO_SESSION {
                self.reflow_session(sid, cols, rows);
            }
            self.render_pane(idx);
        }

        /// Bind session `sid` to pane `idx` and reflow it to the pane's grid.
        fn bind(&mut self, idx: usize, sid: usize) {
            let dims = {
                let Some(Some(p)) = self.panes.get_mut(idx) else {
                    return;
                };
                p.session = sid;
                p.force_full = true;
                (p.cols, p.rows)
            };
            if sid != NO_SESSION {
                self.reflow_session(sid, dims.0, dims.1);
            }
            // Paint now: without this the pane keeps its stale frame (or stays
            // blank) until the session's next PTY output — the "empty Ctrl+J
            // terminal until you type" bug.
            self.render_pane(idx);
        }

        /// Rebuild the font + every pane's renderer for the current
        /// `font_pt` * `dpi_scale`, then recompute grids and reflow + repaint.
        fn rebuild_font(&mut self) {
            self.font.set_size(self.font_pt * self.dpi_scale);
            self.apply_font();
        }

        /// Swap the primary font family (`None` = bundled default), then
        /// rebuild renderers exactly like a size change does.
        fn set_font_family(&mut self, family: Option<&str>) {
            self.font = text::Font::with_family(self.font_pt * self.dpi_scale, family);
            self.apply_font();
        }

        /// Push the current `self.font` metrics into every pane (clears glyph
        /// atlases), recompute grids, reflow bound sessions, repaint.
        fn apply_font(&mut self) {
            let dpi = self.dpi_scale;
            let (cell_w, cell_h, ascent) = (self.font.cell_w, self.font.cell_h, self.font.ascent);
            for i in 0..self.panes.len() {
                if let Some(Some(p)) = self.panes.get_mut(i) {
                    p.renderer.set_font_metrics(cell_w, cell_h, ascent);
                    p.recompute_grid(&self.font, dpi);
                    p.glide_mute_until = std::time::Instant::now() + GLIDE_MUTE_AFTER_RESIZE;
                }
            }
            // Reflow each pane's bound session, then repaint all.
            let binds: Vec<(usize, u16, u16)> = self
                .panes
                .iter()
                .flatten()
                .filter(|p| p.session != NO_SESSION)
                .map(|p| (p.session, p.cols, p.rows))
                .collect();
            for (sid, cols, rows) in binds {
                self.reflow_session(sid, cols, rows);
            }
            self.render_all();
        }

        /// Rebuild for a new DPI scale.
        fn set_scale(&mut self, scale: f32) {
            self.dpi_scale = if scale <= 0.0 { 1.0 } else { scale };
            self.rebuild_font();
        }

        /// Set the base font size in points.
        fn set_font_size(&mut self, pt: f32) {
            if (4.0..=200.0).contains(&pt) {
                self.font_pt = pt;
                self.rebuild_font();
            }
        }

        /// Cursor style override (0 = follow the shell / DECSCUSR, 1 = block,
        /// 2 = bar, 3 = underline) + blink. Blink runs a Win32 timer on the
        /// wakeup window; each tick flips the phase and repaints.
        fn set_cursor(&mut self, style: u8, blink: bool) {
            self.cursor_style = match style {
                1 => Some(term_core::CursorShape::Block),
                2 => Some(term_core::CursorShape::Bar),
                3 => Some(term_core::CursorShape::Underline),
                _ => None,
            };
            if blink != self.cursor_blink {
                self.cursor_blink = blink;
                self.blink_on = true;
                unsafe {
                    if blink {
                        SetTimer(Some(self.wakeup_hwnd), BLINK_TIMER_ID, BLINK_MS, None);
                    } else {
                        let _ = KillTimer(Some(self.wakeup_hwnd), BLINK_TIMER_ID);
                    }
                }
            }
            for p in self.panes.iter_mut().flatten() {
                p.force_full = true;
            }
            self.render_all();
        }

        /// Toggle ligature run shaping on every pane (new panes inherit it).
        fn set_ligatures(&mut self, on: bool) {
            if self.ligatures == on {
                return;
            }
            self.ligatures = on;
            for p in self.panes.iter_mut().flatten() {
                p.renderer.set_ligatures(on);
                p.force_full = true;
            }
            self.render_all();
        }

        /// Set the terminal background (surface clear) color on every pane.
        fn set_bg(&mut self, bg: [u8; 3]) {
            self.bg = bg;
            for p in self.panes.iter_mut().flatten() {
                p.renderer.set_bg(bg);
                p.force_full = true;
            }
            self.render_all();
        }

        /// Set the terminal background alpha (0 = transparent for blur-through).
        fn set_bg_alpha(&mut self, a: f32) {
            self.bg_a = a.clamp(0.0, 1.0);
            for p in self.panes.iter_mut().flatten() {
                p.renderer.set_bg_alpha(self.bg_a);
                p.force_full = true;
            }
            self.render_all();
        }

        /// Apply a color theme: 16 ANSI colors + fg + cursor + selection. The
        /// background is set separately via `set_bg` (it owns the clear color so
        /// blur-through keeps working). Forces a full redraw of every pane.
        fn set_palette(
            &mut self,
            ansi: [[u8; 3]; 16],
            fg: [u8; 3],
            cursor: [u8; 3],
            selection: [u8; 3],
        ) {
            term_core::palette::set_theme(ansi, fg, cursor, selection);
            for p in self.panes.iter_mut().flatten() {
                p.force_full = true;
            }
            self.render_all();
        }

        // ---- Panes (split view) -------------------------------------------

        /// Create a new pane with its own composition swapchain. Returns the stable
        /// pane id and the swapchain pointer (borrowed — the engine keeps it alive)
        /// for C# to bind to a fresh SwapChainPanel.
        fn add_pane(&mut self, kind: PaneKind) -> Result<(usize, *mut c_void)> {
            let swapchain = unsafe { create_comp_swapchain(&self.queue)? };
            let raw = swapchain.as_raw();
            let mut renderer = build_renderer(&self.device, &self.queue, &self.font, self.bg, self.bg_a);
            renderer.set_ligatures(self.ligatures);
            let pane = Pane {
                kind,
                swapchain,
                backbuffers: Vec::new(),
                renderer,
                width: 0,
                height: 0,
                cols: 80,
                rows: 24,
                session: NO_SESSION,
                force_full: true,
                glide_mute_until: std::time::Instant::now(),
            };
            let id = self
                .panes
                .iter()
                .position(|p| p.is_none())
                .unwrap_or(self.panes.len());
            if id == self.panes.len() {
                self.panes.push(Some(pane));
            } else {
                self.panes[id] = Some(pane);
            }
            dbglog(&format!("add_pane: id={id}"));
            Ok((id, raw))
        }

        /// Bind a session to a pane and repaint it (C#-driven, e.g. drop a tab).
        fn pane_bind(&mut self, idx: usize, sid: usize) {
            if matches!(self.panes.get(idx), Some(Some(p)) if p.kind == PaneKind::Editor) {
                return; // the editor pane never shows a session
            }
            if self.sessions.get(sid).and_then(|o| o.as_ref()).is_some() {
                self.bind(idx, sid);
                self.render_pane(idx);
            }
        }

        /// Destroy pane `idx` (releases its swapchain). Pane 0 is the primary
        /// surface and is never destroyed. The session it showed stays alive as a
        /// background tab.
        fn close_pane(&mut self, idx: usize) {
            if idx == 0 || idx == self.editor_pane {
                return;
            }
            if let Some(slot) = self.panes.get_mut(idx) {
                *slot = None;
            }
            if self.focused_pane == idx {
                self.focused_pane = 0;
                self.notify_active_changed();
            }
        }

        // ---- Editor pane ----------------------------------------------------

        /// Create (or return) the editor pane. Idempotent: re-attach hands back
        /// the existing swapchain so C# can rebind after a XAML reload.
        fn editor_attach(&mut self) -> Option<(usize, *mut c_void)> {
            if self.editor_pane != NO_SESSION {
                if let Some(Some(p)) = self.panes.get(self.editor_pane) {
                    return Some((self.editor_pane, p.swapchain.as_raw()));
                }
            }
            match self.add_pane(PaneKind::Editor) {
                Ok((idx, sc)) => {
                    self.editor_pane = idx;
                    Some((idx, sc))
                }
                Err(e) => {
                    dbglog(&format!("editor_attach failed: {e}"));
                    None
                }
            }
        }

        /// Diff dirty state against the last notification and emit flips.
        fn notify_editor_dirty(&mut self) {
            let now = self.workspace.dirty_ids();
            if now == self.last_dirty {
                return;
            }
            if let Some(f) = self.cb.on_editor_dirty {
                for id in now.iter().filter(|i| !self.last_dirty.contains(i)) {
                    f(self.cb.ctx, *id, 1);
                }
                for id in self.last_dirty.iter().filter(|i| !now.contains(i)) {
                    f(self.cb.ctx, *id, 0);
                }
            }
            self.last_dirty = now;
        }

        /// Editor key handling. Returns true when handled (C# swallows the
        /// matching WM_CHAR). Printable chars arrive via `editor_char`.
        fn editor_key(&mut self, vk: u16, mods: u32) -> bool {
            use editor::Move;
            let ctrl = mods & 1 != 0;
            let shift = mods & 2 != 0;
            let rows = match self.panes.get(self.editor_pane) {
                Some(Some(p)) => p.rows as usize,
                _ => 24,
            };
            let hwnd = self.wakeup_hwnd;
            let Some(doc) = self.workspace.doc_mut() else {
                return false;
            };
            let handled = match (vk, ctrl) {
                (0x25, false) => { doc.move_cursor(Move::Left, shift); true }
                (0x27, false) => { doc.move_cursor(Move::Right, shift); true }
                (0x25, true) => { doc.move_cursor(Move::WordLeft, shift); true }
                (0x27, true) => { doc.move_cursor(Move::WordRight, shift); true }
                (0x26, _) => { doc.move_cursor(Move::Up, shift); true }
                (0x28, _) => { doc.move_cursor(Move::Down, shift); true }
                (0x24, false) => { doc.move_cursor(Move::Home, shift); true }
                (0x23, false) => { doc.move_cursor(Move::End, shift); true }
                (0x24, true) => { doc.move_cursor(Move::DocStart, shift); true }
                (0x23, true) => { doc.move_cursor(Move::DocEnd, shift); true }
                (0x21, _) => { doc.move_cursor(Move::PageUp(rows), shift); true }
                (0x22, _) => { doc.move_cursor(Move::PageDown(rows), shift); true }
                (0x08, _) => { doc.backspace(); true }
                (0x2E, _) => { doc.delete(); true }
                (0x0D, _) => { doc.insert("\n"); true }
                (0x09, _) => { doc.insert("\t"); true }
                (0x5A, true) if shift => { doc.redo(); true } // Ctrl+Shift+Z
                (0x5A, true) => { doc.undo(); true }          // Ctrl+Z
                (0x59, true) => { doc.redo(); true }          // Ctrl+Y
                (0x53, true) => {                             // Ctrl+S
                    let _ = doc.save();
                    true
                }
                (0x43, true) => {                             // Ctrl+C
                    if let Some(t) = doc.selection_text() {
                        clipboard_set_text(hwnd, &t);
                    }
                    true
                }
                (0x58, true) => {                             // Ctrl+X
                    if let Some(t) = doc.selection_text() {
                        clipboard_set_text(hwnd, &t);
                        doc.backspace(); // deletes the selection
                    }
                    true
                }
                (0x56, true) => {                             // Ctrl+V
                    if let Some(t) = clipboard_get_text(hwnd) {
                        doc.insert(&t.replace("\r\n", "\n"));
                    }
                    true
                }
                (0x41, true) => {                             // Ctrl+A
                    doc.move_cursor(Move::DocStart, false);
                    doc.move_cursor(Move::DocEnd, true);
                    true
                }
                _ => false,
            };
            if handled {
                self.notify_editor_dirty();
                let ep = self.editor_pane;
                self.render_pane(ep);
            }
            handled
        }

        fn editor_char(&mut self, cu: u32) {
            // Skip control chars (Enter/Tab/Backspace arrive via editor_key;
            // Ctrl+letter WM_CHARs must not insert control bytes).
            let Some(c) = char::from_u32(cu) else { return };
            if c.is_control() {
                return;
            }
            if self.workspace.doc_mut().is_none() {
                return;
            }
            if let Some(doc) = self.workspace.doc_mut() {
                doc.insert(&c.to_string());
            }
            self.notify_editor_dirty();
            let ep = self.editor_pane;
            self.render_pane(ep);
        }

        /// Editor pointer event: place the cursor (down) or extend a drag
        /// selection (move; C# only sends moves while the button is held).
        fn editor_mouse(&mut self, kind: u32, x: f32, y: f32) {
            let (cols, rows) = match self.panes.get(self.editor_pane) {
                Some(Some(p)) => (p.cols, p.rows),
                _ => return,
            };
            let pad = (PAD_LOGICAL_PX * self.dpi_scale).round();
            let col =
                (((x - pad) / self.font.cell_w).max(0.0) as u16).min(cols.saturating_sub(1));
            let row =
                (((y - pad) / self.font.cell_h).max(0.0) as u16).min(rows.saturating_sub(1));
            let Some((line, dcol)) = self.workspace.hit(col, row) else {
                return;
            };
            if let Some(doc) = self.workspace.doc_mut() {
                match kind {
                    0 => doc.set_cursor(line, dcol),
                    1 => doc.select_to(line, dcol),
                    _ => return,
                }
            }
            let ep = self.editor_pane;
            self.render_pane(ep);
        }

        /// Set the pane that receives keyboard input + legacy mouse/resize.
        fn set_focused_pane(&mut self, idx: usize) {
            if matches!(self.panes.get(idx), Some(Some(_))) && self.focused_pane != idx {
                self.focused_pane = idx;
                self.notify_active_changed();
            }
        }

        // ---- Session lifecycle --------------------------------------------

        /// Spawn a new PowerShell tab. Does not bind it to a pane — C# follows with
        /// `velo_tab_set_active` (single-pane) or `velo_pane_bind` (split).
        fn spawn_session(&mut self) -> Result<u32> {
            let id = self
                .sessions
                .iter()
                .position(|s| s.is_none())
                .unwrap_or(self.sessions.len());

            // New shells size to the focused pane's grid (fallback 80x24).
            let (cols, rows) = self.pane_cols_rows(self.focused_pane).unwrap_or((80, 24));

            let inbox = Arc::new(Mutex::new(Vec::<u8>::new()));
            let wakeup_pending = Arc::new(std::sync::atomic::AtomicBool::new(false));
            let on_event = Self::make_on_event(
                id,
                inbox.clone(),
                wakeup_pending.clone(),
                self.wakeup_hwnd.0 as isize,
            );
            dbglog(&format!("spawn_session: id={id}, shell={}, {cols}x{rows}", self.shell));
            let pty = if self.recovery {
                match crate::host_client::spawn_remote(&self.shell, cols, rows, on_event) {
                    Ok(r) => PtyHandle::Remote(r),
                    Err(e) => {
                        dbglog(&format!("spawn_session: host spawn failed ({e}); using local pty"));
                        let ev2 = Self::make_on_event(
                            id,
                            inbox.clone(),
                            wakeup_pending.clone(),
                            self.wakeup_hwnd.0 as isize,
                        );
                        PtyHandle::Local(pty_win::spawn(&self.shell, cols, rows, ev2)?)
                    }
                }
            } else {
                PtyHandle::Local(match pty_win::spawn(&self.shell, cols, rows, on_event) {
                    Ok(p) => p,
                    Err(e) => {
                        dbglog(&format!("spawn_session: pty spawn FAILED: {e}"));
                        return Err(e);
                    }
                })
            };
            self.install_session(id, pty, inbox, wakeup_pending, cols, rows);
            Ok(id as u32)
        }

        /// Reattach to a session still alive in velo-pty-host (from a previous
        /// run). The host replays its output ring so the screen repopulates.
        fn adopt_session(&mut self, host_id: u32) -> Result<u32> {
            let id = self
                .sessions
                .iter()
                .position(|s| s.is_none())
                .unwrap_or(self.sessions.len());
            let (cols, rows) = self.pane_cols_rows(self.focused_pane).unwrap_or((80, 24));
            let inbox = Arc::new(Mutex::new(Vec::<u8>::new()));
            let wakeup_pending = Arc::new(std::sync::atomic::AtomicBool::new(false));
            let on_event = Self::make_on_event(
                id,
                inbox.clone(),
                wakeup_pending.clone(),
                self.wakeup_hwnd.0 as isize,
            );
            dbglog(&format!("adopt_session: id={id}, host_id={host_id}"));
            let remote = crate::host_client::attach_remote(host_id, on_event)?;
            let _ = remote.resize(cols, rows);
            self.install_session(id, PtyHandle::Remote(remote), inbox, wakeup_pending, cols, rows);
            Ok(id as u32)
        }

        /// Reader-thread event sink: append to the inbox and poke the UI loop.
        fn make_on_event(
            id: usize,
            reader_inbox: Arc<Mutex<Vec<u8>>>,
            reader_wakeup_pending: Arc<std::sync::atomic::AtomicBool>,
            hwnd_val: isize,
        ) -> impl FnMut(pty_win::PtyEvent) + Send + 'static {
            move |ev: pty_win::PtyEvent| {
                let hwnd = HWND(hwnd_val as *mut c_void);
                match ev {
                    pty_win::PtyEvent::Data(b) => {
                        reader_inbox.lock().extend_from_slice(&b);
                        // Only post if no wakeup is already in flight — a queued
                        // MSG_PTY_DATA will drain everything appended before it's
                        // handled, so extra posts during a flood are redundant.
                        if !reader_wakeup_pending.swap(true, std::sync::atomic::Ordering::AcqRel) {
                            unsafe {
                                let _ =
                                    PostMessageW(Some(hwnd), MSG_PTY_DATA, WPARAM(id), LPARAM(0));
                            }
                        }
                    }
                    pty_win::PtyEvent::Eof => unsafe {
                        let _ = PostMessageW(Some(hwnd), MSG_PTY_EOF, WPARAM(id), LPARAM(0));
                    },
                }
            }
        }

        fn install_session(
            &mut self,
            id: usize,
            pty: PtyHandle,
            inbox: Arc<Mutex<Vec<u8>>>,
            wakeup_pending: Arc<std::sync::atomic::AtomicBool>,
            cols: u16,
            rows: u16,
        ) {
            let writer = pty.writer();
            let terminal = term_core::Terminal::new(
                cols,
                rows,
                Arc::new(move |b: &[u8]| {
                    let _ = writer.write_all(b);
                }),
            );
            let session = Box::new(Session {
                terminal,
                pty,
                inbox,
                wakeup_pending,
                tab_title: "PowerShell".to_string(),
            });
            if id == self.sessions.len() {
                self.sessions.push(Some(session));
            } else {
                self.sessions[id] = Some(session);
            }
            self.tab_order.push(id);
        }

        /// Close a session: drop it (its `Pty::drop` joins the reader), remove its
        /// tab, and unbind any pane showing it. The C# shell owns pane layout, so it
        /// rebinds panes / reflows after this. Returns true when no tabs remain.
        fn close_session(&mut self, id: usize) -> bool {
            let Some(index) = self.tab_order.iter().position(|&x| x == id) else {
                return self.tab_order.is_empty();
            };
            if let Some(slot) = self.sessions.get_mut(id) {
                // Remote sessions detach on drop — a user close must really
                // end the child, so tell the host first.
                if let Some(s) = slot.as_deref() {
                    s.pty.kill();
                }
                *slot = None;
            }
            self.tab_order.remove(index);
            for p in self.panes.iter_mut().flatten() {
                if p.session == id {
                    p.session = NO_SESSION;
                    p.force_full = true;
                }
            }
            self.tab_order.is_empty()
        }

        /// Switch the focused pane to a session by stable id.
        fn set_active(&mut self, id: usize) {
            if self.sessions.get(id).and_then(|o| o.as_ref()).is_some() {
                let idx = self.focused_pane;
                self.bind(idx, id);
                self.render_pane(idx);
            }
        }

        /// Push session `id`'s live OSC title to C# if it changed.
        fn refresh_title(&mut self, id: usize) {
            let title = match self.sessions.get(id).and_then(|o| o.as_ref()) {
                Some(s) => s
                    .terminal
                    .title()
                    .unwrap_or_else(|| "PowerShell".to_string()),
                None => return,
            };
            if let Some(s) = self.sessions.get_mut(id).and_then(|o| o.as_deref_mut()) {
                if s.tab_title != title {
                    s.tab_title = title.clone();
                    if let Some(f) = self.cb.on_title_changed {
                        let utf16: Vec<u16> = title.encode_utf16().collect();
                        f(self.cb.ctx, id as u32, utf16.as_ptr(), utf16.len());
                    }
                }
            }
        }

        fn notify_active_changed(&self) {
            if let Some(f) = self.cb.on_active_changed {
                f(self.cb.ctx, self.focused_session() as u32);
            }
        }

        /// Map focused-pane physical px to a grid cell + which half (selection side).
        fn pixel_to_cell(&self, x: f32, y: f32, cols: u16, rows: u16) -> (u16, u16, bool) {
            // Subtract the inner padding so clicks map to the right cell.
            let pad = (PAD_LOGICAL_PX * self.dpi_scale).round();
            let x = x - pad;
            let y = y - pad;
            let col = (x / self.font.cell_w).max(0.0) as u16;
            let row = (y / self.font.cell_h).max(0.0) as u16;
            let col = col.min(cols.saturating_sub(1));
            let row = row.min(rows.saturating_sub(1));
            let left_half = (x - col as f32 * self.font.cell_w) < self.font.cell_w * 0.5;
            (col, row, left_half)
        }

        /// Drain a session's inbox, parse it, refresh its title, repaint any pane
        /// that shows it.
        fn on_pty_data(&mut self, id: usize) {
            let bytes = match self.sessions.get(id).and_then(|o| o.as_ref()) {
                Some(s) => {
                    // Clear the pending flag before draining: if the reader appends
                    // and flips false->true after we've read the (now-stale) flag
                    // but before we drain, it re-posts a wakeup for that data. Clear
                    // first, drain second — the reverse order could drop a wakeup for
                    // data that arrives between drain and clear.
                    s.wakeup_pending.store(false, std::sync::atomic::Ordering::Release);
                    std::mem::take(&mut *s.inbox.lock())
                }
                None => return,
            };
            if bytes.is_empty() {
                return;
            }
            let events = if let Some(s) = self.sessions.get_mut(id).and_then(|o| o.as_deref_mut()) {
                s.terminal.advance(&bytes);
                s.terminal.take_shell_events()
            } else {
                Vec::new()
            };
            for ev in events {
                self.notify_shell_event(id, ev);
            }
            self.refresh_title(id);
            self.render_session(id);
        }

        /// Forward a decoded OSC 7 / 133 event to the C# shell.
        fn notify_shell_event(&self, id: usize, ev: term_core::ShellEvent) {
            match ev {
                term_core::ShellEvent::Cwd(cwd) => {
                    if let Some(f) = self.cb.on_cwd_changed {
                        let u: Vec<u16> = cwd.encode_utf16().collect();
                        f(self.cb.ctx, id as u32, u.as_ptr(), u.len());
                    }
                }
                term_core::ShellEvent::Command {
                    phase,
                    exit,
                    dur_ms,
                    text,
                } => {
                    if let Some(f) = self.cb.on_command {
                        let u: Vec<u16> = text.encode_utf16().collect();
                        f(self.cb.ctx, id as u32, phase, exit, dur_ms, u.as_ptr(), u.len());
                    }
                }
            }
        }

        /// App keybinds + navigation keys. Returns `true` = handled (C# sets
        /// `e.Handled` and the matching `velo_char` is swallowed). Tab-management
        /// chords call back into C# (it owns the tab list); terminal-local actions
        /// (copy/paste, nav keys) are handled here. `ctrl`/`shift`/`alt` are the
        /// live modifier states C# reads from the keyboard source.
        fn on_key(&mut self, vk: u16, ctrl: bool, shift: bool, alt: bool) -> bool {
            if ctrl && shift {
                match vk {
                    0x54 => {
                        if let Some(f) = self.cb.on_new_tab_requested {
                            f(self.cb.ctx);
                        }
                        self.swallow_next_char = true;
                        return true;
                    }
                    0x57 => {
                        if let Some(f) = self.cb.on_close_tab_requested {
                            f(self.cb.ctx, self.focused_session() as u32);
                        }
                        self.swallow_next_char = true;
                        return true;
                    }
                    0x43 => {
                        self.copy();
                        self.swallow_next_char = true;
                        return true;
                    }
                    0x56 => {
                        self.paste();
                        self.swallow_next_char = true;
                        return true;
                    }
                    _ if vk == VK_TAB.0 => {
                        if let Some(f) = self.cb.on_switch_tab_requested {
                            f(self.cb.ctx, false);
                        }
                        self.swallow_next_char = true;
                        return true;
                    }
                    _ => {}
                }
            }
            if ctrl && !shift && vk == VK_TAB.0 {
                if let Some(f) = self.cb.on_switch_tab_requested {
                    f(self.cb.ctx, true);
                }
                self.swallow_next_char = true;
                return true;
            }
            if ctrl && !shift && vk == 0x56 {
                self.paste();
                self.swallow_next_char = true;
                return true;
            }

            // Shift+PgUp/PgDn scrolls scrollback a page instead of sending a
            // byte sequence, unless the alt screen (vim/less) is active —
            // there's no scrollback to show there, so fall through to the
            // normal PgUp/PgDn escape sequence below.
            if shift && !ctrl && !alt && (vk == VK_PRIOR.0 || vk == VK_NEXT.0) {
                let idx = self.focused_pane;
                let alt_screen = self
                    .active_session()
                    .map(|s| s.terminal.alt_screen_active())
                    .unwrap_or(false);
                if !alt_screen {
                    let rows = self.pane_cols_rows(idx).map(|(_, r)| r as i32).unwrap_or(24);
                    let delta = if vk == VK_PRIOR.0 { rows } else { -rows };
                    if let Some(s) = self.active_session() {
                        s.terminal.scroll_display(delta);
                    }
                    self.render_pane(idx);
                    return true;
                }
            }

            if let Some(s) = self.active_session() {
                let app_cursor = s.terminal.app_cursor();
                if let Some(seq) = term_core::keys::key_seq(vk, ctrl, shift, alt, app_cursor) {
                    s.terminal.scroll_to_bottom();
                    let _ = s.pty.writer().write_all(&seq);
                    return true;
                }
            }

            false
        }

        /// Text + control chars -> focused PTY (UTF-16 -> UTF-8, surrogate-aware).
        /// `alt` is the live Alt-without-Ctrl state C# reads at char time; when
        /// set, the bytes are prefixed with `ESC` (classic Alt/meta encoding).
        /// Live state (not a flag carried over from `on_key`) so a swallowed
        /// WM_CHAR or IME commit can never inherit a stale prefix.
        fn on_char(&mut self, cu: u16, alt: bool) {
            if self.swallow_next_char {
                self.swallow_next_char = false;
                return;
            }
            if cu == 0x08 {
                if let Some(s) = self.active_session() {
                    s.terminal.scroll_to_bottom();
                    let w = s.pty.writer();
                    if alt {
                        let _ = w.write_all(b"\x1b");
                    }
                    let _ = w.write_all(b"\x7f");
                }
                return;
            }
            if cu == 0x7f {
                // Ctrl+Backspace arrives as the DEL char; send ^W so the shell
                // kills the previous word (PSReadLine, bash, zsh all bind it).
                if let Some(s) = self.active_session() {
                    s.terminal.scroll_to_bottom();
                    let w = s.pty.writer();
                    if alt {
                        let _ = w.write_all(b"\x1b");
                    }
                    let _ = w.write_all(b"\x17");
                }
                return;
            }
            let units: Vec<u16> = match self.pending_high.take() {
                Some(hi) => vec![hi, cu],
                None => {
                    if (0xD800..0xDC00).contains(&cu) {
                        self.pending_high = Some(cu);
                        return;
                    }
                    vec![cu]
                }
            };
            let s = String::from_utf16_lossy(&units);
            if let Some(sess) = self.active_session() {
                sess.terminal.scroll_to_bottom();
                let w = sess.pty.writer();
                if alt {
                    let _ = w.write_all(b"\x1b");
                }
                let _ = w.write_all(s.as_bytes());
            }
        }

        /// Pointer events for pane `idx`. `kind`: 0 = down, 1 = move, 2 = up.
        /// `(x, y)` are physical px inside that pane; `button` is the SGR
        /// button code (0 left, 1 middle, 2 right). `shift`: shift key held,
        /// the user's override to force local text selection even when the app
        /// has enabled mouse reporting. When the session's terminal reports
        /// mouse mode active and shift is not held, pointer events are encoded
        /// as SGR (1006) sequences and written to the PTY instead of driving
        /// selection; XAML owns pointer capture + focus either way. Which path
        /// a press takes is latched for the whole press→move→release gesture.
        fn on_mouse_pane(&mut self, idx: usize, kind: u32, x: f32, y: f32, button: u8, shift: bool) {
            let (cols, rows) = match self.pane_cols_rows(idx) {
                Some(cr) => cr,
                None => return,
            };
            let sid = match self.panes.get(idx) {
                Some(Some(p)) => p.session,
                _ => return,
            };
            if sid == NO_SESSION {
                return;
            }

            let mm = self
                .sessions
                .get(sid)
                .and_then(|o| o.as_ref())
                .map(|s| s.terminal.mouse_mode())
                .unwrap_or_default();
            // Only report when the app also asked for SGR (1006) coords; a
            // legacy X10-only app would get sequences it can't parse, so it
            // falls back to local selection instead.
            let reporting = mm.reporting && mm.sgr;
            let sgr = match kind {
                0 => {
                    let m = reporting && !shift;
                    self.sgr_gesture = m;
                    m
                }
                // Mid-gesture events follow the press's decision, not live Shift.
                _ if self.mouse_down => self.sgr_gesture,
                // Hover motion (no gesture in flight): live check.
                _ => reporting && !shift,
            };
            if sgr {
                let (c, r, _) = self.pixel_to_cell(x, y, cols, rows);
                let col = c + 1;
                let row = r + 1;
                let seq = match kind {
                    0 => {
                        self.mouse_down = true;
                        self.mouse_button = button;
                        // Drop any highlight left over from an earlier local
                        // selection; this press belongs to the app.
                        if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                            s.terminal.clear_selection();
                        }
                        self.render_pane(idx);
                        Some(term_core::keys::sgr_mouse_seq(button, col, row, true))
                    }
                    1 => {
                        if mm.motion || (mm.drag && self.mouse_down) {
                            // xterm: motion with no button held reports button 3.
                            let b = if self.mouse_down { self.mouse_button } else { 3 };
                            Some(term_core::keys::sgr_mouse_seq(b + 32, col, row, true))
                        } else {
                            None
                        }
                    }
                    _ => {
                        if !self.mouse_down {
                            None // no press was sent; don't fabricate a release
                        } else {
                            self.mouse_down = false;
                            Some(term_core::keys::sgr_mouse_seq(self.mouse_button, col, row, false))
                        }
                    }
                };
                if let Some(seq) = seq {
                    if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                        let _ = s.pty.writer().write_all(&seq);
                    }
                }
                return;
            }

            match kind {
                0 => {
                    // Only track left-button presses for click-to-open-link;
                    // right/middle release must not open a link.
                    if button == 0 {
                        self.press_pos = Some((x, y));
                    }
                    let (c, r, l) = self.pixel_to_cell(x, y, cols, rows);
                    if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                        s.terminal.start_selection(c, r, l);
                    }
                    self.mouse_down = true;
                    self.render_pane(idx);
                }
                1 => {
                    if self.mouse_down {
                        let (c, r, l) = self.pixel_to_cell(x, y, cols, rows);
                        if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                            s.terminal.update_selection(c, r, l);
                        }
                        self.render_pane(idx);
                    } else {
                        let (c, r, _) = self.pixel_to_cell(x, y, cols, rows);
                        let over = self
                            .sessions
                            .get(sid)
                            .and_then(|o| o.as_ref())
                            .and_then(|s| s.terminal.link_at(c, r))
                            .is_some();
                        if over != self.link_hover {
                            self.link_hover = over;
                            if let Some(f) = self.cb.on_link_hover {
                                f(self.cb.ctx, over as u8);
                            }
                        }
                    }
                }
                3 => {
                    // Pointer left the pane: drop any pending click and stop
                    // reporting a hover, but leave selection state untouched.
                    self.press_pos = None;
                    if self.link_hover {
                        self.link_hover = false;
                        if let Some(f) = self.cb.on_link_hover {
                            f(self.cb.ctx, 0);
                        }
                    }
                }
                _ => {
                    let clicked = self.press_pos.take().is_some_and(|(px, py)| {
                        (px - x).abs() < 4.0 && (py - y).abs() < 4.0
                    });
                    if clicked {
                        let (c, r, _) = self.pixel_to_cell(x, y, cols, rows);
                        if let Some(link) = self
                            .sessions
                            .get(sid)
                            .and_then(|o| o.as_ref())
                            .and_then(|s| s.terminal.link_at(c, r))
                        {
                            if let Some(f) = self.cb.on_open_link {
                                let w: Vec<u16> = link.target.encode_utf16().collect();
                                f(self.cb.ctx, w.as_ptr(), w.len());
                            }
                        }
                    }
                    self.press_pos = None;
                    self.mouse_down = false;
                }
            }
        }

        /// Paste clipboard text into the focused PTY (CR-normalized; bracketed wrap).
        fn paste(&mut self) {
            let Some(text) = clipboard_get_text(self.wakeup_hwnd) else {
                return;
            };
            if text.is_empty() {
                return;
            }
            let normalized = text.replace("\r\n", "\r").replace('\n', "\r");
            if let Some(s) = self.active_session() {
                s.terminal.scroll_to_bottom();
                let bracketed = s.terminal.bracketed_paste();
                let w = s.pty.writer();
                if bracketed {
                    let _ = w.write_all(b"\x1b[200~");
                }
                let _ = w.write_all(normalized.as_bytes());
                if bracketed {
                    let _ = w.write_all(b"\x1b[201~");
                }
            }
        }

        /// Paste a UTF-16 string handed in by C# (bypasses the OS clipboard).
        fn paste_text(&mut self, text: &str) {
            if text.is_empty() {
                return;
            }
            let normalized = text.replace("\r\n", "\r").replace('\n', "\r");
            if let Some(s) = self.active_session() {
                s.terminal.scroll_to_bottom();
                let bracketed = s.terminal.bracketed_paste();
                let w = s.pty.writer();
                if bracketed {
                    let _ = w.write_all(b"\x1b[200~");
                }
                let _ = w.write_all(normalized.as_bytes());
                if bracketed {
                    let _ = w.write_all(b"\x1b[201~");
                }
            }
        }

        /// Mouse wheel over pane `idx`. `(x, y)` are physical px inside the pane
        /// (used to place the SGR wheel event when mouse reporting is active).
        /// `shift`: shift key held, the user's override to force scrollback/
        /// arrow-key behavior even when the app has enabled mouse reporting.
        /// Priority: (1) app mouse reporting (not shift) sends SGR wheel button
        /// 64/65, one event per notch; (2) alt screen (e.g. vim, less — no
        /// scrollback there) translates the wheel into arrow-key sequences;
        /// (3) otherwise scroll the pane's scrollback.
        fn pane_scroll(&mut self, idx: usize, delta_lines: i32, x: f32, y: f32, shift: bool) {
            let sid = match self.panes.get(idx) {
                Some(Some(p)) => p.session,
                _ => return,
            };
            if sid == NO_SESSION || delta_lines == 0 {
                return;
            }

            let mouse_mode = self
                .sessions
                .get(sid)
                .and_then(|o| o.as_ref())
                .map(|s| s.terminal.mouse_mode());
            if let Some(mm) = mouse_mode {
                // SGR-capable reporting only, same as on_mouse_pane.
                if mm.reporting && mm.sgr && !shift {
                    if let Some((cols, rows)) = self.pane_cols_rows(idx) {
                        let (c, r, _) = self.pixel_to_cell(x, y, cols, rows);
                        let col = c + 1;
                        let row = r + 1;
                        // `delta_lines` carries the caller's lines-per-notch scale
                        // (3); one wheel event per notch, not per line.
                        let notches = delta_lines.unsigned_abs().div_ceil(NOTCH_LINES as u32).max(1);
                        let button = if delta_lines > 0 { 64 } else { 65 };
                        let seq = term_core::keys::sgr_mouse_seq(button, col, row, true);
                        if let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) {
                            let w = s.pty.writer();
                            for _ in 0..notches {
                                let _ = w.write_all(&seq);
                            }
                        }
                    }
                    return;
                }
            }

            let Some(s) = self.sessions.get_mut(sid).and_then(|o| o.as_deref_mut()) else {
                return;
            };
            if s.terminal.alt_screen_active() {
                // `delta_lines` already carries the caller's lines-per-notch
                // scale (3, matching the non-alt-screen scroll amount); one
                // arrow key per line keeps wheel "speed" consistent between
                // the two modes.
                let seq: &[u8] = if delta_lines > 0 { b"\x1b[A" } else { b"\x1b[B" };
                let mut bytes = Vec::with_capacity(seq.len() * delta_lines.unsigned_abs() as usize);
                for _ in 0..delta_lines.unsigned_abs() {
                    bytes.extend_from_slice(seq);
                }
                let _ = s.pty.writer().write_all(&bytes);
            } else {
                s.terminal.scroll_display(delta_lines);
                self.render_pane(idx);
            }
        }

        /// Copy the focused session's selection to the clipboard.
        fn copy(&mut self) {
            let text = self
                .active_session()
                .and_then(|s| s.terminal.selection_text());
            if let Some(t) = text {
                if !t.is_empty() {
                    clipboard_set_text(self.wakeup_hwnd, &t);
                }
            }
        }
    }

    /// Subclass proc for the engine's message-only wakeup window. `dwref` carries
    /// `*mut Engine`. Handles only the PTY reader-thread wakeups; all input and
    /// sizing now arrive through the C ABI from the SwapChainPanel host.
    unsafe extern "system" fn wakeup_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _id: usize,
        dwref: usize,
    ) -> LRESULT {
        let ptr = dwref as *mut Engine;
        if ptr.is_null() {
            return DefSubclassProc(hwnd, msg, wparam, lparam);
        }
        let eng = &mut *ptr;
        match msg {
            MSG_PTY_DATA => {
                eng.on_pty_data(wparam.0);
                LRESULT(0)
            }
            WM_TIMER if wparam.0 == BLINK_TIMER_ID => {
                // ponytail: full repaint per blink tick; damage just the cursor
                // row if this ever shows up in profiles.
                eng.blink_on = !eng.blink_on;
                for p in eng.panes.iter_mut().flatten() {
                    p.force_full = true;
                }
                eng.render_all();
                LRESULT(0)
            }
            MSG_PTY_EOF => {
                let id = wparam.0 as u32;
                dbglog(&format!("MSG_PTY_EOF: session {id} ended (shell exited)"));
                eng.close_session(wparam.0);
                if let Some(f) = eng.cb.on_tab_closed {
                    f(eng.cb.ctx, id);
                }
                LRESULT(0)
            }
            _ => DefSubclassProc(hwnd, msg, wparam, lparam),
        }
    }

    /// Read clipboard text (`CF_UNICODETEXT`) as a `String`, if present.
    fn clipboard_get_text(hwnd: HWND) -> Option<String> {
        unsafe {
            OpenClipboard(Some(hwnd)).ok()?;
            let result = (|| {
                let handle = GetClipboardData(CF_UNICODETEXT).ok()?;
                let hglobal = HGLOBAL(handle.0);
                let ptr = GlobalLock(hglobal) as *const u16;
                if ptr.is_null() {
                    return None;
                }
                let mut len = 0isize;
                while *ptr.offset(len) != 0 {
                    len += 1;
                }
                let slice = std::slice::from_raw_parts(ptr, len as usize);
                let s = String::from_utf16_lossy(slice);
                let _ = GlobalUnlock(hglobal);
                Some(s)
            })();
            let _ = CloseClipboard();
            result
        }
    }

    /// Put text on the clipboard as `CF_UNICODETEXT`.
    fn clipboard_set_text(hwnd: HWND, text: &str) {
        let utf16: Vec<u16> = text.encode_utf16().chain([0]).collect();
        unsafe {
            if OpenClipboard(Some(hwnd)).is_err() {
                return;
            }
            let _ = EmptyClipboard();
            if let Ok(hglobal) = GlobalAlloc(GMEM_MOVEABLE, utf16.len() * 2) {
                let dst = GlobalLock(hglobal) as *mut u16;
                if !dst.is_null() {
                    std::ptr::copy_nonoverlapping(utf16.as_ptr(), dst, utf16.len());
                    let _ = GlobalUnlock(hglobal);
                    let _ = SetClipboardData(CF_UNICODETEXT, Some(HANDLE(hglobal.0)));
                }
            }
            let _ = CloseClipboard();
        }
    }

    // ---- C ABI ------------------------------------------------------------

    /// ponytail: temp-file logger to debug the black-screen/self-close bug (a
    /// WinExe has no console, so println!/log:: are invisible). Shares
    /// %TEMP%\velo-debug.log with the C# side. Drop once the bug is found.
    fn dbglog(msg: &str) {
        use std::io::Write;
        // Same file as the C# side: process working dir (repo root under
        // `dotnet run`), falling back to %TEMP%.
        static LOG_PATH: std::sync::OnceLock<std::path::PathBuf> = std::sync::OnceLock::new();
        let path = LOG_PATH.get_or_init(|| {
            std::env::current_dir()
                .unwrap_or_else(|_| std::env::temp_dir())
                .join("velo-debug.log")
        });
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
            let _ = writeln!(f, "[rust] {msg}");
        }
    }

    fn install_panic_hook() {
        use std::sync::Once;
        static ONCE: Once = Once::new();
        ONCE.call_once(|| {
            let prev = std::panic::take_hook();
            std::panic::set_hook(Box::new(move |info| {
                dbglog(&format!("PANIC: {info}"));
                prev(info);
            }));
        });
    }

    /// Init the GPU + composition swapchain and the engine's internal wakeup
    /// window. Returns an opaque engine handle (null on failure). `dpi_scale` is
    /// the panel's rasterization scale. After this, call `velo_get_swapchain` to
    /// bind the swapchain to a `SwapChainPanel`, then `velo_resize` once the panel
    /// has a size.
    ///
    /// # Safety
    /// Must be called on the UI thread (the wakeup window's messages are pumped
    /// there). The returned handle is freed by `velo_shutdown`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_attach(dpi_scale: f32) -> *mut Engine {
        install_panic_hook();
        dbglog(&format!("velo_attach: enter, scale={dpi_scale}"));
        let comp = match init_composition() {
            Ok(c) => c,
            Err(e) => {
                log::error!("velo_attach init_composition failed: {e}");
                dbglog(&format!("velo_attach: init_composition FAILED: {e}"));
                return std::ptr::null_mut();
            }
        };
        dbglog("velo_attach: init_composition ok");
        let Composition {
            device,
            queue,
            swapchain,
        } = comp;

        let wakeup_hwnd = create_wakeup_window();
        if wakeup_hwnd.0.is_null() {
            log::error!("velo_attach: create_wakeup_window failed");
            dbglog("velo_attach: create_wakeup_window FAILED");
            return std::ptr::null_mut();
        }
        dbglog("velo_attach: wakeup window ok, building renderer");

        let scale = if dpi_scale <= 0.0 { 1.0 } else { dpi_scale };
        let font = text::Font::new(FONT_SIZE_PT * scale);
        let bg = [0x1e, 0x1e, 0x1e];
        let bg_a = 1.0;
        let renderer = build_renderer(&device, &queue, &font, bg, bg_a);

        // Pane 0 reuses the primary swapchain so the single-pane host is unchanged.
        let pane0 = Pane {
            kind: PaneKind::Terminal,
            swapchain,
            backbuffers: Vec::new(),
            renderer,
            width: 0,
            height: 0,
            cols: 80,
            rows: 24,
            session: NO_SESSION,
            force_full: true,
            glide_mute_until: std::time::Instant::now(),
        };

        let engine = Box::new(Engine {
            wakeup_hwnd,
            device,
            queue,
            font,
            dpi_scale: scale,
            font_pt: FONT_SIZE_PT,
            shell: "powershell.exe".to_string(),
            recovery: false,
            bg,
            bg_a,
            ligatures: true,
            cursor_style: None,
            cursor_blink: false,
            blink_on: true,
            panes: vec![Some(pane0)],
            focused_pane: 0,
            sessions: Vec::new(),
            tab_order: Vec::new(),
            workspace: editor::Workspace::default(),
            editor_pane: NO_SESSION,
            last_dirty: Vec::new(),
            mouse_down: false,
            mouse_button: 0,
            sgr_gesture: false,
            swallow_next_char: false,
            pending_high: None,
            press_pos: None,
            link_hover: false,
            cb: VeloCallbacks::default(),
        });

        let raw = Box::into_raw(engine);
        let _ = SetWindowSubclass(wakeup_hwnd, Some(wakeup_proc), SUBCLASS_ID, raw as usize);
        dbglog(&format!("velo_attach: success, engine={raw:?}"));
        raw
    }

    /// Return the primary pane's swapchain pointer (borrowed — the engine keeps it
    /// alive). C# casts the `SwapChainPanel` to `ISwapChainPanelNative` and calls
    /// `SetSwapChain(this)`, which AddRefs it.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_get_swapchain(eng: *mut Engine) -> *mut c_void {
        match eng.as_ref() {
            Some(e) => match e.panes.first().and_then(|o| o.as_ref()) {
                Some(p) => p.swapchain.as_raw(),
                None => std::ptr::null_mut(),
            },
            None => std::ptr::null_mut(),
        }
    }

    /// Resize the primary pane (pane 0) to `w`x`h` physical px and reflow.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_resize(eng: *mut Engine, w: u32, h: u32) {
        if let Some(e) = eng.as_mut() {
            e.resize_pane(0, w, h);
        }
    }

    /// Draw + present every pane immediately.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_render(eng: *mut Engine) {
        if let Some(e) = eng.as_mut() {
            e.render_all();
        }
    }

    /// Forward a key-down. `vk` is the Win32 virtual-key code; `mods` is a bitset
    /// (bit0 = Ctrl, bit1 = Shift, bit2 = Alt). Returns 1 if the core handled it
    /// (C# sets `e.Handled` and the matching `velo_char` is swallowed).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_key(eng: *mut Engine, vk: u32, mods: u32) -> u8 {
        match eng.as_mut() {
            Some(e) => e.on_key(vk as u16, mods & 1 != 0, mods & 2 != 0, mods & 4 != 0) as u8,
            None => 0,
        }
    }

    /// Forward a received character (one UTF-16 code unit, surrogate-aware).
    /// `mods` is the same live bitset as `velo_key` (bit0 = Ctrl, bit2 = Alt),
    /// read at char time; Alt-without-Ctrl gets the ESC/meta prefix (the Ctrl
    /// exclusion keeps AltGr layouts clean).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_char(eng: *mut Engine, cu: u32, mods: u32) {
        if let Some(e) = eng.as_mut() {
            e.on_char(cu as u16, mods & 4 != 0 && mods & 1 == 0);
        }
    }

    /// Forward a pointer event to the focused pane. `kind`: 0 = down, 1 = move,
    /// 2 = up, 3 = leave (pointer left the pane; clears any pending click and
    /// stale link hover, but does not touch selection state). `(x, y)` are
    /// physical px inside the panel (ignored for `kind == 3`; pass 0, 0).
    /// `button` is 0 left, 1 middle, 2 right. `mods` is the same live bitset
    /// as `velo_key` (bit1 = Shift; shift overrides app mouse reporting to
    /// force local text selection).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_mouse(eng: *mut Engine, kind: u32, x: f32, y: f32, button: u32, mods: u32) {
        if let Some(e) = eng.as_mut() {
            let idx = e.focused_pane;
            e.on_mouse_pane(idx, kind, x, y, button.min(2) as u8, mods & 2 != 0);
        }
    }

    /// Register C# callbacks. Call once after `velo_attach`, before the first tab.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_callbacks(eng: *mut Engine, cb: VeloCallbacks) {
        if let Some(e) = eng.as_mut() {
            e.cb = cb;
        }
    }

    /// Spawn a new shell tab; returns its stable session id (u32::MAX on failure).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tab_new(eng: *mut Engine) -> u32 {
        match eng.as_mut() {
            Some(e) => e.spawn_session().unwrap_or(u32::MAX),
            None => u32::MAX,
        }
    }

    /// Enable/disable session recovery: new tabs spawn inside the detached
    /// velo-pty-host process and survive the app closing.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_recovery(eng: *mut Engine, on: u8) {
        if let Some(e) = eng.as_mut() {
            e.recovery = on != 0;
        }
    }

    /// Reattach a tab to host session `host_id` from a previous run. Returns
    /// the new tab id, or u32::MAX when the session no longer exists.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tab_adopt(eng: *mut Engine, host_id: u32) -> u32 {
        match eng.as_mut() {
            Some(e) => e.adopt_session(host_id).unwrap_or(u32::MAX),
            None => u32::MAX,
        }
    }

    /// Host session id backing tab `id` (u32::MAX when local / no such tab).
    /// C# persists this so the next launch can `velo_tab_adopt`.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tab_host_id(eng: *mut Engine, id: u32) -> u32 {
        eng.as_ref()
            .and_then(|e| e.sessions.get(id as usize))
            .and_then(|o| o.as_ref())
            .and_then(|s| s.pty.host_id())
            .unwrap_or(u32::MAX)
    }

    /// Close the tab with stable id `id`. Returns 1 when no tabs remain.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tab_close(eng: *mut Engine, id: u32) -> u32 {
        match eng.as_mut() {
            Some(e) => e.close_session(id as usize) as u32,
            None => 1,
        }
    }

    /// Bind the tab with stable id `id` into the focused pane (single-pane tab
    /// switch). For split view use `velo_pane_bind`.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_tab_set_active(eng: *mut Engine, id: u32) {
        if let Some(e) = eng.as_mut() {
            e.set_active(id as usize);
        }
    }

    // ---- Panes (split view) ----------------------------------------------

    /// Create a new pane with its own composition swapchain. Writes the swapchain
    /// pointer to `*out_swapchain` (bind it to a fresh `SwapChainPanel` via
    /// `ISwapChainPanelNative.SetSwapChain`) and returns the stable pane id, or
    /// `u32::MAX` on failure. Call `velo_pane_resize` once the panel has a size and
    /// `velo_pane_bind` to show a tab in it.
    ///
    /// # Safety
    /// `eng` must be live; `out_swapchain` must be a valid writable pointer.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_new(eng: *mut Engine, out_swapchain: *mut *mut c_void) -> u32 {
        let Some(e) = eng.as_mut() else {
            return u32::MAX;
        };
        match e.add_pane(PaneKind::Terminal) {
            Ok((id, sc)) => {
                if !out_swapchain.is_null() {
                    *out_swapchain = sc;
                }
                id as u32
            }
            Err(err) => {
                dbglog(&format!("velo_pane_new failed: {err}"));
                u32::MAX
            }
        }
    }

    /// Show the tab with stable id `session` in pane `pane`.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_bind(eng: *mut Engine, pane: u32, session: u32) {
        if let Some(e) = eng.as_mut() {
            e.pane_bind(pane as usize, session as usize);
        }
    }

    /// Resize pane `pane` to `w`x`h` physical px and reflow its session.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_resize(eng: *mut Engine, pane: u32, w: u32, h: u32) {
        if let Some(e) = eng.as_mut() {
            e.resize_pane(pane as usize, w, h);
        }
    }

    /// Forward a pointer event to a specific pane. `kind`: 0 = down, 1 = move,
    /// 2 = up, 3 = leave (pointer left the pane; clears any pending click and
    /// stale link hover, but does not touch selection state). `(x, y)` are
    /// physical px inside that pane (ignored for `kind == 3`; pass 0, 0).
    /// `button` is 0 left, 1 middle, 2 right. `mods` is the same live bitset
    /// as `velo_key` (bit1 = Shift; shift overrides app mouse reporting to
    /// force local text selection).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_mouse(eng: *mut Engine, pane: u32, kind: u32, x: f32, y: f32, button: u32, mods: u32) {
        if let Some(e) = eng.as_mut() {
            e.on_mouse_pane(pane as usize, kind, x, y, button.min(2) as u8, mods & 2 != 0);
        }
    }

    /// Mouse wheel over pane `pane`. `delta_lines` is signed lines-to-scroll
    /// (positive = up/toward history, negative = down/toward the present).
    /// `(x, y)` are physical px inside the pane (used to place the SGR wheel
    /// event when mouse reporting is active). `mods` is the same live bitset
    /// as `velo_key` (bit1 = Shift; shift overrides app mouse reporting). When
    /// the pane's session has mouse reporting enabled (and shift is not
    /// held), this sends SGR wheel button 64/65 instead; otherwise it falls
    /// back to scrollback, or (alt screen active) arrow-key translation.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    // ---- Editor pane ABI ---------------------------------------------------

    /// Create (or return) the editor pane; writes its swapchain to
    /// `*out_swapchain` (bind to the EditorHost SwapChainPanel). Returns the
    /// pane id, or `u32::MAX` on failure.
    ///
    /// # Safety
    /// `eng` must be live; `out_swapchain` must be a valid writable pointer.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_attach(
        eng: *mut Engine,
        out_swapchain: *mut *mut c_void,
    ) -> u32 {
        let Some(e) = eng.as_mut() else {
            return u32::MAX;
        };
        match e.editor_attach() {
            Some((idx, sc)) => {
                if !out_swapchain.is_null() {
                    *out_swapchain = sc;
                }
                idx as u32
            }
            None => u32::MAX,
        }
    }

    /// Resize the editor pane to physical px.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_resize(eng: *mut Engine, w: u32, h: u32) {
        if let Some(e) = eng.as_mut() {
            let ep = e.editor_pane;
            e.resize_pane(ep, w, h);
        }
    }

    /// Open (or refocus) a file in the editor. Returns its stable file id, or
    /// -1 when the file is unreadable / not UTF-8 text.
    ///
    /// # Safety
    /// `eng` must be live; `path` must point to `len` valid UTF-16 code units.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_open(
        eng: *mut Engine,
        path: *const u16,
        len: usize,
    ) -> i64 {
        let Some(e) = eng.as_mut() else {
            return -1;
        };
        let s = String::from_utf16_lossy(std::slice::from_raw_parts(path, len));
        match e.workspace.open(std::path::Path::new(&s)) {
            Ok(id) => {
                let ep = e.editor_pane;
                e.render_pane(ep);
                id as i64
            }
            Err(_) => -1,
        }
    }

    /// Close a file (saves it first if dirty).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_close_file(eng: *mut Engine, id: u32) {
        if let Some(e) = eng.as_mut() {
            let _ = e.workspace.close(id);
            e.notify_editor_dirty();
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    /// Focus an open file (editor tab click).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_focus_file(eng: *mut Engine, id: u32) {
        if let Some(e) = eng.as_mut() {
            e.workspace.focus(id);
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    /// Save every dirty editor buffer (autosave flush).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_save_all(eng: *mut Engine) {
        if let Some(e) = eng.as_mut() {
            let _ = e.workspace.save_all();
            e.notify_editor_dirty();
        }
    }

    /// Editor key-down. mods bit0=Ctrl, bit1=Shift, bit2=Alt. Returns 1 when
    /// handled (C# swallows the matching character event).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_key(eng: *mut Engine, vk: u32, mods: u32) -> u8 {
        match eng.as_mut() {
            Some(e) => e.editor_key(vk as u16, mods) as u8,
            None => 0,
        }
    }

    /// Editor character input (one UTF-16 code unit; control chars ignored).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_char(eng: *mut Engine, cu: u32) {
        if let Some(e) = eng.as_mut() {
            e.editor_char(cu);
        }
    }

    /// Editor wheel scroll (positive = down/toward the end of the file).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_scroll(eng: *mut Engine, delta_lines: i32) {
        if let Some(e) = eng.as_mut() {
            e.workspace.scroll(delta_lines);
            let ep = e.editor_pane;
            e.render_pane(ep);
        }
    }

    /// Editor pointer event. kind 0=down, 1=move (drag), 2=up; (x, y) physical
    /// px inside the editor panel.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_editor_mouse(
        eng: *mut Engine,
        kind: u32,
        x: f32,
        y: f32,
        _mods: u32,
    ) {
        if let Some(e) = eng.as_mut() {
            e.editor_mouse(kind, x, y);
        }
    }

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

    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_scroll(
        eng: *mut Engine,
        pane: u32,
        delta_lines: i32,
        x: f32,
        y: f32,
        mods: u32,
    ) {
        if let Some(e) = eng.as_mut() {
            e.pane_scroll(pane as usize, delta_lines, x, y, mods & 2 != 0);
        }
    }

    /// Make pane `pane` the focused pane (keyboard target). Notifies C# of the
    /// session it shows via `on_active_changed`.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_focus(eng: *mut Engine, pane: u32) {
        if let Some(e) = eng.as_mut() {
            e.set_focused_pane(pane as usize);
        }
    }

    /// Destroy pane `pane` (releases its swapchain). Pane 0 cannot be destroyed.
    /// The session it showed stays alive as a background tab.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_pane_close(eng: *mut Engine, pane: u32) {
        if let Some(e) = eng.as_mut() {
            e.close_pane(pane as usize);
        }
    }

    /// Update the DPI scale (rebuilds font + every pane renderer, reflows).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_scale(eng: *mut Engine, scale: f32) {
        if let Some(e) = eng.as_mut() {
            e.set_scale(scale);
        }
    }

    /// Set the base font size in points (rebuilds font + renderers, reflows).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_font_size(eng: *mut Engine, pt: f32) {
        if let Some(e) = eng.as_mut() {
            e.set_font_size(pt);
        }
    }

    /// Cursor style override (0 = shell default, 1 = block, 2 = bar,
    /// 3 = underline) + blink (nonzero = on). Forces a redraw.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_cursor(eng: *mut Engine, style: u8, blink: u8) {
        if let Some(e) = eng.as_mut() {
            e.set_cursor(style, blink != 0);
        }
    }

    /// Toggle ligature run shaping (0 = off, nonzero = on). Forces a redraw.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_ligatures(eng: *mut Engine, on: u8) {
        if let Some(e) = eng.as_mut() {
            e.set_ligatures(on != 0);
        }
    }

    /// Set the terminal font family (rebuilds font + renderers, reflows).
    /// `len == 0` (or null `ptr`) restores the bundled default font.
    ///
    /// # Safety
    /// `eng` must be live; `ptr` must point to `len` valid UTF-16 code units
    /// (or be null when `len` is 0).
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_font_family(eng: *mut Engine, ptr: *const u16, len: usize) {
        let Some(e) = eng.as_mut() else { return };
        if ptr.is_null() || len == 0 {
            e.set_font_family(None);
            return;
        }
        let slice = std::slice::from_raw_parts(ptr, len);
        let name = String::from_utf16_lossy(slice);
        e.set_font_family(Some(&name));
    }

    /// List every installed font family as one UTF-16 buffer, families joined
    /// by '\n'. Writes the code-unit count to `out_len` and returns the buffer;
    /// caller frees it with [`velo_free_utf16`]. Returns null on empty.
    ///
    /// # Safety
    /// `out_len` must be a valid pointer.
    #[no_mangle]
    pub unsafe extern "C" fn velo_list_fonts(out_len: *mut usize) -> *mut u16 {
        let joined = text::list_families().join("\n");
        let utf16: Vec<u16> = joined.encode_utf16().collect();
        if out_len.is_null() {
            return std::ptr::null_mut();
        }
        *out_len = utf16.len();
        if utf16.is_empty() {
            return std::ptr::null_mut();
        }
        Box::into_raw(utf16.into_boxed_slice()) as *mut u16
    }

    /// Free a buffer returned by [`velo_list_fonts`].
    ///
    /// # Safety
    /// `ptr`/`len` must be exactly what `velo_list_fonts` returned, freed once.
    #[no_mangle]
    pub unsafe extern "C" fn velo_free_utf16(ptr: *mut u16, len: usize) {
        if !ptr.is_null() && len > 0 {
            drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)));
        }
    }

    /// Set the shell command line that new tabs spawn. Affects future tabs only.
    ///
    /// # Safety
    /// `eng` must be live; `ptr` must point to `len` valid UTF-16 code units.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_shell(eng: *mut Engine, ptr: *const u16, len: usize) {
        let Some(e) = eng.as_mut() else { return };
        if ptr.is_null() || len == 0 {
            return;
        }
        let slice = std::slice::from_raw_parts(ptr, len);
        e.shell = String::from_utf16_lossy(slice);
    }

    /// Set the terminal background color (RGB, 0..=255 each).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_bg(eng: *mut Engine, r: u8, g: u8, b: u8) {
        if let Some(e) = eng.as_mut() {
            e.set_bg([r, g, b]);
        }
    }

    /// Set the terminal background alpha: 0.0 = fully transparent (Mica/acrylic
    /// behind the window blurs through), 1.0 = opaque.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_bg_alpha(eng: *mut Engine, a: f32) {
        if let Some(e) = eng.as_mut() {
            e.set_bg_alpha(a);
        }
    }

    /// Apply a color theme. `ptr` points to `len` bytes laid out as RGB triples:
    /// 16 ANSI colors, then foreground, cursor, and selection — 19 triples (57
    /// bytes). The background is set separately via `velo_set_bg`. No-op on a
    /// wrong length.
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`; `ptr` must point to `len`
    /// valid bytes.
    #[no_mangle]
    pub unsafe extern "C" fn velo_set_palette(eng: *mut Engine, ptr: *const u8, len: usize) {
        let Some(e) = eng.as_mut() else { return };
        if ptr.is_null() || len != 19 * 3 {
            return;
        }
        let bytes = std::slice::from_raw_parts(ptr, len);
        let mut ansi = [[0u8; 3]; 16];
        for (i, c) in ansi.iter_mut().enumerate() {
            *c = [bytes[i * 3], bytes[i * 3 + 1], bytes[i * 3 + 2]];
        }
        let trip = |n: usize| [bytes[n * 3], bytes[n * 3 + 1], bytes[n * 3 + 2]];
        e.set_palette(ansi, trip(16), trip(17), trip(18));
    }

    /// Paste a UTF-16 string (len code units) into the focused session.
    ///
    /// # Safety
    /// `eng` must be live; `ptr` must point to `len` valid UTF-16 code units.
    #[no_mangle]
    pub unsafe extern "C" fn velo_paste_utf16(eng: *mut Engine, ptr: *const u16, len: usize) {
        let Some(e) = eng.as_mut() else { return };
        if ptr.is_null() {
            return;
        }
        let slice = std::slice::from_raw_parts(ptr, len);
        let text = String::from_utf16_lossy(slice);
        e.paste_text(&text);
    }

    /// Detach the wakeup hook, destroy its window, and drop the engine (joins all
    /// PTY reader threads).
    ///
    /// # Safety
    /// `eng` must be a live handle from `velo_attach`; must not be used after.
    #[no_mangle]
    pub unsafe extern "C" fn velo_shutdown(eng: *mut Engine) {
        if eng.is_null() {
            return;
        }
        let e = Box::from_raw(eng);
        let _ = RemoveWindowSubclass(e.wakeup_hwnd, Some(wakeup_proc), SUBCLASS_ID);
        let _ = DestroyWindow(e.wakeup_hwnd);
        drop(e);
    }
}
