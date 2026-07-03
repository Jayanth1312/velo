using System;
using System.Runtime.InteropServices;

namespace Velo.App;

/// <summary>
/// P/Invoke surface for velo_core.dll (the Rust terminal core). The core renders
/// into a composition swapchain that we bind to a WinUI <c>SwapChainPanel</c>;
/// input and sizing are forwarded from XAML events through this ABI.
/// </summary>
internal static unsafe partial class Native
{
    private const string Core = "velo_core.dll";

    /// Mirrors the Rust `#[repr(C)] VeloCallbacks`. All fields pointer-sized:
    /// `Ctx` is our opaque handle (a GCHandle), the rest are unmanaged function
    /// pointers (`&Method` of `[UnmanagedCallersOnly]` statics).
    [StructLayout(LayoutKind.Sequential)]
    internal struct VeloCallbacks
    {
        public IntPtr Ctx;
        public IntPtr OnTitleChanged;     // (ctx, id, utf16*, len)
        public IntPtr OnTabClosed;        // (ctx, id)
        public IntPtr OnActiveChanged;    // (ctx, id)
        public IntPtr OnNewTabRequested;  // (ctx)
        public IntPtr OnCloseTabRequested;// (ctx, id)
        public IntPtr OnSwitchTabRequested;// (ctx, forward)
        public IntPtr OnCwdChanged;       // (ctx, id, utf16*, len)
        public IntPtr OnCommand;          // (ctx, id, phase, exit, dur_ms, utf16*, len)
    }

    /// Init GPU + composition swapchain + the core's internal wakeup window.
    /// Must be called on the UI thread.
    [LibraryImport(Core)]
    internal static partial IntPtr velo_attach(float dpiScale);

    /// Borrowed IDXGISwapChain pointer to hand to ISwapChainPanelNative.SetSwapChain.
    [LibraryImport(Core)]
    internal static partial IntPtr velo_get_swapchain(IntPtr eng);

    /// Resize the swapchain to physical px and reflow.
    [LibraryImport(Core)]
    internal static partial void velo_resize(IntPtr eng, uint w, uint h);

    /// Draw + present the active session immediately.
    [LibraryImport(Core)]
    internal static partial void velo_render(IntPtr eng);

    /// Forward a key-down. mods bit0=Ctrl, bit1=Shift, bit2=Alt. Returns 1 if handled.
    [LibraryImport(Core)]
    internal static partial byte velo_key(IntPtr eng, uint vk, uint mods);

    /// Forward a received character (one UTF-16 code unit). mods is the same
    /// live modifier bitset as velo_key (bit0=Ctrl, bit1=Shift, bit2=Alt).
    [LibraryImport(Core)]
    internal static partial void velo_char(IntPtr eng, uint cu, uint mods);

    /// Forward a pointer event. kind 0=down,1=move,2=up; (x,y) physical px.
    /// button: 0=left, 1=middle, 2=right. mods is the velo_key bitset (bit1 =
    /// Shift; shift forces local selection even when the app has enabled
    /// mouse reporting).
    [LibraryImport(Core)]
    internal static partial void velo_mouse(IntPtr eng, uint kind, float x, float y, uint button, uint mods);

    [LibraryImport(Core)]
    internal static partial void velo_set_callbacks(IntPtr eng, VeloCallbacks cb);

    [LibraryImport(Core)]
    internal static partial uint velo_tab_new(IntPtr eng);

    [LibraryImport(Core)]
    internal static partial uint velo_tab_close(IntPtr eng, uint id);

    [LibraryImport(Core)]
    internal static partial void velo_tab_set_active(IntPtr eng, uint id);

    // ---- Panes (split view) ----------------------------------------------

    /// Create a pane with its own swapchain. Writes the swapchain ptr to
    /// *outSwapchain (bind to a fresh SwapChainPanel); returns the pane id
    /// (uint.MaxValue on failure).
    [LibraryImport(Core)]
    internal static partial uint velo_pane_new(IntPtr eng, IntPtr* outSwapchain);

    /// Show tab `session` in pane `pane`.
    [LibraryImport(Core)]
    internal static partial void velo_pane_bind(IntPtr eng, uint pane, uint session);

    /// Resize pane `pane` to physical px and reflow its session.
    [LibraryImport(Core)]
    internal static partial void velo_pane_resize(IntPtr eng, uint pane, uint w, uint h);

    /// Forward a pointer event to a specific pane. kind 0=down,1=move,2=up.
    /// button: 0=left, 1=middle, 2=right. mods is the velo_key bitset (bit1 =
    /// Shift; shift forces local selection even when the app has enabled
    /// mouse reporting).
    [LibraryImport(Core)]
    internal static partial void velo_pane_mouse(IntPtr eng, uint pane, uint kind, float x, float y, uint button, uint mods);

    /// Make `pane` the keyboard-focused pane.
    [LibraryImport(Core)]
    internal static partial void velo_pane_focus(IntPtr eng, uint pane);

    /// Mouse wheel over `pane`. deltaLines is signed (positive = up/history,
    /// negative = down/present). (x,y) are physical px inside the pane (places
    /// the SGR wheel event when the app has mouse reporting on); mods is the
    /// velo_key bitset (bit1 = Shift overrides mouse reporting). Falls back to
    /// arrow keys on the alt screen (vim, less, ...) or scrollback otherwise.
    [LibraryImport(Core)]
    internal static partial void velo_pane_scroll(IntPtr eng, uint pane, int deltaLines, float x, float y, uint mods);

    /// Destroy `pane` (pane 0 cannot be destroyed); its tab survives.
    [LibraryImport(Core)]
    internal static partial void velo_pane_close(IntPtr eng, uint pane);

    [LibraryImport(Core)]
    internal static partial void velo_set_scale(IntPtr eng, float scale);

    [LibraryImport(Core)]
    internal static partial void velo_set_font_size(IntPtr eng, float pt);

    [LibraryImport(Core)]
    internal static partial void velo_set_shell(IntPtr eng, ushort* ptr, nuint len);

    [LibraryImport(Core)]
    internal static partial void velo_set_bg(IntPtr eng, byte r, byte g, byte b);

    [LibraryImport(Core)]
    internal static partial void velo_set_bg_alpha(IntPtr eng, float a);

    /// Apply a color theme. `ptr` -> 57 bytes (19 RGB triples): 16 ANSI colors,
    /// then foreground, cursor, selection. Background is set via velo_set_bg.
    [LibraryImport(Core)]
    internal static partial void velo_set_palette(IntPtr eng, byte* ptr, nuint len);

    [LibraryImport(Core)]
    internal static partial void velo_paste_utf16(IntPtr eng, ushort* ptr, nuint len);

    [LibraryImport(Core)]
    internal static partial void velo_shutdown(IntPtr eng);
}

/// <summary>
/// Native bridge for binding a DXGI swapchain into a WinUI SwapChainPanel. The
/// panel's backing object implements this; cast it and call SetSwapChain with the
/// core's swapchain pointer (the panel AddRefs it).
/// </summary>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}
