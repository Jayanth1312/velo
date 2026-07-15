using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace Velo.App;

public sealed partial class MainWindow : Window
{
    /// All tabs in display order (groups flattened). Drives switch/index logic; kept
    /// in sync from <see cref="_layout"/>. The ListView binds to TabListItems, not this.
    public ObservableCollection<TabVM> Tabs { get; } = new();

    /// What the sidebar ListView shows: group-header rows (TabGroup) interleaved with
    /// tab rows (TabVM). Rebuilt from _layout; a collapsed group emits only its header.
    public ObservableCollection<object> TabListItems { get; } = new();

    /// Source of truth for tab order + grouping: each entry is a bare TabVM (ungrouped)
    /// or a TabGroup (which owns its member TabVMs). Tabs/TabListItems derive from this.
    private readonly List<object> _layout = new();

    /// Tabs currently Ctrl+Click-selected, awaiting a Ctrl+G group action.
    private readonly HashSet<TabVM> _selected = new();

    /// Filtered rows shown in the command palette.
    public ObservableCollection<PaletteItem> PaletteItems { get; } = new();
    private List<PaletteItem> _paletteSource = new();

    private IntPtr _engine = IntPtr.Zero;   // *Engine from velo_attach
    private GCHandle _selfHandle;            // ctx passed to the Rust callbacks
    private double _lastScale = 1.0;
    private double _zoom = 1.0;               // terminal font zoom (Ctrl++/Ctrl+-)
    private Storyboard? _zoomToastSb;
    private bool _suppressSelection;
    private bool _sidebarOpen = true;
    private bool _detailsOpen = false;
    private readonly Settings _settings = Settings.Load();

    // Manual title-bar drag: the whole bar is a passthrough hover strip (so it
    // reveals the toggles everywhere), so dragging is started by hand here.
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Window-class background brush: Windows fills freshly exposed window area
    // (maximize, fast resize) with this brush BEFORE the app paints a frame.
    // Default is null → black flash on every maximize. Kept in sync with the
    // terminal bg in ApplySettings.
    private const int GCLP_HBRBACKGROUND = -10;
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorRef);   // 0x00BBGGRR
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    private IntPtr _classBgBrush;

    // ---- Split view (up to 4 panes in a 2x2 grid) ----
    private const uint InvalidId = uint.MaxValue;
    private const int MaxPanes = 4;
    private SwapChainPanel[] _panels = System.Array.Empty<SwapChainPanel>(); // set in Loaded
    private readonly uint[] _slotCore = { 0, InvalidId, InvalidId, InvalidId };   // core pane id per slot
    private readonly uint[] _slotTab = { InvalidId, InvalidId, InvalidId, InvalidId }; // tab shown per slot
    private readonly IntPtr[] _slotSwap = new IntPtr[MaxPanes];  // pending swapchain to bind once visible
    private int _paneCount = 1;          // active panes (= visible leaves of the shown tree)
    // Absolute rect of each slot's panel in PaneHost coords (multi-pane tree layout).
    private readonly double[] _slotX = new double[MaxPanes], _slotY = new double[MaxPanes],
                             _slotW = new double[MaxPanes], _slotH = new double[MaxPanes];
    private int _focusedPane;            // slot with focus
    private TabVM? _dragTab;             // tab being dragged from the list
    private uint _dragAnchorTab = InvalidId; // split/tab the body showed before the drag selected _dragTab
    private enum Edge { Left, Right, Top, Bottom }

    private OverlappedPresenter? _presenter;
    private InputNonClientPointerSource? _nonClient;

    public MainWindow()
    {
        Log.Write("ctor: enter [build:geom-unparent+simple-backdrop]");
        PaneTree.SelfCheck();   // pane-tree op sanity (throws on regression)
        InitializeComponent();
        Log.Write($"ctor: InitializeComponent done. Content={Content?.GetType().Name ?? "null"}");

        // Selector wired in code, not XAML: instantiating a custom
        // DataTemplateSelector as a <Grid.Resources> entry crashes the WinUI 1.6
        // XamlCompiler (silent exit 1). Templates stay in XAML; we just bind them
        // to the selector here and hand it to the tab list.
        TabList.ItemTemplateSelector = new TabRowTemplateSelector
        {
            TabTemplate = (DataTemplate)RootGrid.Resources["TabRowTemplate"],
            GroupHeaderTemplate = (DataTemplate)RootGrid.Resources["GroupHeaderTemplate"],
            SplitTemplate = (DataTemplate)RootGrid.Resources["SplitRowTemplate"],
        };

        // DEBUG: bracket the first-layout crash. These fire in order:
        // root.Loading -> root.Loaded -> PaneHost_Loaded -> TerminalPanel Loaded ->
        // first Rendering. Whichever is the LAST line in the log is where it dies.
        if (Content is FrameworkElement root)
        {
            root.Loading += (_, _) => Log.Write("DEBUG root.Loading");
            root.Loaded += (_, _) => Log.Write("DEBUG root.Loaded");
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFirstRender;

        // Inject shell integration before the first shell spawns so OSC 7/133
        // emitters are live in tab 1's profile load. Dispatched off-thread: it
        // only touches profile files, injection is idempotent, and nothing in
        // the ctor depends on it completing.
        Task.Run(() => ShellIntegration.Ensure(_settings));
        Log.Write("ctor: ShellIntegration dispatched");
        SelectDetailsTab(InfoTabButton);   // default section, shown when panel opens
        Log.Write("ctor: SelectDetailsTab done");
        // Warm the agent scan (PATH + WSL probes take seconds) so the Agent
        // panel's dropdown is populated by the time it first opens.
        _ = AgentProfiles.AllAsync();

        // Frameless: drop the system title bar (keep the resize border), then
        // extend our content into the whole window.
        _presenter = AppWindow.Presenter as OverlappedPresenter;
        _presenter?.SetBorderAndTitleBar(true, false);
        ExtendsContentIntoTitleBar = true;
        Log.Write("ctor: titlebar done, calling ApplyBackdrop");
        ApplyBackdrop();
        Log.Write("ctor: ApplyBackdrop done");

        _nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange || args.DidSizeChange)
            {
                UpdateMaxGlyph();
                // Maximize/restore must not wait out the 33ms resize throttle:
                // push any parked pane size now so the swapchain fills the new
                // area on the next frame instead of showing the class-brush fill.
                FlushPendingResizes();
            }
        };

        _selfHandle = GCHandle.Alloc(this);
        Closed += OnClosed;

        // Re-window-activation steals focus to chrome; pull it back to the panel
        // so typing keeps working ("could type at first, then couldn't").
        Activated += (_, args) =>
        {
            try
            {
                Log.Write($"Activated: state={args.WindowActivationState}");
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                    FocusTerminal();
            }
            catch (Exception ex)
            {
                Log.Ex("Activated", ex);
            }
        };
        Log.Write("ctor: complete");
    }

    // DEBUG: logs the first composited frame, then unhooks itself. If this never
    // appears, the compositor never produced a frame (native render/init crash).
    private void OnFirstRender(object? sender, object e)
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFirstRender;
        Log.Write("DEBUG first CompositionTarget.Rendering");
    }

    // Fires when the terminal host Grid loads — right before TerminalPanel_Loaded.
    private void PaneHost_Loaded(object sender, RoutedEventArgs e)
    {
        Log.Write($"PaneHost_Loaded: size={PaneHost.ActualWidth}x{PaneHost.ActualHeight}");
        // Split panes are absolutely positioned from the tree; recompute their rects
        // whenever the body resizes (window resize, sidebar toggle).
        PaneHost.SizeChanged += PaneHost_SizeChanged;
        // XAML KeyDown="..." registers with handledEventsToo:false, so if the
        // framework marks the key event Handled (accelerators / focus internals)
        // before it reaches us, the handler is silently skipped — the "can't type"
        // bug (log shows focus=Grid but no Panel_KeyDown ever fires). Re-register
        // with handledEventsToo:true so we receive the keys regardless.
        PaneHost.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Panel_KeyDown), true);
        PaneHost.AddHandler(
            UIElement.CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(Panel_CharacterReceived),
            true);

        // Root-level safety net: if focus drifts off the terminal (sidebar list, a
        // realized details control, a dismissed flyout) PaneHost stops receiving keys
        // and the terminal goes dead even though FocusManager still reports the Grid.
        // Catch keys at the window root and forward them to the terminal UNLESS focus
        // is on the terminal already (PaneHost handler covers it — avoid double input)
        // or in a text box (palette / find boxes own their typing).
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        RootGrid.AddHandler(
            UIElement.CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(Root_CharacterReceived),
            true);

        // Ctrl+, → Settings. The comma is VK_OEM_COMMA (188); VirtualKey has no name
        // for it, so the accelerator is built in code rather than XAML.
        var settingsAccel = new KeyboardAccelerator
        {
            Modifiers = VirtualKeyModifiers.Control,
            Key = (VirtualKey)188,
        };
        settingsAccel.Invoked += (_, args) => { args.Handled = true; Settings_Click(this, null!); };
        RootGrid.KeyboardAccelerators.Add(settingsAccel);

        // Ctrl+G → group the Ctrl+Click-selected tabs.
        var groupAccel = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.G };
        groupAccel.Invoked += GroupAccel_Invoked;
        RootGrid.KeyboardAccelerators.Add(groupAccel);
    }

    // ---- Lifecycle --------------------------------------------------------

    private void TerminalPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Log.Write($"Loaded: enter. engine={_engine}, scaleX={TerminalPanel.CompositionScaleX}, " +
                  $"size={TerminalPanel.ActualWidth}x{TerminalPanel.ActualHeight}");
        if (_engine != IntPtr.Zero)
            return;

        // Each SwapChainPanel carries its core pane id in Tag. Slot 0 = core pane 0
        // (created at attach); slots 1-3 get core panes lazily on split.
        _panels = new[] { TerminalPanel, TerminalPanel1, TerminalPanel2, TerminalPanel3 };
        TerminalPanel.Tag = 0u;

        try
        {
            // SwapChainPanel's CompositionScaleX is the authoritative DPI scale for
            // sizing the swapchain. Using XamlRoot.RasterizationScale here is what
            // made the terminal render small then get bilinear-upscaled (big + blurry).
            _lastScale = TerminalPanel.CompositionScaleX > 0 ? TerminalPanel.CompositionScaleX : 1.0;
            _engine = Native.velo_attach((float)_lastScale);
            Log.Write($"Loaded: velo_attach -> {_engine}");
            if (_engine == IntPtr.Zero)
            {
                Log.Write("Loaded: ABORT, velo_attach returned null");
                return;
            }

            Native.velo_set_callbacks(_engine, BuildCallbacks());
            Log.Write("Loaded: callbacks set");
            ApplySettings();
            Log.Write("Loaded: ApplySettings done");

            // Bind the core's composition swapchain into this panel — the terminal now
            // composites inside the XAML tree (no airspace / overlay HWND).
            var panelNative = TerminalPanel.As<ISwapChainPanelNative>();
            var swap = Native.velo_get_swapchain(_engine);
            Log.Write($"Loaded: swapchain ptr={swap}");
            panelNative.SetSwapChain(swap);
            Log.Write("Loaded: SetSwapChain done");

            PushSize();
            Native.velo_render(_engine); // immediate cleared frame, no late black box
            Log.Write("Loaded: first render done");

            TerminalPanel.CompositionScaleChanged += OnCompositionScaleChanged;

            UpdateMaxGlyph();
            RestoreSession();
            FocusTerminal();
            Log.Write("Loaded: complete");
        }
        catch (Exception ex)
        {
            Log.Ex("TerminalPanel_Loaded", ex);
            throw;
        }
    }

    /// Reopen last session's tabs. Tabs that ran in velo-pty-host are ADOPTED —
    /// the same process, still running, output replayed. Others (or gone host
    /// sessions) respawn fresh in their saved cwd. Falls back to one default tab.
    private SessionState _restored = new();

    private void RestoreSession()
    {
        var s = SessionState.Load();
        _restored = s;   // agent chat replays when the panel first opens
        foreach (var t in s.Tabs)
        {
            if (t.Browser)
            {
                _layout.Add(new TabVM(_nextBrowserId++, "New Tab")
                {
                    IsBrowser = true,
                    ShellKind = "web",
                    IconFile = "web.svg",
                    BrowserUrl = string.IsNullOrWhiteSpace(t.Url) ? "https://www.google.com/" : t.Url,
                });
                continue;
            }
            if (string.IsNullOrWhiteSpace(t.Cmd))
                continue;
            TabVM? vm = null;
            if (t.HostId != uint.MaxValue && _settings.SessionRecovery)
            {
                uint tid = Native.velo_tab_adopt(_engine, t.HostId);
                Log.Write($"RestoreSession: adopt host={t.HostId} -> {tid}");
                if (tid != uint.MaxValue)
                {
                    vm = new TabVM(tid, ShellDisplayName(t.Cmd)) { LaunchCmd = t.Cmd, Cwd = t.Cwd };
                    ApplyShellKind(vm, t.Cmd);
                }
            }
            if (vm is null)
            {
                var run = ShouldRerun(t.Running) ? t.Running : null;
                vm = SpawnTab(new ShellProfile(ShellDisplayName(t.Cmd), CmdWithCwd(t.Cmd, t.Cwd, run), "powershell.svg"));
                if (vm is null)
                    continue;
                vm.LaunchCmd = t.Cmd;   // unwrapped: re-saving must not nest the cd
            }
            _layout.Add(vm);
        }
        RefreshTabList();
        if (Tabs.Count == 0)
        {
            AddTab();
            return;
        }
        TabList.SelectedItem = Tabs[0];
    }

    /// True when policy + whitelist allow re-launching `running` on restore.
    private bool ShouldRerun(string running)
    {
        if (string.IsNullOrWhiteSpace(running) || _settings.RerunOnRestore == "Off")
            return false;
        if (_settings.RerunOnRestore == "All")
            return true;
        var first = running.TrimStart().Split(' ', '\t')[0];
        foreach (var w in _settings.RerunWhitelist.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (first.Equals(w, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// Wrap a shell launch command so it starts in `cwd`, optionally re-running
    /// `run` there (PowerShell/cmd only; WSL tabs keep processes alive via the
    /// pty host instead).
    private static string CmdWithCwd(string cmd, string cwd, string? run = null)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return cmd;
        var c = cmd.ToLowerInvariant();
        if (c.Contains("wsl"))
            return $"{cmd} --cd \"{cwd}\"";
        if (c.Contains("cmd"))
            return run is null
                ? $"{cmd} /K \"cd /d {cwd}\""
                : $"{cmd} /K \"cd /d {cwd} && {run}\"";
        if (c.Contains("powershell") || c.Contains("pwsh"))
            return run is null
                ? $"{cmd} -NoExit -Command \"Set-Location -LiteralPath '{cwd}'\""
                : $"{cmd} -NoExit -Command \"Set-Location -LiteralPath '{cwd}'; {run.Replace("\"", "'")}\"";
        return cmd;
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        Log.Write($"OnClosed: window closing. Tabs={Tabs.Count}\n{Environment.StackTrace}");
        // Snapshot tabs (command + cwd + host session) for RestoreSession.
        var session = new SessionState();
        foreach (var t in Tabs)
        {
            if (t.IsBrowser)
            {
                session.Tabs.Add(new SessionState.TabInfo { Browser = true, Url = t.BrowserUrl });
                continue;
            }
            if (t.LaunchCmd.Length > 0)
                session.Tabs.Add(new SessionState.TabInfo
                {
                    Cmd = t.LaunchCmd,
                    Cwd = t.Cwd,
                    HostId = _engine != IntPtr.Zero ? Native.velo_tab_host_id(_engine, t.Id) : uint.MaxValue,
                    Running = t.RunningCommand,
                });
        }
        if (_settings.RestoreAgentChat)
        {
            foreach (var m in _agentHistory)
                session.AgentChat.Add(new SessionState.AgentMsg { Text = m.Text, User = m.User });
            session.AgentName = _agentSel?.Name ?? "";
            session.AgentContinue = _agentContinue;
            session.AgentPending = _agentPendingPrompt ?? "";
        }
        session.Save();
        if (_engine != IntPtr.Zero)
        {
            // velo_shutdown drops every PTY Session, which calls ClosePseudoConsole
            // + joins the reader thread for each tab. When shells haven't exited yet
            // this blocks the UI thread for several seconds (one join per tab).
            // Offload to a thread-pool thread so the window closes immediately; the
            // process will exit once the last background thread finishes anyway.
            var eng = _engine;
            _engine = IntPtr.Zero;
            System.Threading.Tasks.Task.Run(() => Native.velo_shutdown(eng));
        }
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    // ---- Sizing / DPI -----------------------------------------------------

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
        => PushPaneSize((SwapChainPanel)sender);

    /// Push pane 0's size to the core (used by Loaded + scale changes).
    private void PushSize() => PushPaneSize(TerminalPanel);

    // A native resize is expensive: it flushes the GPU (buffer release before
    // ResizeBuffers), rewraps the scrollback, and repaints. Per-frame layout
    // changes (sidebar width animation, the maximize/restore transition) fired
    // it every frame → visible jank. Coalesce: first call goes through
    // immediately, further calls within 33ms park in _pendingPanes and flush on
    // the timer tick. Storyboard-driven layout suppresses pushes entirely and
    // flushes once on Completed.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeThrottle;
    private readonly HashSet<SwapChainPanel> _pendingPanes = new();
    private bool _pendingEditorResize;
    private bool _suppressPaneResize;

    private void FlushPendingResizes()
    {
        foreach (var p in _pendingPanes)
            PushPaneSizeNow(p);
        _pendingPanes.Clear();
        if (_pendingEditorResize)
        {
            _pendingEditorResize = false;
            PushEditorSizeNow();
        }
    }

    /// True → caller should park the resize; false → push now (throttle armed).
    private bool DeferResize()
    {
        if (_suppressPaneResize)
            return true;
        if (_resizeThrottle is null)
        {
            _resizeThrottle = DispatcherQueue.CreateTimer();
            _resizeThrottle.Interval = TimeSpan.FromMilliseconds(33);
            _resizeThrottle.IsRepeating = false;
            _resizeThrottle.Tick += (_, _) => FlushPendingResizes();
        }
        if (_resizeThrottle.IsRunning)
            return true;
        _resizeThrottle.Start();
        return false;
    }

    /// Push a panel's physical pixel size to its core pane (resize + reflow).
    private void PushPaneSize(SwapChainPanel panel)
    {
        if (_engine == IntPtr.Zero)
            return;
        if (DeferResize())
        {
            _pendingPanes.Add(panel);
            return;
        }
        PushPaneSizeNow(panel);
    }

    private void PushPaneSizeNow(SwapChainPanel panel)
    {
        if (_engine == IntPtr.Zero)
            return;
        uint id = PaneId(panel);
        double sx = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : _lastScale;
        double sy = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : _lastScale;
        uint w = (uint)Math.Max(1, Math.Round(panel.ActualWidth * sx));
        uint h = (uint)Math.Max(1, Math.Round(panel.ActualHeight * sy));
        Native.velo_pane_resize(_engine, id, w, h);
    }

    /// Called BEFORE a sidebar-close animation: push every visible pane's
    /// post-animation width to the core now, so the widening column reveals an
    /// already-rendered terminal instead of a bg strip (the swapchain buffer is
    /// never smaller than the visible panel). The animation's own SizeChanged
    /// storm stays suppressed; the exact size lands on the Completed flush.
    /// ponytail: extra width prorated by the pane's share of PaneHost — ±1px
    /// off for splits, corrected by that same Completed flush.
    private void PreGrowPaneSizes(double delta)
    {
        if (_engine == IntPtr.Zero || delta <= 0)
            return;
        if (_editorMode)
        {
            if (_editorAttached && EditorPanel.ActualWidth > 0)
            {
                double esx = EditorPanel.CompositionScaleX > 0 ? EditorPanel.CompositionScaleX : _lastScale;
                double esy = EditorPanel.CompositionScaleY > 0 ? EditorPanel.CompositionScaleY : _lastScale;
                Native.velo_editor_resize(_engine,
                    (uint)Math.Max(1, Math.Round((EditorPanel.ActualWidth + delta) * esx)),
                    (uint)Math.Max(1, Math.Round(EditorPanel.ActualHeight * esy)));
            }
            return;
        }
        double host = PaneHost.ActualWidth;
        if (host <= 0)
            return;
        foreach (var p in _panels)
        {
            if (p is null || p.Visibility != Visibility.Visible || p.ActualWidth <= 0)
                continue;
            double sx = p.CompositionScaleX > 0 ? p.CompositionScaleX : _lastScale;
            double sy = p.CompositionScaleY > 0 ? p.CompositionScaleY : _lastScale;
            double w = p.ActualWidth + delta * p.ActualWidth / host;
            Native.velo_pane_resize(_engine, PaneId(p),
                (uint)Math.Max(1, Math.Round(w * sx)),
                (uint)Math.Max(1, Math.Round(p.ActualHeight * sy)));
        }
    }

    /// Core pane id stored in each SwapChainPanel's Tag (defaults to pane 0).
    private static uint PaneId(object panel)
        => panel is FrameworkElement fe && fe.Tag is uint id ? id : 0u;

    /// Core id of the split pane that currently holds focus. Key/char events arrive
    /// on PaneHost (the swapchains aren't hit-testable), whose Tag is unset — so we
    /// must resolve the focused slot ourselves, else input always lands on slot 0.
    private uint FocusedCore()
        => (_focusedPane >= 0 && _focusedPane < MaxPanes && _slotCore[_focusedPane] != InvalidId)
            ? _slotCore[_focusedPane]
            : 0u;

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        var scale = sender.CompositionScaleX > 0 ? sender.CompositionScaleX : 1.0;
        if (Math.Abs(scale - _lastScale) > 0.001 && _engine != IntPtr.Zero)
        {
            _lastScale = scale;
            Native.velo_set_scale(_engine, (float)scale);
        }
        PushSize();
        UpdateDragRegions();
    }

    // ---- Frameless chrome -------------------------------------------------

    // Chrome bars (sidebar top rows) recompute the drag/passthrough strip on resize.
    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDragRegions();
    /// Make the whole top strip Passthrough (client) so XAML — not the OS caption —
    /// owns it: the terminal handles its own first-row pointer/selection, and the
    /// sidebar chrome starts window drag by hand (see TitleBarHover_PointerPressed).
    private void UpdateDragRegions()
    {
        if (_nonClient is null || Content is not UIElement content)
            return;
        double s = content.XamlRoot?.RasterizationScale ?? _lastScale;
        int barH = (int)Math.Round(34 * s);
        int winW = AppWindow.Size.Width;

        _nonClient.SetRegionRects(NonClientRegionKind.Caption, Array.Empty<RectInt32>());
        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { new RectInt32(0, 0, winW, barH) });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => _presenter?.Minimize();

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter is null)
            return;
        if (_presenter.State == OverlappedPresenterState.Maximized)
            _presenter.Restore();
        else
            _presenter.Maximize();
        UpdateMaxGlyph();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxGlyph()
        => MaxButton.Content = _presenter?.State == OverlappedPresenterState.Maximized
            ? ""  // restore
            : ""; // maximize

    private void PaneToggle_Click(object sender, RoutedEventArgs e) => SetSidebar(!_sidebarOpen);

    // Inner-bar widths for the toggle glyph: wide = pane open, narrow = closed.
    private const double BarOpen = 5, BarClosed = 2;
    private const double SidebarWidth = 280;

    /// Open/close the tabs column by animating its Border WIDTH (the Auto column
    /// tracks it). The terminal column is `*`, so it genuinely reflows in lockstep —
    /// one container, real push, no overlay/slide desync.
    private void SetSidebar(bool open)
    {
        DropOverlay(false);   // if shown as an overlay, dock it from a clean state
        _sidebarOpen = open;
        AnimateWidth(ToggleBar, open ? BarOpen : BarClosed);
        // Animate the column width instead of an instant jump: a 280px one-frame jump
        // exposes a misaligned strip at the terminal↔sidebar seam (the swapchain's
        // DComp present lags the XAML column commit by a frame → the color flash). At
        // ~a few px per frame that desync is sub-pixel and reads as motion, not a flash.
        // Native resizes stay muted while the storyboard runs (one push at the end):
        // a swapchain resize per animation frame stalls the GPU + rewraps scrollback
        // and dragged the whole animation to slideshow speed.
        _suppressPaneResize = true;
        // Closing → the terminal column GROWS: pre-size the panes to their final
        // width so the reveal shows rendered terminal, not a background strip.
        Sidebar.Width = TabsWidth;
        PreGrowPaneSizes(SidebarSurface.ActualWidth - (open ? TabsWidth : 0));
        AnimateWidth(SidebarSurface, open ? TabsWidth : 0, () =>
        {
            _suppressPaneResize = false;
            FlushPendingResizes();
        });
        UpdateEdgeTriggers();
        FocusTerminal();
    }

    /// Animate a FrameworkElement.Width (a layout/dependent property, hence
    /// EnableDependentAnimation). Drives both the panel columns and the toggle glyph.
    private static void AnimateWidth(FrameworkElement el, double to, Action? completed = null)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Width");
        sb.Children.Add(anim);
        if (completed is not null)
            sb.Completed += (_, _) => completed();
        sb.Begin();
    }

    // ---- Floating overlay sidebars (Zen-style edge-hover) -----------------
    //
    // Closed sidebar + hover the window edge -> the sidebar reveals as a panel
    // floating over the terminal (a separate component from the docked column).
    // The live content subtree is reparented into the overlay host on reveal and
    // back to its docked surface on retract, so there is ONE instance of each
    // panel (its named controls + code-behind bindings stay intact). Retracts on
    // pointer-leave; clicking the in-panel toggle promotes it to docked.
    private bool _detailsOverlay, _tabsOverlay;
    private const double OverlayHidden = 500;   // off-screen slide distance (px)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _revealTimer;

    private void DetailsEdge_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ArmReveal(left: true);

    private void TabsEdge_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ArmReveal(left: false);

    // 150ms dwell before revealing, so a fast cursor sweep past the edge does not
    // flash the overlay.
    private void ArmReveal(bool left)
    {
        if (left ? (_detailsOpen || _detailsOverlay) : (_sidebarOpen || _tabsOverlay))
            return;
        _revealTimer?.Stop();
        _revealTimer = DispatcherQueue.CreateTimer();
        _revealTimer.Interval = TimeSpan.FromMilliseconds(150);
        _revealTimer.IsRepeating = false;
        _revealTimer.Tick += (_, _) => RevealOverlay(left);
        _revealTimer.Start();
    }

    // Cursor left the edge strip before the dwell elapsed: cancel a pending reveal.
    private void OverlayEdge_PointerExited(object sender, PointerRoutedEventArgs e)
        => _revealTimer?.Stop();

    private void RevealOverlay(bool left)
    {
        _revealTimer?.Stop();
        if (left)
        {
            if (_detailsOpen || _detailsOverlay) return;
            _detailsOverlay = true;
            DetailsContent.Width = DetailsWidth;
            DetailsSurface.Child = null;
            DetailsOverlayPanel.Child = DetailsContent;
            DetailsOverlayHost.Visibility = Visibility.Visible;
            EnsureDetailsRealized();
            AnimateTranslateX(DetailsOverlayXform, 0);
        }
        else
        {
            if (_sidebarOpen || _tabsOverlay) return;
            _tabsOverlay = true;
            Sidebar.Width = TabsWidth;
            SidebarSurface.Child = null;
            TabsOverlayPanel.Child = Sidebar;
            TabsOverlayHost.Visibility = Visibility.Visible;
            AnimateTranslateX(TabsOverlayXform, 0);
        }
    }

    private void Overlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited bubbles up from children too; only retract when the pointer
        // is genuinely outside the host bounds.
        var host = (FrameworkElement)sender;
        var p = e.GetCurrentPoint(host).Position;
        if (p.X >= 0 && p.Y >= 0 && p.X <= host.ActualWidth && p.Y <= host.ActualHeight)
            return;
        RetractOverlay(ReferenceEquals(sender, DetailsOverlayHost));
    }

    private void RetractOverlay(bool left)
    {
        if (left)
        {
            if (!_detailsOverlay) return;
            AnimateTranslateX(DetailsOverlayXform, -OverlayHidden, () =>
            {
                DetailsOverlayHost.Visibility = Visibility.Collapsed;
                DetailsOverlayPanel.Child = null;
                DetailsSurface.Child = DetailsContent;
                _detailsOverlay = false;
            });
        }
        else
        {
            if (!_tabsOverlay) return;
            AnimateTranslateX(TabsOverlayXform, OverlayHidden, () =>
            {
                TabsOverlayHost.Visibility = Visibility.Collapsed;
                TabsOverlayPanel.Child = null;
                SidebarSurface.Child = Sidebar;
                _tabsOverlay = false;
            });
        }
    }

    // Reparent content back to the docked surface immediately (no slide) — used
    // when promoting an overlay to docked so SetDetails/SetSidebar can animate the
    // real column width from a consistent starting point.
    private void DropOverlay(bool left)
    {
        _revealTimer?.Stop();
        if (left && _detailsOverlay)
        {
            DetailsOverlayHost.Visibility = Visibility.Collapsed;
            DetailsOverlayXform.X = -OverlayHidden;
            DetailsOverlayPanel.Child = null;
            DetailsSurface.Child = DetailsContent;
            _detailsOverlay = false;
        }
        else if (!left && _tabsOverlay)
        {
            TabsOverlayHost.Visibility = Visibility.Collapsed;
            TabsOverlayXform.X = OverlayHidden;
            TabsOverlayPanel.Child = null;
            SidebarSurface.Child = Sidebar;
            _tabsOverlay = false;
        }
    }

    // Docked panels own their edge; disable the closed-state hover trigger there so
    // the panel's outer edge stays clickable.
    private void UpdateEdgeTriggers()
    {
        DetailsEdge.IsHitTestVisible = !_detailsOpen;
        TabsEdge.IsHitTestVisible = !_sidebarOpen;
    }

    private static void AnimateTranslateX(TranslateTransform t, double to, Action? completed = null)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        if (completed is not null)
            sb.Completed += (_, _) => completed();
        sb.Begin();
    }

    private void PaneToggleAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetSidebar(!_sidebarOpen);
    }

    // ---- Details panel (left) ---------------------------------------------

    private void DetailsToggle_Click(object sender, RoutedEventArgs e) => SetDetails(!_detailsOpen);

    private void DetailsToggleAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetDetails(!_detailsOpen);
    }

    /// Mirror of SetSidebar for the LEFT details column.
    private void SetDetails(bool open)
    {
        DropOverlay(true);    // if shown as an overlay, dock it from a clean state
        Log.Write($"SetDetails: open={open} tab={_activeDetailsTab} realized={_detailsRealized}");
        _detailsOpen = open;
        AnimateWidth(DetailsToggleBar, open ? BarOpen : BarClosed);
        _suppressPaneResize = true;                              // see SetSidebar
        DetailsContent.Width = DetailsWidth;
        PreGrowPaneSizes(DetailsSurface.ActualWidth - (open ? DetailsWidth : 0));
        AnimateWidth(DetailsSurface, open ? DetailsWidth : 0, () =>
        {
            _suppressPaneResize = false;
            FlushPendingResizes();
        });
        if (open)
            EnsureDetailsRealized();   // build + fill the panels on first open
        UpdateEdgeTriggers();
        FocusTerminal();
    }

    private void DetailsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            SelectDetailsTab(b);
        if (!_detailsOpen && !_detailsOverlay)
            SetDetails(true);
        // Files button doubles as the editor-mode toggle: first click opens the
        // tree + editor surface, clicking again returns to the terminal.
        if (ReferenceEquals(sender, FilesTabButton))
        {
            SetEditorMode(!_editorMode);
            return; // SetEditorMode owns focus either way
        }
        // The agent panel is a chat: keyboard goes to its input box.
        if (ReferenceEquals(sender, AgentTabButton) && _detailsRealized)
        {
            AgentInput.Focus(FocusState.Programmatic);
            return;
        }
        FocusTerminal();
    }

    /// Highlight the chosen section button (filled pill) and swap the placeholder
    /// body. Real per-section panels replace the body in later phases.
    private void SelectDetailsTab(Button selected)
    {
        var pill = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        // Only the selected section shows its text label; the rest stay icon-only.
        var labels = new[]
        {
            (InfoTabButton, InfoLabel),
            (OutlineTabButton, OutlineLabel),
            (GitTabButton, GitLabel),
            (FilesTabButton, FilesLabel),
            (AgentTabButton, AgentLabel),
        };
        foreach (var (btn, label) in labels)
        {
            btn.Background = btn == selected ? pill : null;
            label.Visibility = btn == selected ? Visibility.Visible : Visibility.Collapsed;
        }
        if (selected.Tag is string tag)
            _activeDetailsTab = tag;
        SyncDetailsWidth();
        // Panels live under x:Load="False" — only touch them once realized
        // (i.e. after the panel has been opened at least once).
        if (_detailsRealized)
        {
            ApplyDetailsTab();
            RefreshDetails();
        }
    }

    /// Details column target width: user drag wins, else the Agent panel gets
    /// more room than the lists.
    private const double AgentSidebarWidth = 420;
    private double DetailsWidth => _detailsUserWidth ?? (_activeDetailsTab == "Agent" ? AgentSidebarWidth : SidebarWidth);
    /// Tabs (right) column target width: user drag wins, else the default.
    private double TabsWidth => _tabsUserWidth ?? SidebarWidth;

    // ---- Sidebar drag-resize grips ----------------------------------------

    private const double PanelMinWidth = 200, PanelMaxWidth = 600;
    private double? _detailsUserWidth, _tabsUserWidth;
    private bool _gripDragL, _gripDragR;
    private double _gripStartX, _gripStartW;

    private void Grip_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        bool open = ReferenceEquals(sender, DetailsGrip) ? _detailsOpen : _sidebarOpen;
        if (open)
            SetElementCursor((UIElement)sender, InputSystemCursorShape.SizeWestEast);
    }

    private void Grip_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetElementCursor((UIElement)sender, InputSystemCursorShape.Arrow);

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool left = ReferenceEquals(sender, DetailsGrip);
        if (left ? !_detailsOpen : !_sidebarOpen)
            return;
        _gripDragL = left;
        _gripDragR = !left;
        _gripStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _gripStartW = left ? DetailsSurface.ActualWidth : SidebarSurface.ActualWidth;
        _suppressPaneResize = true;    // one swapchain resize on release, not per frame
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_gripDragL && !_gripDragR)
            return;
        double dx = e.GetCurrentPoint(RootGrid).Position.X - _gripStartX;
        if (_gripDragL)
        {
            double w = Math.Clamp(_gripStartW + dx, PanelMinWidth, PanelMaxWidth);
            _detailsUserWidth = w;
            DetailsSurface.Width = w;
            DetailsContent.Width = w;
        }
        else
        {
            double w = Math.Clamp(_gripStartW - dx, PanelMinWidth, PanelMaxWidth);
            _tabsUserWidth = w;
            SidebarSurface.Width = w;
            Sidebar.Width = w;
        }
        e.Handled = true;
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_gripDragL && !_gripDragR)
            return;
        _gripDragL = _gripDragR = false;
        ((UIElement)sender).ReleasePointerCaptures();
        _suppressPaneResize = false;
        FlushPendingResizes();
    }

    private static void SetElementCursor(UIElement el, InputSystemCursorShape shape)
    {
        try { s_protectedCursorProp?.SetValue(el, InputSystemCursor.Create(shape)); }
        catch { /* reflection blocked: cursor stays default */ }
    }

    /// Re-animate the open details column when a tab switch changes its target
    /// width (280 lists ↔ 420 agent). Closed panel: SetDetails applies it on open.
    private void SyncDetailsWidth()
    {
        DetailsContent.Width = DetailsWidth;
        if (!_detailsOpen || Math.Abs(DetailsSurface.ActualWidth - DetailsWidth) < 0.5)
            return;
        _suppressPaneResize = true;                              // see SetSidebar
        PreGrowPaneSizes(DetailsSurface.ActualWidth - DetailsWidth);
        AnimateWidth(DetailsSurface, DetailsWidth, () =>
        {
            _suppressPaneResize = false;
            FlushPendingResizes();
        });
    }

    // ---- Detail panels (Info / Outline / Git / Files) --------------------
    // The whole DetailsBodyHost subtree is x:Load="False"; realizing it at
    // window load inside the Width=0 column crashed first layout. So it stays
    // deferred until the panel first opens.

    private bool _detailsRealized;
    private string _activeDetailsTab = "Info";
    private string? _filesDir;   // browse dir for the Files panel (Up/into-folder)
    private bool _showHidden;

    private void EnsureDetailsRealized()
    {
        if (_detailsRealized)
        {
            RefreshDetails();
            return;
        }
        DetailsBodyHost.Visibility = Visibility.Visible;   // build/measure the bodies now (first open)
        Log.Write($"EnsureDetailsRealized: bodies shown InfoPanel={(InfoPanel is null ? "null" : "ok")}");
        _detailsRealized = true;
        ApplyDetailsTab();
        RefreshDetails();
    }

    /// Show only the active section's panel.
    private void ApplyDetailsTab()
    {
        InfoPanel.Visibility   = _activeDetailsTab == "Info"    ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.Visibility = _activeDetailsTab == "Outline" ? Visibility.Visible : Visibility.Collapsed;
        GitPanel.Visibility    = _activeDetailsTab == "Git"     ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility  = _activeDetailsTab == "Files"   ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility  = _activeDetailsTab == "Agent"   ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDetails()
    {
        if (!_detailsRealized)
            return;
        switch (_activeDetailsTab)
        {
            case "Info":    RefreshInfo(); break;
            case "Outline": OutlineList.ItemsSource = CurrentTab()?.CommandHistory; break;
            case "Git":     _ = RefreshGitAsync(); break;
            case "Files":   RefreshFiles(); break;
            case "Agent":   _ = RefreshAgentPanelAsync(); break;
        }
    }

    /// Tab bound to the focused pane (falls back to the sidebar selection).
    private TabVM? CurrentTab()
    {
        var id = _slotTab[_focusedPane];
        if (id != InvalidId)
            foreach (var t in Tabs)
                if (t.Id == id) return t;
        return TabList.SelectedItem as TabVM;
    }

    /// cwd of the focused tab, or a sane fallback dir.
    private string CurrentCwd()
    {
        var c = CurrentTab()?.Cwd;
        if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
            return c!;
        return Environment.CurrentDirectory;
    }

    // ---- Info ----
    private void RefreshInfo()
    {
        InfoCwd.Text = CurrentCwd();
        InfoShell.Text = System.IO.Path.GetFileName(_settings.Shell);
    }

    private void InfoCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(CurrentCwd());
        Clipboard.SetContent(dp);
    }

    // Reveal the tab's cwd in Explorer. The raw cwd may be a WSL path
    // (/mnt/d/velo) that Windows can't open directly, so convert first.
    private void InfoReveal_Click(object sender, RoutedEventArgs e)
    {
        var raw = CurrentTab()?.Cwd;
        var win = ToWindowsPath(raw);
        Log.Write($"reveal: raw={raw ?? "<null>"} win={win ?? "<null>"}");
        // No fallback: explorer.exe opens Documents for any arg it can't parse,
        // which is worse than doing nothing.
        if (win is null || !Directory.Exists(win))
        {
            Log.Write("reveal: skipped (no cwd or directory missing)");
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{win}\"") { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    /// Default WSL distro name (first line of `wsl.exe -l -q`), cached; null if
    /// WSL is unavailable.
    private static string? _wslDistro;
    private static bool _wslDistroProbed;

    private static string? WslDistro()
    {
        if (_wslDistroProbed) return _wslDistro;
        _wslDistroProbed = true;
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode,
            };
            using var p = Process.Start(psi);
            var line = p?.StandardOutput.ReadLine()?.Trim('\0', ' ', '\r', '\n');
            p?.WaitForExit(2000);
            _wslDistro = string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch { _wslDistro = null; }
        return _wslDistro;
    }

    /// Best-effort Windows path for a shell cwd: maps WSL /mnt/&lt;drive&gt;/… to
    /// &lt;DRIVE&gt;:\…, any other absolute unix path to \\wsl.localhost\&lt;distro&gt;\…;
    /// leaves an already-Windows path untouched.
    private static string? ToWindowsPath(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
            return null;
        if (cwd.Length >= 7 && cwd.StartsWith("/mnt/", StringComparison.Ordinal)
            && char.IsLetter(cwd[5]) && cwd[6] == '/')
        {
            var rest = cwd.Substring(7).Replace('/', '\\');
            return $"{char.ToUpperInvariant(cwd[5])}:\\{rest}";
        }
        if (cwd[0] == '/')
        {
            // Inside-WSL path (e.g. /home/user/proj): reachable via the
            // \\wsl.localhost UNC share of the default distro.
            var distro = WslDistro();
            return distro is null ? null : $@"\\wsl.localhost\{distro}{cwd.Replace('/', '\\')}";
        }
        return cwd;
    }

    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ---- Files (lazy tree: VS Code-style explorer) ----
    private void RefreshFiles()
    {
        if (!_detailsRealized)
            return;
        var dir = _filesDir ?? CurrentCwd();
        _filesDir = dir;
        FilesTree.RootNodes.Clear();
        if (!Directory.Exists(dir))
            return;
        foreach (var node in BuildNodes(dir, applyFilter: true))
            FilesTree.RootNodes.Add(node);
    }

    /// One directory level → TreeViewNodes. Dirs get HasUnrealizedChildren so the
    /// expander chevron shows and children load lazily on first expand. Find-box
    /// filter applies only at the root level (children are unfiltered).
    private IEnumerable<TreeViewNode> BuildNodes(string dir, bool applyFilter)
    {
        var filter = applyFilter ? (FilesFind?.Text ?? "") : "";
        var nodes = new List<TreeViewNode>();
        try
        {
            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Where(fi => _showHidden || (fi.Attributes & FileAttributes.Hidden) == 0)
                .Where(fi => filter.Length == 0 || fi.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fi => fi is DirectoryInfo)
                .ThenBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var fi in entries)
            {
                var isDir = fi is DirectoryInfo;
                nodes.Add(new TreeViewNode
                {
                    Content = new FileItem { Path = fi.FullName, Name = fi.Name, IsDir = isDir },
                    HasUnrealizedChildren = isDir,
                });
            }
        }
        catch { /* permission etc. */ }
        return nodes;
    }

    private void FilesTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not FileItem fi)
            return;
        fi.SetOpen(true);   // swap closed → open folder icon
        if (args.Node.HasUnrealizedChildren)
        {
            foreach (var child in BuildNodes(fi.Path, applyFilter: false))
                args.Node.Children.Add(child);
            args.Node.HasUnrealizedChildren = false;
        }
    }

    private void FilesTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Node.Content is FileItem fi)
            fi.SetOpen(false);
    }

    /// Single click on a folder row toggles its inline reveal.
    private void FilesTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // InvokedItem may be the FileItem or the TreeViewNode depending on mode.
        var fi = args.InvokedItem as FileItem
                 ?? (args.InvokedItem as TreeViewNode)?.Content as FileItem;
        if (fi is { IsDir: true })
        {
            var node = FindNode(sender.RootNodes, fi);
            if (node is not null)
                node.IsExpanded = !node.IsExpanded;
        }
        else if (fi is { IsDir: false })
        {
            OpenInEditor(fi.Path); // flips to editor mode itself
        }
    }

    private static TreeViewNode? FindNode(IList<TreeViewNode> nodes, FileItem item)
    {
        foreach (var n in nodes)
        {
            if (ReferenceEquals(n.Content, item))
                return n;
            var hit = FindNode(n.Children, item);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    private void FilesFind_TextChanged(object sender, TextChangedEventArgs e) => RefreshFiles();
    private void FilesRefresh_Click(object sender, RoutedEventArgs e) => RefreshFiles();
    private void FilesHidden_Click(object sender, RoutedEventArgs e) { _showHidden = !_showHidden; RefreshFiles(); }

    private void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        var p = Directory.GetParent(_filesDir ?? CurrentCwd());
        if (p is not null) { _filesDir = p.FullName; RefreshFiles(); }
    }

    /// Double click: open a file, or set a folder as the new tree root.
    private void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FilesTree.SelectedNode?.Content is not FileItem fi)
            return;
        if (fi.IsDir) { _filesDir = fi.Path; RefreshFiles(); }
        else OpenInEditor(fi.Path);
    }

    // ---- Git (porcelain status of the cwd's repo) ----
    private static readonly SolidColorBrush GitBrushMod = new(Windows.UI.Color.FromArgb(255, 0xD2, 0x99, 0x22)); // amber
    private static readonly SolidColorBrush GitBrushAdd = new(Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50)); // green
    private static readonly SolidColorBrush GitBrushDel = new(Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49)); // red
    private static readonly SolidColorBrush GitBrushRen = new(Windows.UI.Color.FromArgb(255, 0x58, 0xA6, 0xFF)); // blue
    private static readonly SolidColorBrush GitBrushUnk = new(Windows.UI.Color.FromArgb(255, 0x8B, 0x94, 0x9E)); // gray

    private async Task RefreshGitAsync()
    {
        if (!_detailsRealized)
            return;
        var dir = CurrentCwd();
        GitBranch.Text = "…";
        GitAdds.Text = "";
        GitDels.Text = "";
        GitRemote.Visibility = Visibility.Collapsed;
        GitFiles.ItemsSource = null;
        GitUnstagedLabel.Text = "Changes";

        var status = await RunGitAsync(dir, "status --porcelain=v1 --branch");
        if (string.IsNullOrWhiteSpace(status))
        {
            GitBranch.Text = "Not a git repository";
            return;
        }

        var lines = status.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string branch = "—";
        var rows = new List<GitFileRow>();
        foreach (var ln in lines)
        {
            if (ln.StartsWith("## "))
            {
                var b = ln.Substring(3);
                int dots = b.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? b.Substring(0, dots) : b;
            }
            else if (ln.Length >= 3)
            {
                rows.Add(MakeGitRow(ln.Substring(0, 2), ln.Substring(3)));
            }
        }
        GitBranch.Text = branch;
        GitUnstagedLabel.Text = rows.Count == 0 ? "No changes" : $"Changes ({rows.Count})";
        GitFiles.ItemsSource = rows;

        var (adds, dels) = ParseShortstat(await RunGitAsync(dir, "diff HEAD --shortstat"));
        GitAdds.Text = adds > 0 ? $"+{adds}" : "";
        GitDels.Text = dels > 0 ? $"−{dels}" : "";

        var remote = (await RunGitAsync(dir, "remote get-url origin")).Trim();
        if (remote.Length > 0)
        {
            GitRemote.Content = remote;
            try { GitRemote.NavigateUri = new Uri(NormalizeRemote(remote)); } catch { /* non-URL remote */ }
            GitRemote.Visibility = Visibility.Visible;
        }
    }

    /// Porcelain "XY path" → a display row. X=index, Y=worktree; we surface the
    /// more meaningful of the two as a single colored badge, and left-truncate the
    /// path so the tail (filename) stays visible.
    private static GitFileRow MakeGitRow(string code, string raw)
    {
        // renames come as "old -> new"; key off the new path.
        var path = raw;
        int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0) path = path.Substring(arrow + 4);

        char c = code.Trim().Length > 0 ? code.Trim()[0] : ' ';
        if (code == "??") c = '?';
        var (letter, brush) = c switch
        {
            'M' => ("M", GitBrushMod),
            'A' => ("A", GitBrushAdd),
            'D' => ("D", GitBrushDel),
            'R' => ("R", GitBrushRen),
            '?' => ("?", GitBrushUnk),
            _   => (c.ToString(), GitBrushUnk),
        };

        const int budget = 30;
        var display = path.Length <= budget ? path : "…" + path.Substring(path.Length - (budget - 1));
        return new GitFileRow { Status = letter, StatusBrush = brush, Display = display, FullPath = path };
    }

    private static (int adds, int dels) ParseShortstat(string s)
    {
        int adds = 0, dels = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+) insertion");
        if (m.Success) int.TryParse(m.Groups[1].Value, out adds);
        m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+) deletion");
        if (m.Success) int.TryParse(m.Groups[1].Value, out dels);
        return (adds, dels);
    }

    /// git@github.com:user/repo.git → https://github.com/user/repo
    private static string NormalizeRemote(string remote)
    {
        if (remote.StartsWith("git@"))
        {
            var at = remote.IndexOf('@');
            var rest = remote.Substring(at + 1).Replace(":", "/");
            remote = "https://" + rest;
        }
        if (remote.EndsWith(".git", StringComparison.Ordinal))
            remote = remote.Substring(0, remote.Length - 4);
        return remote;
    }

    // ---- Git actions ----
    // SplitButton.Click has its own args type; forward to the shared handlers.
    private void GitCommitBtn_Click(SplitButton sender, SplitButtonClickEventArgs args) => GitCommit_Click(sender, null!);
    private void GitForkBtn_Click(SplitButton sender, SplitButtonClickEventArgs args) => GitFetch_Click(sender, null!);

    private async void GitStageAll_Click(object sender, RoutedEventArgs e) => await GitRunRefresh("add -A");

    private async void GitFiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitFileRow row)
            await GitRunRefresh($"add -- \"{row.FullPath}\"");
    }

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        var msg = await PromptAsync("Commit", "Message");
        if (!string.IsNullOrWhiteSpace(msg))
            await GitRunRefresh($"commit -m \"{Escape(msg)}\"");
    }

    private async void GitCommitPush_Click(object sender, RoutedEventArgs e)
    {
        var msg = await PromptAsync("Commit & Push", "Message");
        if (!string.IsNullOrWhiteSpace(msg))
            await GitRunRefresh($"commit -m \"{Escape(msg)}\"", then: "push");
    }

    private async void GitAmend_Click(object sender, RoutedEventArgs e)
    {
        var msg = await PromptAsync("Amend last commit", "New message (blank = keep)");
        var arg = string.IsNullOrWhiteSpace(msg) ? "commit --amend --no-edit" : $"commit --amend -m \"{Escape(msg)}\"";
        await GitRunRefresh(arg);
    }

    private async void GitFetch_Click(object sender, RoutedEventArgs e) => await GitRunRefresh("fetch");
    private async void GitPull_Click(object sender, RoutedEventArgs e) => await GitRunRefresh("pull");
    private async void GitPush_Click(object sender, RoutedEventArgs e) => await GitRunRefresh("push");

    private async Task GitRunRefresh(string args, string? then = null)
    {
        var dir = CurrentCwd();
        await RunGitAsync(dir, args);
        if (then is not null)
            await RunGitAsync(dir, then);
        await RefreshGitAsync();
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"");

    /// Minimal single-line input dialog. Returns null on cancel.
    private async Task<string?> PromptAsync(string title, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, AcceptsReturn = false };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private static async Task<string> RunGitAsync(string dir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
                return "";
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? outp : "";
        }
        catch { return ""; }
    }

    /// Clip a panel to its current (mid-animation) width so any non-shrinkable
    /// content (e.g. the fixed-width details icon row) can't spill into the terminal
    /// while the column is narrower than its content. Fires every layout frame of
    /// the width animation.
    private void Panel_ClipToBounds(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight),
            };
    }

    /// Any tap inside the details body (file tree, git list, info buttons) pulls
    /// keyboard focus into the sidebar, so typing stops reaching the terminal. Hand
    /// focus back — EXCEPT when the tap landed in the Find box (the user is about to
    /// type a filter there). FocusTerminal defers to Low priority, so the control's
    /// own click runs first; only the keyboard focus returns.
    private void DetailsBody_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Ancestor<TextBox>(d) is not null)
            return;
        FocusTerminal();
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var n = d; n is not null; n = VisualTreeHelper.GetParent(n))
            if (n is T t) return t;
        return null;
    }


    // Arm a drag on press; only hand off to the OS move loop once the pointer
    // actually moves past a small threshold. A simple click never enters the loop,
    // so DoubleTapped (maximize/restore) still fires.
    private bool _dragArmed;
    private Point _dragStart;

    private void TitleBarHover_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((UIElement)sender);
        if (!pt.Properties.IsLeftButtonPressed)
            return;
        _dragArmed = true;
        _dragStart = pt.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TitleBarHover_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragArmed)
            return;
        var p = e.GetCurrentPoint((UIElement)sender).Position;
        if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) < 4)
            return;
        _dragArmed = false;
        var el = (UIElement)sender;
        el.ReleasePointerCapture(e.Pointer);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void TitleBarHover_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragArmed = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void TitleBarHover_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => MaximizeRestore_Click(sender, e);

    /// Return keyboard focus to the terminal. Chrome/sidebar interactions steal it;
    /// without this the user "can type at first, then can't".
    private void FocusTerminal()
        // Low priority: a tab switch / new tab / ListView selection moves focus
        // AFTER this runs at normal priority, stealing it back so keys never reach
        // PaneHost (the "can't type until I alt-tab away and back" bug). Deferring to
        // Low runs the focus restore once that churn has settled, so it sticks.
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                // Focus the PaneHost Grid, not the SwapChainPanel: the latter has no
                // Background and isn't hit-test-visible, so Focus() returns false and no
                // KeyDown/CharacterReceived ever fire (the "can't type" bug).
                if (PaneHost is null)
                    return;
                PaneHost.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                Log.Ex("FocusTerminal", ex);
            }
        });

    /// Open the details panel on a given section by name (used by the palette).
    private void OpenDetails(string tag)
    {
        var b = tag switch
        {
            "Outline" => OutlineTabButton,
            "Git" => GitTabButton,
            "Files" => FilesTabButton,
            "Agent" => AgentTabButton,
            _ => InfoTabButton,
        };
        SelectDetailsTab(b);
        SetDetails(true);
    }

    // ---- Command palette --------------------------------------------------

    private void PaletteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenPalette();
    }

    private void OpenPalette()
    {
        ShowPaletteRoot();
        PaletteOverlay.Visibility = Visibility.Visible;
        PaletteSearch.Text = "";
        PaletteSearch.Focus(FocusState.Programmatic);
    }

    private void ClosePalette()
    {
        PaletteOverlay.Visibility = Visibility.Collapsed;
        FocusTerminal();
    }

    /// Root command list.
    private void ShowPaletteRoot() => SetPalettePage(new[]
    {
        new PaletteItem { Label = "Toggle Tabs Panel", Shortcut = "Ctrl+B", Checked = _sidebarOpen, Action = () => SetSidebar(!_sidebarOpen) },
        new PaletteItem { Label = "Toggle Details Panel", Shortcut = "Ctrl+Shift+B", Checked = _detailsOpen, Action = () => SetDetails(!_detailsOpen) },
        new PaletteItem { Label = "Details: Info", Action = () => OpenDetails("Info") },
        new PaletteItem { Label = "Details: Outline", Action = () => OpenDetails("Outline") },
        new PaletteItem { Label = "Details: Git", Action = () => OpenDetails("Git") },
        new PaletteItem { Label = "Details: Files", Action = () => OpenDetails("Files") },
        new PaletteItem { Label = "Theme…", KeepOpen = true, Action = ShowPaletteThemes },
        new PaletteItem { Label = "Clear Screen", Action = ClearScreen },
    });

    /// Theme-picker page (navigated to from "Theme…"; stays open).
    private void ShowPaletteThemes()
        => SetPalettePage(Themes.All.Select(t => new PaletteItem
        {
            Label = t.Name,
            Checked = _settings.ThemeName == t.Name,
            Action = () => ApplyTheme(t),
        }));

    private void SetPalettePage(IEnumerable<PaletteItem> items)
    {
        _paletteSource = items.ToList();
        FilterPalette(PaletteSearch?.Text ?? "");
    }

    private void FilterPalette(string query)
    {
        PaletteItems.Clear();
        foreach (var it in _paletteSource)
            if (string.IsNullOrEmpty(query)
                || it.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
                PaletteItems.Add(it);
        if (PaletteItems.Count > 0)
            PaletteList.SelectedIndex = 0;
    }

    private void InvokePalette(PaletteItem? it)
    {
        if (it is null)
            return;
        if (it.KeepOpen)
        {
            it.Action?.Invoke();          // navigate within the palette
            PaletteSearch.Text = "";
            PaletteSearch.Focus(FocusState.Programmatic);
        }
        else
        {
            ClosePalette();
            it.Action?.Invoke();
        }
    }

    private void PaletteSearch_TextChanged(object sender, TextChangedEventArgs e)
        => FilterPalette(PaletteSearch.Text);

    private void PaletteSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                ClosePalette();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                InvokePalette(PaletteList.SelectedItem as PaletteItem ?? PaletteItems.FirstOrDefault());
                e.Handled = true;
                break;
            case VirtualKey.Down:
                if (PaletteItems.Count > 0)
                    PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, PaletteItems.Count - 1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                if (PaletteItems.Count > 0)
                    PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
        }
    }

    private void PaletteList_ItemClick(object sender, ItemClickEventArgs e)
        => InvokePalette(e.ClickedItem as PaletteItem);

    // Dim area closes; taps on the card itself must not bubble up to it.
    private void PaletteOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ClosePalette();
    private void PaletteCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private unsafe void ClearScreen()
    {
        if (_engine == IntPtr.Zero)
            return;
        // ponytail: shell-agnostic-ish — "clear" works in PowerShell/pwsh/bash;
        // cmd.exe users would prefer "cls". Default shell is PowerShell.
        const string cmd = "clear\r";
        fixed (char* p = cmd)
            Native.velo_paste_utf16(_engine, (ushort*)p, (nuint)cmd.Length);
    }

    // ---- Input forwarding -------------------------------------------------

    private static uint Modifiers()
    {
        uint m = 0;
        if (IsDown(VirtualKey.Control)) m |= 1;
        if (IsDown(VirtualKey.Shift)) m |= 2;
        if (IsDown(VirtualKey.Menu)) m |= 4;
        return m;
    }

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// True when keyboard focus is NOT on the terminal and NOT in a text box — i.e.
    /// it drifted somewhere that would otherwise swallow input (the dead-terminal bug).
    private bool ShouldRouteKeysToTerminal()
    {
        // Editor mode owns its own routing (EditorPanel / EditorTermHost handlers).
        // Without this, typing in the Ctrl+J terminal hits both its own handler AND
        // this root net → every char sent to the PTY twice, and Root_KeyDown steals
        // focus back to PaneHost on every keystroke.
        if (_editorMode) return false;
        var xr = Content?.XamlRoot;
        if (xr is null) return false;
        var focused = FocusManager.GetFocusedElement(xr) as DependencyObject;
        if (focused is null) return true;                    // nobody owns focus → terminal
        if (IsWithin(focused, PaneHost)) return false;       // terminal handler already runs
        if (Ancestor<TextBox>(focused) is not null) return false; // a text box owns its keys
        return true;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (!ShouldRouteKeysToTerminal()) return;
        // Pull focus back to PaneHost: a focused non-text control (e.g. the Info
        // ScrollViewer) forwards keydowns but never raises CharacterReceived, so
        // typed TEXT is lost. Refocusing makes this key's WM_CHAR land on PaneHost
        // (which does raise it) and keeps subsequent keys on the normal path.
        PaneHost.Focus(FocusState.Programmatic);
        Panel_KeyDown(PaneHost, e);
    }

    private void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (ShouldRouteKeysToTerminal())
            Panel_CharacterReceived(PaneHost, e);
    }

    private void Panel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;

        // Route input to the focused split pane (keeps core + slot in sync).
        Native.velo_pane_focus(_engine, FocusedCore());

        // Ctrl++ / Ctrl+- / Ctrl+0: zoom the terminal body (intercept before forwarding).
        if (IsDown(VirtualKey.Control))
        {
            switch ((int)e.Key)
            {
                case 187: case 107: AdjustZoom(0.1); e.Handled = true; return;   // OemPlus / numpad Add
                case 189: case 109: AdjustZoom(-0.1); e.Handled = true; return;  // OemMinus / numpad Subtract
                case 48: case 96: ResetZoom(); e.Handled = true; return;         // 0 / numpad 0
            }
        }

        if (Native.velo_key(_engine, (uint)e.Key, Modifiers()) != 0)
            e.Handled = true;
    }

    // ---- Zoom -------------------------------------------------------------

    private void AdjustZoom(double delta)
    {
        _zoom = Math.Clamp(_zoom + delta, 0.5, 3.0);
        ApplyZoom();
    }

    private void ResetZoom()
    {
        _zoom = 1.0;
        ApplyZoom();
    }

    /// Push the zoomed font size to the core and flash the level toast.
    private void ApplyZoom()
    {
        if (_engine == IntPtr.Zero)
            return;
        Native.velo_set_font_size(_engine, (float)(_settings.FontSize * _zoom));
        ShowZoomToast((int)Math.Round(_zoom * 100));
    }

    /// Show "NNN%" centered at the bottom of the terminal body, then fade it out.
    private void ShowZoomToast(int pct)
    {
        ZoomToastText.Text = $"{pct}%";
        _zoomToastSb?.Stop();
        ZoomToast.Opacity = 1;

        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(700),
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, ZoomToast);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        _zoomToastSb = sb;
        sb.Begin();
    }

    private void Panel_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        Native.velo_pane_focus(_engine, FocusedCore()); // route to the focused pane
        Native.velo_char(_engine, e.Character, Modifiers());
    }

    /// Pane index a press captured, so move/release during a drag keep going
    /// to that pane even when the pointer leaves its bounds. -1 = no drag.
    private int _mousePane = -1;

    /// All pointer input for the pane area lands on PaneHost: the SwapChainPanels
    /// composite their swapchain via DComp (no XAML brush), so they are not
    /// hit-test-visible and their own pointer events never fire. A press focuses
    /// the pane under the pointer and starts forwarding mouse events to it
    /// (selection, or SGR mouse reporting when an app enabled it).
    private void PaneHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        var pressPos = e.GetCurrentPoint(PaneHost).Position;
        if (SeamAt(pressPos) is Branch seam)
        {
            _seamDrag = seam;
            _suppressPaneResize = true;   // one swapchain resize on release
            PaneHost.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        int i = PaneAt(pressPos);
        if (i < 0)
            return;
        _focusedPane = i;
        UpdatePaneFocusRing();
        PaneHost.Focus(FocusState.Pointer);   // SwapChainPanel can't take focus
        Native.velo_pane_focus(_engine, PaneId(_panels[i]));
        // Clicking a split pane makes its tab the active one in the list
        // (suppressed selection: no ShowView relayout, the view is already up).
        if (_slotTab[i] != InvalidId && (TabList.SelectedItem as TabVM)?.Id != _slotTab[i])
        {
            SetActiveTab(_slotTab[i]);
            SelectById(_slotTab[i]);
        }
        _mousePane = i;
        PaneHost.CapturePointer(e.Pointer);
        ForwardMouse(_panels[i], 0, e);
    }

    private void PaneHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        var pos = e.GetCurrentPoint(PaneHost).Position;
        if (_seamDrag is Branch drag)
        {
            double t = _seamVert ? (pos.Y - _seamOrigin) / _seamExtent
                                 : (pos.X - _seamOrigin) / _seamExtent;
            drag.Ratio = Math.Clamp(t, 0.15, 0.85);
            BuildLayout();
            e.Handled = true;
            return;
        }
        // Resize cursor over a seam; back to I-beam when leaving it.
        bool nearSeam = SeamAt(pos) is not null;
        if (nearSeam != _seamHover)
        {
            _seamHover = nearSeam;
            if (nearSeam)
                SetElementCursor(PaneHost, _seamVert ? InputSystemCursorShape.SizeNorthSouth
                                                     : InputSystemCursorShape.SizeWestEast);
            else
                SetPaneCursor(false);
        }
        UpdatePaneCtl(pos);
        int i = _mousePane >= 0 ? _mousePane : PaneAt(pos);
        if (i < 0)
            return;
        ForwardMouse(_panels[i], 1, e);
    }

    private void PaneHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        PaneCtl.Visibility = Visibility.Collapsed;
        if (_engine == IntPtr.Zero)
            return;
        var pos = e.GetCurrentPoint(PaneHost).Position;
        int i = _mousePane >= 0 ? _mousePane : PaneAt(pos);
        if (i < 0)
            return;
        // Leave event: clears any pending click + stale link hover in the
        // engine (see velo_pane_mouse kind == 3), coordinates are unused.
        Native.velo_pane_mouse(_engine, PaneId(_panels[i]), 3, 0, 0, 0, 0);
    }

    // ---- Per-pane split controls (swap position / make full screen) --------

    private int _ctlSlot;   // slot the PaneCtl bar currently floats over

    /// Move the control bar to the hovered pane; hidden outside split mode.
    private void UpdatePaneCtl(Point pos)
    {
        if (_paneCount < 2)
        {
            PaneCtl.Visibility = Visibility.Collapsed;
            return;
        }
        int i = PaneAt(pos);
        if (i < 0)
        {
            PaneCtl.Visibility = Visibility.Collapsed;
            return;
        }
        _ctlSlot = i;
        // Float the bar at the top-centre of the hovered pane's rect (panels are
        // absolutely positioned in the split layout, so read the stored rect).
        double cw = PaneCtl.ActualWidth > 0 ? PaneCtl.ActualWidth : 66;
        PaneCtl.HorizontalAlignment = HorizontalAlignment.Left;
        PaneCtl.VerticalAlignment = VerticalAlignment.Top;
        PaneCtl.Margin = new Thickness(_slotX[i] + _slotW[i] / 2 - cw / 2, _slotY[i] + 6, 0, 0);
        PaneCtl.Visibility = Visibility.Visible;
    }

    /// Exchange this pane's tab with the next slot (cycles positions). The
    /// order persists in the split group so re-showing the view keeps it.
    private void PaneCtlSwap_Click(object sender, RoutedEventArgs e)
    {
        if (_paneCount < 2 || _ctlSlot >= _paneCount || _viewRoot is null)
            return;
        int next = (_ctlSlot + 1) % _paneCount;
        uint a = _slotTab[_ctlSlot], b = _slotTab[next];
        // Exchange the two leaves' tabs in the tree; re-showing rebuilds slots/pills.
        var la = PaneTree.Find(_viewRoot, a);
        var lb = PaneTree.Find(_viewRoot, b);
        if (la is null || lb is null)
            return;
        (la.TabId, lb.TabId) = (lb.TabId, la.TabId);
        uint active = _slotTab[_focusedPane];
        ShowView(active);
        RefreshTabList();              // pill order follows the pane order
        FocusTerminal();
    }

    /// Pull this pane's tab out of its split group and show it full-size; the
    /// group survives with the remaining tabs (dissolving below 2 members).
    private void PaneCtlFull_Click(object sender, RoutedEventArgs e)
    {
        if (_paneCount < 2 || _ctlSlot >= _paneCount)
            return;
        uint keep = _slotTab[_ctlSlot];
        if (keep == InvalidId)
            return;
        // Pull `keep` out of its tree (siblings promote, tree survives); show it full.
        if (RootContaining(keep) is PaneNode root)
        {
            var promoted = PaneTree.Remove(root, keep);
            int oi = _trees.IndexOf(root);
            if (promoted is null or Leaf) { if (oi >= 0) _trees.RemoveAt(oi); }
            else if (oi >= 0) _trees[oi] = promoted;
        }
        // ponytail: hidden core panes keep their old bindings until the next
        // split re-binds them. Add an unbind FFI call if idle renders of hidden
        // panes ever show up in profiles.
        SetActiveTab(keep);
        ShowView(keep);
        RefreshTabList();              // pill row shrinks (or dissolves back to rows)
        SelectById(keep);
        FocusTerminal();
    }

    private void PaneHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        if (_seamDrag is not null)
        {
            _seamDrag = null;
            PaneHost.ReleasePointerCapture(e.Pointer);
            _suppressPaneResize = false;
            FlushPendingResizes();
            e.Handled = true;
            return;
        }
        int i = _mousePane >= 0 ? _mousePane : PaneAt(e.GetCurrentPoint(PaneHost).Position);
        _mousePane = -1;
        PaneHost.ReleasePointerCapture(e.Pointer);
        if (i < 0)
            return;
        ForwardMouse(_panels[i], 2, e);
    }

    /// Index of the visible pane under `pos` (PaneHost coordinates), or -1.
    private int PaneAt(Windows.Foundation.Point pos)
    {
        for (int i = 0; i < _paneCount && i < _panels.Length; i++)
        {
            var p = _panels[i];
            if (p.Visibility != Visibility.Visible)
                continue;
            var r = p.TransformToVisual(PaneHost)
                     .TransformBounds(new Windows.Foundation.Rect(0, 0, p.ActualWidth, p.ActualHeight));
            if (pos.X >= r.X && pos.X <= r.X + r.Width && pos.Y >= r.Y && pos.Y <= r.Y + r.Height)
                return i;
        }
        return -1;
    }

    /// Wheel over the pane area. Handled on PaneHost, not the SwapChainPanels —
    /// like PointerPressed above, the panels are not hit-test-visible, so their
    /// own wheel events never fire.
    private void PaneHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        int i = PaneAt(e.GetCurrentPoint(PaneHost).Position);
        if (i < 0)
            return;
        int delta = e.GetCurrentPoint(PaneHost).Properties.MouseWheelDelta;
        // Multiply first: precision touchpads report sub-120 deltas that
        // delta/120*3 would truncate to zero.
        int lines = delta * 3 / 120;
        if (lines == 0)
            return;
        var panel = _panels[i];
        double sx = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : _lastScale;
        double sy = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : _lastScale;
        var p = e.GetCurrentPoint(panel).Position;
        Native.velo_pane_scroll(_engine, PaneId(panel), lines,
            (float)(p.X * sx), (float)(p.Y * sy), Modifiers());
    }

    private void ForwardMouse(SwapChainPanel panel, uint kind, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        double sx = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : _lastScale;
        double sy = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : _lastScale;
        var pt = e.GetCurrentPoint(panel);
        var p = pt.Position;
        Native.velo_pane_mouse(_engine, PaneId(panel), kind,
            (float)(p.X * sx), (float)(p.Y * sy), MouseButton(pt.Properties), Modifiers());
    }

    /// SGR button code (0=left, 1=middle, 2=right) for a pointer event. Uses
    /// PointerUpdateKind so releases (where the pressed flags are already
    /// false) still identify the button.
    private static uint MouseButton(Microsoft.UI.Input.PointerPointProperties p)
        => p.PointerUpdateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed or
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 1u,
            Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed or
            Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 2u,
            _ => p.IsMiddleButtonPressed ? 1u : p.IsRightButtonPressed ? 2u : 0u,
        };

    // ---- Tab actions ------------------------------------------------------

    private void AddTab_Click(object sender, RoutedEventArgs e) => AddTab();

    // The + button spawns the configured default shell, ungrouped.
    private void AddTab() => AddProfileTab(null);

    /// Spawn a tab for `profile` (null = configured default shell) and append it ungrouped.
    private void AddProfileTab(ShellProfile? profile)
    {
        var vm = SpawnTab(profile);
        if (vm is null)
            return;
        _layout.Add(vm);           // new tabs are always ungrouped (appended at the end)
        RefreshTabList();
        TabList.SelectedItem = vm; // drives velo_tab_set_active via SelectionChanged
        FocusTerminal();
    }

    // ---- Browser tabs -----------------------------------------------------
    // Browser tabs live entirely C#-side (no core session). Synthetic ids start
    // high so they never collide with core tab ids (small sequential uints).
    private uint _nextBrowserId = 0x8000_0000u;

    /// Append a browser tab and select it (SelectionChanged overlays its WebView2).
    private void AddBrowserTab(string? url = null)
    {
        var vm = new TabVM(_nextBrowserId++, "New Tab")
        {
            IsBrowser = true,
            ShellKind = "web",
            IconFile = "web.svg",
            BrowserUrl = string.IsNullOrWhiteSpace(url) ? "https://www.google.com/" : url,
        };
        _layout.Add(vm);
        RefreshTabList();
        TabList.SelectedItem = vm;
    }

    private TabVM? TabById(uint id)
    {
        if (id == InvalidId)
            return null;
        foreach (var t in Tabs)
            if (t.Id == id)
                return t;
        return null;
    }

    /// velo_tab_set_active, skipping browser tabs (they have no core session).
    private void SetActiveTab(uint id)
    {
        if (_engine == IntPtr.Zero || TabById(id) is { IsBrowser: true })
            return;
        Native.velo_tab_set_active(_engine, id);
    }

    /// Overlay each browser tab's WebView2 pane onto its slot's rect (collapsing that
    /// slot's terminal SwapChainPanel), and collapse any browser view not shown. Call
    /// after BuildLayout so the slot rects (_slotX/Y/W/H) are current.
    private void SyncBrowserPanes()
    {
        int overlayAt = PaneHost.Children.IndexOf(SplitOverlay); // keep drop overlay/ring on top
        var shown = new HashSet<uint>();
        for (int i = 0; i < _paneCount; i++)
        {
            var tab = TabById(_slotTab[i]);
            bool isBrowser = tab is { IsBrowser: true };
            if (isBrowser)
                _panels[i].Visibility = Visibility.Collapsed;
            if (!isBrowser)
                continue;
            shown.Add(tab!.Id);
            var view = tab.View ??= new BrowserView(
                tab, () => CloseTab(tab.Id), () => MaximizeBrowserTab(tab), tab.BrowserUrl);
            Grid.SetRow(view, 0);
            Grid.SetColumn(view, 0);
            if (_paneCount <= 1)
            {
                view.HorizontalAlignment = HorizontalAlignment.Stretch;
                view.VerticalAlignment = VerticalAlignment.Stretch;
                view.Width = double.NaN;
                view.Height = double.NaN;
                view.Margin = new Thickness(0);
            }
            else
            {
                view.HorizontalAlignment = HorizontalAlignment.Left;
                view.VerticalAlignment = VerticalAlignment.Top;
                view.Width = Math.Max(1, _slotW[i]);
                view.Height = Math.Max(1, _slotH[i]);
                view.Margin = new Thickness(_slotX[i], _slotY[i], 0, 0);
            }
            if (view.Parent is null)
                PaneHost.Children.Insert(overlayAt < 0 ? PaneHost.Children.Count : overlayAt, view);
            view.Visibility = Visibility.Visible;
        }
        foreach (var t in Tabs)
            if (t.IsBrowser && t.View is not null && !shown.Contains(t.Id))
                t.View.Visibility = Visibility.Collapsed;
    }

    /// Pop a browser out of its split so it fills the body (the split's other panes
    /// promote to their own view).
    private void MaximizeBrowserTab(TabVM tab)
    {
        if (RootContaining(tab.Id) is PaneNode root)
        {
            var promoted = PaneTree.Remove(root, tab.Id);
            int oi = _trees.IndexOf(root);
            if (promoted is null or Leaf) { if (oi >= 0) _trees.RemoveAt(oi); }
            else if (oi >= 0) _trees[oi] = promoted;
        }
        ShowView(tab.Id);
        RefreshTabList();
        SelectById(tab.Id);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tab.View?.FocusWeb());
    }

    /// New empty group holding one fresh default-shell tab.
    private void NewGroupWithTab()
    {
        var vm = SpawnTab(null);
        if (vm is null)
            return;
        var grp = new TabGroup($"Group {CountGroups() + 1}");
        vm.Group = grp;
        grp.Members.Add(vm);
        vm.RefreshGrouping();
        _layout.Add(grp);
        RefreshTabList();
        TabList.SelectedItem = vm;
        FocusTerminal();
    }

    /// Core spawn: optionally switch the engine shell for this tab, then restore the
    /// default so the + button stays on the configured shell. Returns null on failure.
    private TabVM? SpawnTab(ShellProfile? profile)
    {
        if (_engine == IntPtr.Zero)
            return null;
        if (profile is not null)
            SetShell(profile.Command);
        uint id = Native.velo_tab_new(_engine);
        if (profile is not null)
            SetShell(_settings.Shell);
        Log.Write($"SpawnTab: velo_tab_new -> {id} ({profile?.Name ?? "default"})");
        if (id == uint.MaxValue)
        {
            Log.Write("SpawnTab: ABORT, velo_tab_new returned uint.MaxValue (shell spawn failed?)");
            return null;
        }
        var vm = new TabVM(id, profile?.Name ?? ShellDisplayName(_settings.Shell));
        vm.LaunchCmd = profile?.Command ?? _settings.Shell;
        ApplyShellKind(vm, profile?.Command ?? _settings.Shell);
        return vm;
    }

    /// Human tab name for a launch command ("wsl.exe -d Ubuntu" -> "Ubuntu").
    private static string ShellDisplayName(string cmd)
    {
        if (cmd.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            return "PowerShell";
        if (cmd.Contains("cmd", StringComparison.OrdinalIgnoreCase))
            return "Command Prompt";
        if (cmd.Contains("wsl", StringComparison.OrdinalIgnoreCase))
        {
            var d = cmd.IndexOf("-d ", StringComparison.OrdinalIgnoreCase);
            if (d >= 0)
            {
                var distro = cmd[(d + 3)..].Trim().Split(' ')[0];
                if (distro.Length > 0)
                    return distro;
            }
            return "WSL";
        }
        return "Shell";
    }

    /// Sets the row's shell-kind label, derived from the launch command. For WSL
    /// this probes the distro's default shell off the UI thread and fills the
    /// label in once it's known instead of blocking tab creation on it.
    private void ApplyShellKind(TabVM vm, string command)
    {
        if (command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
        {
            vm.ShellKind = "pwsh";
            vm.IconFile = "powershell.svg";
            return;
        }
        if (command.Contains("cmd", StringComparison.OrdinalIgnoreCase))
        {
            vm.ShellKind = "cmd";
            vm.IconFile = "cmd.svg";
            return;
        }
        if (!command.Contains("wsl", StringComparison.OrdinalIgnoreCase))
            return;
        var dIdx = command.IndexOf("-d ", StringComparison.OrdinalIgnoreCase);
        // Plain `wsl.exe` (no -d): default distro — probe with an empty name.
        var distro = dIdx < 0 ? "" : command[(dIdx + 3)..].Trim().Split(' ')[0];
        vm.IconFile = distro.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)
            ? "ubuntu.svg" : "linux.svg";
        _ = ShellProfiles.DefaultShellForAsync(distro).ContinueWith(t =>
        {
            var kind = t.Result;
            DispatcherQueue.TryEnqueue(() => vm.ShellKind = kind);
        }, TaskScheduler.Default);
    }

    /// Windows-Terminal-style dropdown: list installed shells + New group.
    private void NewTabMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        var flyout = new MenuFlyout();
        foreach (var p in ShellProfiles.All())
        {
            var prof = p;
            var item = new MenuFlyoutItem { Text = prof.Name, Icon = ShellIcon(prof.Icon) };
            item.Click += (_, _) => AddProfileTab(prof);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var browser = new MenuFlyoutItem { Text = "Browser", Icon = ShellIcon("web.svg") };
        browser.Click += (_, _) => AddBrowserTab();
        flyout.Items.Add(browser);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(flyout, "New group", NewGroupWithTab);
        flyout.ShowAt(fe);
    }

    /// 18px SVG brand icon for a shell profile (Assets/ShellIcons/<file>).
    private static IconElement ShellIcon(string file) => new ImageIcon
    {
        Width = 18,
        Height = 18,
        Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri($"ms-appx:///Assets/ShellIcons/{file}")),
    };

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is uint id)
            CloseTab(id);
    }

    // Close button is hidden until its tab row is hovered.
    private void TabRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabVM t) t.IsHovered = true;
        SetRowClose(sender, 1);
    }

    private void TabRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabVM t) t.IsHovered = false;
        SetRowClose(sender, 0);
    }

    // The row fill (active/hover/multi-select) is VM-bound; this only fades the
    // close button in and the shell-kind label out on hover.
    private static void SetRowClose(object sender, double opacity)
    {
        if (sender is not Grid g)
            return;
        foreach (var child in g.Children)
        {
            if (child is Button b) b.Opacity = opacity;
        }
    }

    private void CloseTab(uint id)
    {
        if (_engine == IntPtr.Zero)
            return;
        if (TabById(id) is { IsBrowser: true } b)
        {
            if (b.View is not null) { PaneHost.Children.Remove(b.View); b.View.Dispose(); b.View = null; }
            Log.Write($"CloseTab: browser id={id}");
            HandleTabGone(id);
            return;
        }
        Native.velo_tab_close(_engine, id);   // core frees the session + unbinds panes
        Log.Write($"CloseTab: id={id}, remaining={Tabs.Count - 1}");
        HandleTabGone(id);
    }

    /// Shared cleanup after a tab disappears (user close or shell exit): drop it
    /// from the list and from any pane, then re-pack the remaining panes so only the
    /// closed pane goes away (the others keep their split).
    private void HandleTabGone(uint id)
    {
        // Was the closed tab showing in the current view's panes?
        bool wasShown = false;
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] == id) { wasShown = true; break; }

        // Drop it from its split tree first (siblings promote; a lone leaf dissolves).
        if (RootContaining(id) is PaneNode root)
        {
            var promoted = PaneTree.Remove(root, id);
            int oi = _trees.IndexOf(root);
            if (promoted is null or Leaf) { if (oi >= 0) _trees.RemoveAt(oi); }
            else if (oi >= 0) _trees[oi] = promoted;
        }
        RemoveTab(id);
        if (Tabs.Count == 0)
        {
            Close();
            return;
        }
        if (!wasShown)
            return;
        // The visible tab went away: re-show the view anchored on a survivor (a
        // remaining pane of this view, else the selection, else the first tab).
        uint show = InvalidId;
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] != InvalidId && _slotTab[i] != id) { show = _slotTab[i]; break; }
        if (show == InvalidId)
            show = (TabList.SelectedItem as TabVM)?.Id ?? InvalidId;
        if (show == InvalidId || show == id)
            show = Tabs.Count > 0 ? Tabs[0].Id : InvalidId;
        if (show == InvalidId)
            return;
        SetActiveTab(show);
        ShowView(show);
        SelectById(show);
    }

    private void RemoveTab(uint id)
    {
        TabVM? hit = null;
        foreach (var t in OrderedTabs())
            if (t.Id == id) { hit = t; break; }
        if (hit is null)
            return;
        _selected.Remove(hit);
        DetachFromLayout(hit);     // drops it from a bare slot or a group (empty groups vanish)
        RefreshTabList();
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Track the active-tab fill regardless of suppression (RefreshTabList and
        // ToggleGroupCollapse re-select programmatically with _suppressSelection
        // set) so the active row's shared fill follows the selection.
        foreach (var r in e.RemovedItems) if (r is TabVM rv) rv.IsActive = false;
        foreach (var a in e.AddedItems) if (a is TabVM av) av.IsActive = true;
        UpdateTitleBarTitle();
        if (_suppressSelection || _engine == IntPtr.Zero)
            return;
        if (TabList.SelectedItem is TabVM t)
        {
            // Picking a tab leaves editor mode (workspace state survives in the
            // engine; re-entering shows the same files).
            SetEditorMode(false);
            SetActiveTab(t.Id);          // no-op for browser tabs (no core session)
            // Browser-style: show this tab's whole view (its split group, or the
            // tab alone) instead of swapping it into the focused pane.
            ShowView(t.Id);
            _filesDir = null;            // Files panel follows the new tab's cwd
            if (_detailsOpen) RefreshDetails();
            if (t.IsBrowser)
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => t.View?.FocusWeb());
            else
                FocusTerminal();
        }
        else if (TabList.SelectedItem is TabGroup or SplitRowVM)
        {
            // Header/pill rows aren't a tab themselves: bounce selection back so
            // the row doesn't show the selected fill (pills handle their own taps).
            _suppressSelection = true;
            TabList.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _suppressSelection = false;
        }
    }

    // ---- Split view (drag a tab onto an edge → up to 4 panes) ------------
    //
    // Browser-style split groups: a tab belongs to at most ONE group; selecting
    // any member shows the whole group, selecting a non-member shows that tab
    // alone. Multiple groups coexist; the pane slots only ever show the active
    // tab's view.

    // Active split trees (binary PaneNode). Only one is shown at a time (the view
    // of the active tab); a tab with no tree is a lone pane.
    private readonly List<PaneNode> _trees = new();
    private PaneNode _viewRoot = null!;     // the tree the body currently shows
    private uint _viewTab = InvalidId;      // tab whose view the body shows
    private uint _prevViewTab = InvalidId;  // the view before it (drop-anchor rescue)
    // Per-pane drop preview: the leaf to split + its edge, captured during DragOver.
    private uint _previewTargetTab = InvalidId;
    private Edge _previewEdge;

    /// The tree whose leaves include `tabId`, or null when the tab is a lone pane.
    private PaneNode? RootContaining(uint tabId) => _trees.Find(t => PaneTree.Contains(t, tabId));

    /// Lay the pane slots out for `activeId`'s view (its split group, or the tab
    /// alone) and bind every visible pane. The single authority for what the
    /// terminal body shows.
    private void ShowView(uint activeId)
    {
        if (_engine == IntPtr.Zero || activeId == InvalidId)
            return;
        if (activeId != _viewTab)
        {
            _prevViewTab = _viewTab;
            _viewTab = activeId;
        }
        var root = RootContaining(activeId);
        if (root is null)
        {
            _viewRoot = new Leaf(activeId);
            _paneCount = 1;
            _slotTab[0] = activeId;
            for (int i = 1; i < MaxPanes; i++)
                _slotTab[i] = InvalidId;
            _focusedPane = 0;
            PaneCtl.Visibility = Visibility.Collapsed;
        }
        else
        {
            _viewRoot = root;
            var leaves = PaneTree.Leaves(root).ToList();   // in-order = slot order
            _paneCount = Math.Min(leaves.Count, MaxPanes);
            for (int i = 0; i < MaxPanes; i++)
                _slotTab[i] = i < _paneCount ? leaves[i] : InvalidId;
            _focusedPane = Math.Max(0, leaves.IndexOf(activeId));
            for (int i = 1; i < _paneCount; i++)
                CreateCorePane(i);          // no-op if the slot already has one
        }
        BuildLayout();
        for (int i = 1; i < _paneCount; i++)
            BindSwapchain(i);               // no-op once bound
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] != InvalidId && _slotCore[i] != InvalidId
                && TabById(_slotTab[i]) is not { IsBrowser: true })
                Native.velo_pane_bind(_engine, _slotCore[i], _slotTab[i]);
        Native.velo_pane_focus(_engine, _slotCore[_focusedPane]);
    }

    /// Tap on a split-row pill: activate that member tab (shows the whole split).
    private void SplitPill_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TabVM t)
            return;
        e.Handled = true;
        if (_engine == IntPtr.Zero)
            return;
        SetEditorMode(false);
        SetActiveTab(t.Id);
        ShowView(t.Id);
        SelectById(t.Id);          // split branch: clears row selection, sets IsActive
        _filesDir = null;
        if (_detailsOpen) RefreshDetails();
        FocusTerminal();
    }

    private void TabList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragTab = e.Items.Count > 0 ? e.Items[0] as TabVM : null;
        if (_dragTab is null)
            return;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        // Selecting a tab to drag it switched the body to that tab. Restore the view
        // that was showing before (the split we're dropping INTO) so the preview
        // shrinks those panes — not the dragged tab. _prevViewTab was set by that
        // selection's ShowView; keep it as the anchor for PreviewSplit / AddPane.
        _dragAnchorTab = _prevViewTab != InvalidId && _prevViewTab != _dragTab.Id
            ? _prevViewTab
            : InvalidId;
        if (_dragAnchorTab != InvalidId)
            ShowView(_dragAnchorTab);
    }

    private void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragTab = null;
        _dragAnchorTab = InvalidId;
        HideDropZone();
    }

    private void PaneHost_DragOver(object sender, DragEventArgs e)
    {
        if (_dragTab is null || Tabs.Count < 2)
            return;
        var pos = e.GetPosition(PaneHost);
        int slot = PaneAt(pos);
        if (slot < 0)
            return;
        uint target = _slotTab[slot];
        // Pointer over the dragged tab's own pane in a real split → nothing to do.
        if (target == _dragTab.Id && _paneCount > 1)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        // Target pane's tree already full → can't subdivide it; whole-body hint.
        var root = RootContaining(target);
        int leaves = root is null ? 1 : PaneTree.Count(root);
        if (leaves >= MaxPanes)
        {
            ShowDropRect(0, 0, PaneHost.ActualWidth, PaneHost.ActualHeight);
            _previewTargetTab = InvalidId;
            return;
        }
        PreviewSplit(slot, pos);
    }

    /// Live drop preview: shrink ONLY the pane under the pointer into the half
    /// opposite the nearest edge, and highlight the incoming half. Pure XAML
    /// relayout (DComp scales the swapchain) — no native resize, which would wedge
    /// the drag's modal input loop. Real reflow happens on drop. This per-pane
    /// targeting is what produces mixed layouts.
    private void PreviewSplit(int slot, Point pos)
    {
        double px = _slotX[slot], py = _slotY[slot], pw = _slotW[slot], ph = _slotH[slot];
        Edge edge = EdgeInRect(pos, px, py, pw, ph);
        _previewTargetTab = _slotTab[slot];
        _previewEdge = edge;
        // Highlight-only preview: mark the half of the target pane the new pane will
        // take. We deliberately DON'T shrink the existing pane during the drag —
        // a layout resize detaches the swapchain content, and a RenderTransform
        // scales the font. The real reflow (normal text, correct size) happens on
        // drop. This matches how Windows Terminal previews a split.
        bool vertical = edge is Edge.Top or Edge.Bottom;
        bool front = edge is Edge.Left or Edge.Top;
        double dx, dy, dw, dh;
        if (vertical)
        {
            double half = ph / 2;
            dx = px; dw = pw; dh = half;
            dy = front ? py : py + half;
        }
        else
        {
            double half = pw / 2;
            dy = py; dh = ph; dw = half;
            dx = front ? px : px + half;
        }
        ShowDropRect(dx, dy, dw, dh);
    }

    private void PaneHost_DragLeave(object sender, DragEventArgs e)
    {
        HideDropZone();
        _previewTargetTab = InvalidId;
    }

    private void PaneHost_Drop(object sender, DragEventArgs e)
    {
        var t = _dragTab;
        _dragTab = null;
        // Capture the preview target now: DragItemsCompleted / a relayout clears it
        // before the deferred AddPane below runs.
        uint target = _previewTargetTab;
        Edge edge = _previewEdge;
        HideDropZone();
        _previewTargetTab = InvalidId;
        if (t is null || target == InvalidId)
            return;
        // Defer (Low priority = after the drag's modal loop fully unwinds): the OS
        // drag-drop is still completing on this thread. Doing the swapchain create +
        // blocking GPU resize here wedges the drag's input loop and freezes the whole
        // app.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AddPane(t, edge, target));
    }

    /// Nearest edge of a rect to the pointer (rect in the same coord space as p).
    private static Edge EdgeInRect(Point p, double x, double y, double w, double h)
    {
        double rx = w > 0 ? (p.X - x) / w : 0.5;
        double ry = h > 0 ? (p.Y - y) / h : 0.5;
        double dl = rx, dr = 1 - rx, dt = ry, db = 1 - ry;
        double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (m == dl) return Edge.Left;
        if (m == dr) return Edge.Right;
        if (m == dt) return Edge.Top;
        return Edge.Bottom;
    }

    /// Highlight one cell of the previewed layout (PaneHost coords; SplitOverlay
    /// stretches over PaneHost so a top-left margin lands in the same space).
    private void ShowDropRect(double x, double y, double w, double h)
    {
        SplitOverlay.Visibility = Visibility.Visible;
        DropZone.Opacity = 1;
        DropZone.HorizontalAlignment = HorizontalAlignment.Left;
        DropZone.VerticalAlignment = VerticalAlignment.Top;
        DropZone.Margin = new Thickness(x, y, 0, 0);
        DropZone.Width = w;
        DropZone.Height = h;
    }

    private void HideDropZone()
    {
        SplitOverlay.Visibility = Visibility.Collapsed;
        DropZone.Opacity = 0;
    }

    /// Drop `dropped` against one edge of the pane showing `targetTab`: split ONLY
    /// that leaf into a branch (orientation + child order from the edge), leaving the
    /// rest of the tree untouched — this is what makes mixed layouts possible. The
    /// dropped tab leaves any other tree first.
    private void AddPane(TabVM dropped, Edge edge, uint targetTab)
    {
        if (_engine == IntPtr.Zero)
            return;
        if (targetTab == InvalidId || targetTab == dropped.Id)
            targetTab = _prevViewTab;
        if (targetTab == InvalidId || targetTab == dropped.Id)
            return;

        // Pull the dropped tab out of whatever tree it was in (promote its sibling).
        var oldRoot = RootContaining(dropped.Id);
        if (oldRoot is not null)
        {
            var promoted = PaneTree.Remove(oldRoot, dropped.Id);
            int oi = _trees.IndexOf(oldRoot);
            if (promoted is null or Leaf) { if (oi >= 0) _trees.RemoveAt(oi); }
            else if (oi >= 0) _trees[oi] = promoted;
        }

        // Resolve the target's tree fresh (removal above may have changed it).
        var root = RootContaining(targetTab);
        if (root is null)
        {
            root = new Leaf(targetTab);
            _trees.Add(root);
        }
        if (PaneTree.Count(root) >= MaxPanes)
            return;
        bool vertical = edge is Edge.Top or Edge.Bottom;
        bool front = edge is Edge.Left or Edge.Top;
        var newRoot = PaneTree.SplitLeaf(root, targetTab, dropped.Id, vertical, front);
        int ri = _trees.IndexOf(root);
        if (ri >= 0) _trees[ri] = newRoot; else _trees.Add(newRoot);

        SetActiveTab(dropped.Id);
        ShowView(dropped.Id);
        RefreshTabList();              // members collapse into their pill row
        SelectById(dropped.Id);        // suppressed handler: no double ShowView
        FocusTerminal();
    }

    /// Right-click a split pill: rename / unjoin the whole split / close this tab.
    private void SplitPill_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TabVM t)
            return;
        var flyout = new MenuFlyout();
        Add(flyout, "Rename", () => BeginRename(t));
        if (RootContaining(t.Id) is PaneNode root)
            Add(flyout, "Unjoin tabs", () => UnjoinSplit(root));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(flyout, "Close", () => CloseTab(t.Id));
        ShowFlyout(flyout, fe, e);
    }

    /// Split a set of tabs into one side-by-side (columns) tree, capped at MaxPanes.
    /// Each tab leaves any tree it was already in.
    private void JoinTabs(List<TabVM> tabs)
    {
        if (_engine == IntPtr.Zero || tabs.Count < 2)
            return;
        var members = tabs.Take(MaxPanes).Select(t => t.Id).ToList();
        foreach (var id in members)
        {
            if (RootContaining(id) is PaneNode old)
            {
                var promoted = PaneTree.Remove(old, id);
                int oi = _trees.IndexOf(old);
                if (promoted is null or Leaf) { if (oi >= 0) _trees.RemoveAt(oi); }
                else if (oi >= 0) _trees[oi] = promoted;
            }
        }
        _trees.Add(PaneTree.Columns(members));
        SetActiveTab(members[0]);
        ShowView(members[0]);
        RefreshTabList();
        SelectById(members[0]);
        FocusTerminal();
    }

    /// Dissolve a split tree back into separate tabs; the first leaf becomes active.
    private void UnjoinSplit(PaneNode root)
    {
        if (_engine == IntPtr.Zero)
            return;
        uint first = PaneTree.Leaves(root).FirstOrDefault(InvalidId);
        _trees.Remove(root);
        if (first != InvalidId)
        {
            SetActiveTab(first);
            ShowView(first);
            SelectById(first);
        }
        RefreshTabList();
        FocusTerminal();
    }

    /// Build a split-group row body: one equal-width pill per member (icon + title
    /// + hover-reveal close), filling the row. Assembled in code because a nested
    /// DataTemplate can't distribute row width evenly or wire per-pill close cleanly.
    private Microsoft.UI.Xaml.UIElement BuildSplitPills(SplitRowVM row)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var member in row.Members)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var close = new Button
            {
                Content = "",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Opacity = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = member.Id,
            };
            close.Click += TabClose_Click;

            var icon = new Image { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Source = member.IconSource };
            var title = new TextBlock
            {
                Text = member.Title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(close, 2);

            var inner = new Grid { ColumnSpacing = 5 };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.Children.Add(icon);
            inner.Children.Add(title);
            inner.Children.Add(close);

            // Active-member fill (same brush as full tab rows), bound live so it
            // follows focus without a rebuild.
            var fillBrush = Application.Current.Resources.TryGetValue("ListViewItemBackgroundSelected", out var res)
                && res is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            var fill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = fillBrush,
                IsHitTestVisible = false,
            };
            fill.SetBinding(UIElement.OpacityProperty, new Microsoft.UI.Xaml.Data.Binding
            {
                Path = new Microsoft.UI.Xaml.PropertyPath("FillOpacity"),
                Source = member,
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
            });

            var body = new Grid();
            body.Children.Add(fill);
            body.Children.Add(inner);

            var pill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 6, 5),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Child = body,
                DataContext = member,
            };
            pill.Tapped += SplitPill_Tapped;
            pill.ContextRequested += SplitPill_ContextRequested;
            pill.PointerEntered += (_, _) => close.Opacity = 1;
            pill.PointerExited += (_, _) => close.Opacity = 0;
            Grid.SetColumn(pill, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(pill);
        }
        return grid;
    }

    /// Realize the current view (`_viewRoot`) into panel geometry. Single pane
    /// stretches (auto-resizes with the window). Splits use absolute rects computed
    /// from the tree so arbitrary nesting works; a PaneHost SizeChanged recomputes
    /// them. Slot order = in-order leaves (matches how ShowView fills `_slotTab`).
    private void BuildLayout()
    {
        for (int i = 0; i < MaxPanes; i++)
            _panels[i].Visibility = i < _paneCount ? Visibility.Visible : Visibility.Collapsed;

        // Single container cell: panels position themselves absolutely (multi) or
        // stretch (single) inside it, regardless of any legacy grid tracks.
        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();
        PaneHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        double W = PaneHost.ActualWidth, H = PaneHost.ActualHeight;

        if (_paneCount <= 1 || _viewRoot is null)
        {
            var p = _panels[0];
            Grid.SetRow(p, 0); Grid.SetColumn(p, 0);
            p.HorizontalAlignment = HorizontalAlignment.Stretch;
            p.VerticalAlignment = VerticalAlignment.Stretch;
            p.Width = double.NaN; p.Height = double.NaN;
            p.Margin = new Thickness(0);
            _slotX[0] = 0; _slotY[0] = 0; _slotW[0] = W; _slotH[0] = H;
            PaneHost.UpdateLayout();
            PushPaneSize(p);
            UpdatePaneFocusRing();
            SyncBrowserPanes();
            return;
        }

        if (W <= 0 || H <= 0)
            return;                     // PaneHost not measured yet; SizeChanged reruns us

        int slot = 0;
        PlaceNode(_viewRoot, 0, 0, W, H, ref slot);
        PaneHost.UpdateLayout();
        for (int i = 0; i < _paneCount; i++)
            PushPaneSize(_panels[i]);
        UpdatePaneFocusRing();
        SyncBrowserPanes();
    }

    /// Accent outline over the focused pane's rect; hidden outside split mode.
    private void UpdatePaneFocusRing()
    {
        if (_paneCount <= 1 || _focusedPane < 0 || _focusedPane >= _paneCount)
        {
            PaneFocusRing.Visibility = Visibility.Collapsed;
            return;
        }
        Grid.SetRow(PaneFocusRing, 0);
        Grid.SetColumn(PaneFocusRing, 0);
        PaneFocusRing.Width = Math.Max(1, _slotW[_focusedPane]);
        PaneFocusRing.Height = Math.Max(1, _slotH[_focusedPane]);
        PaneFocusRing.Margin = new Thickness(_slotX[_focusedPane], _slotY[_focusedPane], 0, 0);
        PaneFocusRing.Visibility = Visibility.Visible;
    }

    // ---- Split seam drag-resize --------------------------------------------

    private Branch? _seamDrag;
    private bool _seamVert;
    private double _seamOrigin, _seamExtent; // branch rect start/size on the drag axis
    private bool _seamHover;

    /// Branch whose split seam lies within a few px of `pt` (PaneHost coords),
    /// walking the tree with PlaceNode's geometry. Sets the _seam* fields.
    private Branch? SeamAt(Point pt)
        => _viewRoot is null || _paneCount <= 1
            ? null
            : SeamIn(_viewRoot, 0, 0, PaneHost.ActualWidth, PaneHost.ActualHeight, pt);

    private Branch? SeamIn(PaneNode node, double x, double y, double w, double h, Point pt)
    {
        if (node is not Branch b)
            return null;
        const double grab = 4;
        double fa = b.Ratio ?? (double)PaneTree.Count(b.A) / PaneTree.Count(node);
        if (b.Vertical)
        {
            double ha = h * fa;
            if (pt.X >= x && pt.X <= x + w && Math.Abs(pt.Y - (y + ha)) <= grab)
            {
                _seamVert = true; _seamOrigin = y; _seamExtent = h;
                return b;
            }
            return SeamIn(b.A, x, y, w, ha, pt) ?? SeamIn(b.B, x, y + ha, w, h - ha, pt);
        }
        else
        {
            double wa = w * fa;
            if (pt.Y >= y && pt.Y <= y + h && Math.Abs(pt.X - (x + wa)) <= grab)
            {
                _seamVert = false; _seamOrigin = x; _seamExtent = w;
                return b;
            }
            return SeamIn(b.A, x, y, wa, h, pt) ?? SeamIn(b.B, x + wa, y, w - wa, h, pt);
        }
    }

    private const double SplitGap = 1;   // seam between panes

    /// Recursively assign each leaf a rect and position its panel absolutely.
    private void PlaceNode(PaneNode node, double x, double y, double w, double h, ref int slot)
    {
        if (node is Leaf)
        {
            int i = slot++;
            if (i >= MaxPanes) return;
            var p = _panels[i];
            Grid.SetRow(p, 0); Grid.SetColumn(p, 0);
            p.HorizontalAlignment = HorizontalAlignment.Left;
            p.VerticalAlignment = VerticalAlignment.Top;
            p.Width = Math.Max(1, w);
            p.Height = Math.Max(1, h);
            p.Margin = new Thickness(x, y, 0, 0);
            _slotX[i] = x; _slotY[i] = y; _slotW[i] = w; _slotH[i] = h;
            return;
        }
        var b = (Branch)node;
        double g = SplitGap / 2;
        // Weight the split by each child's LEAF COUNT, not a fixed 0.5, so a chain
        // of same-orientation splits comes out with equal-sized panes instead of
        // halving each time (1/2, 1/4, 1/8…). ponytail: ignores b.Ratio — manual
        // drag-resize (Phase 3) will reintroduce it, blended with these weights.
        double fa = b.Ratio ?? (double)PaneTree.Count(b.A) / PaneTree.Count(node);
        if (b.Vertical)
        {
            double ha = h * fa;
            PlaceNode(b.A, x, y, w, ha - g, ref slot);
            PlaceNode(b.B, x, y + ha + g, w, h - ha - g, ref slot);
        }
        else
        {
            double wa = w * fa;
            PlaceNode(b.A, x, y, wa - g, h, ref slot);
            PlaceNode(b.B, x + wa + g, y, w - wa - g, h, ref slot);
        }
    }

    private void PaneHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_engine != IntPtr.Zero && _paneCount > 1)
            BuildLayout();
    }

    /// Lazily create the core pane backing slot `i`; stashes its swapchain to bind
    /// once the panel is realized (BindSwapchain).
    private bool CreateCorePane(int i)
    {
        if (i == 0 || _slotCore[i] != InvalidId)
            return true;
        IntPtr swap;
        uint pid;
        unsafe
        {
            IntPtr s;
            pid = Native.velo_pane_new(_engine, &s);
            swap = s;
        }
        if (pid == InvalidId || swap == IntPtr.Zero)
        {
            Log.Write("CreateCorePane: velo_pane_new failed");
            return false;
        }
        _slotCore[i] = pid;
        _slotSwap[i] = swap;
        _panels[i].Tag = pid;
        return true;
    }

    private void BindSwapchain(int i)
    {
        if (_slotSwap[i] == IntPtr.Zero)
            return;
        _panels[i].As<ISwapChainPanelNative>().SetSwapChain(_slotSwap[i]);
        _slotSwap[i] = IntPtr.Zero;
    }

    /// Tapping a tab (or empty list space) must hand focus back to the terminal;
    /// the ListView grabs it on click and never returns it otherwise.
    private void TabList_Tapped(object sender, TappedRoutedEventArgs e) => FocusTerminal();

    private void SelectById(uint id)
    {
        // Split members have no own row (they live in a pill row): drive the
        // active flag + title directly and clear the list selection.
        if (RootContaining(id) is not null)
        {
            // Deselect FIRST: the SelectionChanged handler clears the removed
            // item's IsActive, which would wipe the flag set below.
            _suppressSelection = true;
            TabList.SelectedItem = null;
            _suppressSelection = false;
            foreach (var t in Tabs)
                t.IsActive = t.Id == id;
            UpdateTitleBarTitle();
            return;
        }
        foreach (var t in Tabs)
        {
            if (t.Id == id)
            {
                _suppressSelection = true;
                TabList.SelectedItem = t;
                _suppressSelection = false;
                return;
            }
        }
    }

    private void SwitchSelection(bool forward)
    {
        if (Tabs.Count == 0)
            return;
        int cur = TabList.SelectedItem is TabVM t ? Tabs.IndexOf(t) : 0;
        int n = Tabs.Count;
        int next = forward ? (cur + 1) % n : (cur + n - 1) % n;
        TabList.SelectedItem = Tabs[next]; // not suppressed: drives set_active
    }

    // ---- Tab grouping -----------------------------------------------------

    /// Every TabVM in display order (a group's members appear where the group sits).
    // Groups pinned to the top, ungrouped tabs below — see RefreshTabList.
    private IEnumerable<TabVM> OrderedTabs()
    {
        foreach (var entry in _layout)
            if (entry is TabGroup g)
                foreach (var m in g.Members) yield return m;
        foreach (var entry in _layout)
            if (entry is TabVM t) yield return t;
    }

    /// Regenerate Tabs (flat, for switch/index logic) + TabListItems (display) from
    /// _layout. Preserves the active selection across the rebuild.
    private void RefreshTabList()
    {
        var active = TabList.SelectedItem as TabVM;

        Tabs.Clear();
        foreach (var t in OrderedTabs())
            Tabs.Add(t);

        _suppressSelection = true;
        TabListItems.Clear();
        // Split-group members collapse into ONE pill row, emitted where the
        // first member sits; the remaining members vanish from their own rows.
        var seenTrees = new HashSet<PaneNode>();
        void Emit(TabVM m)
        {
            var root = RootContaining(m.Id);
            if (root is null)
            {
                TabListItems.Add(m);
                return;
            }
            if (!seenTrees.Add(root))
                return;
            var row = new SplitRowVM();
            foreach (var tid in PaneTree.Leaves(root))
                foreach (var tv in Tabs)
                    if (tv.Id == tid) { row.Members.Add(tv); break; }
            row.Content = BuildSplitPills(row);
            TabListItems.Add(row);
        }
        // Groups first (header + members), then all ungrouped tabs below.
        bool firstGroup = true;
        foreach (var entry in _layout)
        {
            if (entry is TabGroup g)
            {
                g.ShowSeparator = !firstGroup;   // divider only between groups, not above the first
                firstGroup = false;
                TabListItems.Add(g);
                if (!g.IsCollapsed)
                    foreach (var m in g.Members) Emit(m);
            }
        }
        foreach (var entry in _layout)
            if (entry is TabVM t)
                Emit(t);
        // Restore the active highlight if its row is still visible (not in a collapsed
        // group). Core binding is independent of ListView selection, so a hidden active
        // tab keeps rendering in its pane regardless.
        if (active is not null && TabListItems.Contains(active))
            TabList.SelectedItem = active;
        _suppressSelection = false;
    }

    /// Remove a tab from wherever it lives in _layout: a bare entry, or a group's
    /// members (an emptied group is dropped). Does not refresh — caller does.
    private void DetachFromLayout(TabVM t)
    {
        if (_layout.Remove(t))
            return;
        foreach (var entry in _layout)
        {
            if (entry is TabGroup g && g.Members.Remove(t))
            {
                if (g.Members.Count == 0)
                    _layout.Remove(g);
                return;
            }
        }
    }

    // ---- Multi-select (Ctrl+Click) ----------------------------------------

    private void TabRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TabVM t)
            return;
        // Right-click opens the context menu — must NOT touch the multi-select set,
        // else the pending selection is gone before the menu is built.
        if (e.GetCurrentPoint(fe).Properties.IsRightButtonPressed)
            return;
        if ((Modifiers() & 1) != 0)            // Ctrl held → toggle into the group set
        {
            if (!_selected.Add(t)) { _selected.Remove(t); t.IsMultiSelected = false; }
            else t.IsMultiSelected = true;
            e.Handled = true;                  // suppress the normal active-select
        }
        else
        {
            ClearMultiSelect();                // plain click drops any pending selection
        }
    }

    private void ClearMultiSelect()
    {
        foreach (var t in _selected) t.IsMultiSelected = false;
        _selected.Clear();
    }

    // ---- Group / ungroup --------------------------------------------------

    private void GroupAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_selected.Count == 0)
            return;
        // The active tab joins the group even if it was never Ctrl+Clicked.
        var set = new HashSet<TabVM>(_selected);
        if (CurrentTab() is TabVM cur) set.Add(cur);
        GroupTabs(OrderedTabs().Where(set.Contains).ToList());
    }

    /// Pull `tabs` (in display order) out of their current slots into a new group,
    /// inserted at the position of the first one.
    private void GroupTabs(List<TabVM> tabs)
    {
        if (tabs.Count == 0)
            return;
        var grp = new TabGroup($"Group {CountGroups() + 1}");
        var sel = new HashSet<TabVM>(tabs);

        var next = new List<object>();
        bool inserted = false;
        void Take(TabVM t)
        {
            if (!inserted) { next.Add(grp); inserted = true; }
            t.Group = grp;
            grp.Members.Add(t);
            t.RefreshGrouping();
        }
        foreach (var entry in _layout)
        {
            if (entry is TabVM t)
            {
                if (sel.Contains(t)) Take(t); else next.Add(t);
            }
            else if (entry is TabGroup g)
            {
                var keep = new List<TabVM>();
                foreach (var m in g.Members)
                {
                    if (sel.Contains(m)) Take(m); else keep.Add(m);
                }
                if (keep.Count > 0)
                {
                    g.Members.Clear();
                    foreach (var m in keep) g.Members.Add(m);
                    next.Add(g);
                }
            }
        }
        _layout.Clear();
        _layout.AddRange(next);
        ClearMultiSelect();
        RefreshTabList();
    }

    private int CountGroups()
    {
        int n = 0;
        foreach (var e in _layout) if (e is TabGroup) n++;
        return n;
    }

    /// Dissolve a group: its members become bare tabs in place.
    private void Ungroup(TabGroup g)
    {
        int i = _layout.IndexOf(g);
        if (i < 0)
            return;
        _layout.RemoveAt(i);
        var members = g.Members.ToList();
        g.Members.Clear();
        for (int k = 0; k < members.Count; k++)
        {
            members[k].Group = null;
            members[k].RefreshGrouping();
            _layout.Insert(i + k, members[k]);
        }
        RefreshTabList();
    }

    /// Pop a single tab out of its group (the group survives if others remain).
    private void RemoveFromGroup(TabVM t)
    {
        if (t.Group is not TabGroup g)
            return;
        int gi = _layout.IndexOf(g);
        g.Members.Remove(t);
        t.Group = null;
        t.RefreshGrouping();
        if (g.Members.Count == 0) { _layout.RemoveAt(gi); _layout.Insert(gi, t); }
        else _layout.Insert(gi + 1, t);
        RefreshTabList();
    }

    // Single tap toggles collapse; double tap renames. Collapse updates the list
    // INCREMENTALLY (adds/removes only the member rows) instead of via
    // RefreshTabList's full Clear+rebuild — a full rebuild destroyed this header's
    // ListView container between the two taps, so DoubleTapped never fired and
    // rename was dead. Keeping the header container alive, Tapped then DoubleTapped
    // both land on it as intended.
    private void GroupHeader_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabGroup g)
            ToggleGroupCollapse(g);
    }

    // Presenter hover is disabled list-wide (see TabList ItemContainerStyle), so
    // the group header draws its own hover fill.
    private void GroupHeader_PointerEntered(object sender, PointerRoutedEventArgs e) => SetHeaderHover(sender, 1);
    private void GroupHeader_PointerExited(object sender, PointerRoutedEventArgs e) => SetHeaderHover(sender, 0);

    private static void SetHeaderHover(object sender, double opacity)
    {
        if (sender is Grid g)
            foreach (var child in g.Children)
                if (child is Border { Name: "HoverFill" } hf) { hf.Opacity = opacity; break; }
    }

    private void GroupHeader_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabGroup g)
        {
            ToggleGroupCollapse(g);   // undo the collapse the paired single-tap applied
            BeginRename(g);
            e.Handled = true;
        }
    }

    /// Expand/collapse a group by inserting/removing just its member rows, keeping
    /// the header's ListView container alive (so gestures on it aren't interrupted).
    private void ToggleGroupCollapse(TabGroup g)
    {
        int hi = TabListItems.IndexOf(g);
        if (hi < 0)
            return;
        var active = TabList.SelectedItem as TabVM;
        _suppressSelection = true;
        if (g.IsCollapsed)
        {
            g.IsCollapsed = false;
            for (int i = 0; i < g.Members.Count; i++)
                TabListItems.Insert(hi + 1 + i, g.Members[i]);
        }
        else
        {
            g.IsCollapsed = true;
            foreach (var m in g.Members.ToList())
                TabListItems.Remove(m);
        }
        if (active is not null && TabListItems.Contains(active))
            TabList.SelectedItem = active;
        _suppressSelection = false;
    }

    private void SetGroupColor(TabGroup g, string hex)
    {
        g.ColorHex = hex;
        foreach (var m in g.Members) m.RefreshGrouping();
    }

    // ---- Inline rename ----------------------------------------------------

    private void BeginRename(object item)
    {
        if (item is TabVM t) t.IsEditing = true;
        else if (item is TabGroup g) g.IsEditing = true;
        DispatcherQueue.TryEnqueue(() => FocusRenameBox(item));
    }

    private void FocusRenameBox(object item)
    {
        if (TabList.ContainerFromItem(item) is FrameworkElement c &&
            FindDescendant(c, "RenameBox") is TextBox tb)
        {
            tb.Focus(FocusState.Programmatic);
            tb.SelectAll();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { CommitRename(sender, save: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CommitRename(sender, save: false); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename(sender, save: true);

    private void CommitRename(object sender, bool save)
    {
        if (sender is TextBox tb)
        {
            string v = tb.Text.Trim();
            if (tb.DataContext is TabVM t)
            {
                if (save && v.Length > 0) t.Title = v;
                t.IsEditing = false;
                UpdateTitleBarTitle();
            }
            else if (tb.DataContext is TabGroup g)
            {
                if (save && v.Length > 0) g.Name = v;
                g.IsEditing = false;
            }
        }
        FocusTerminal();
    }

    private void TabRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabVM t) { BeginRename(t); e.Handled = true; }
    }

    // ---- Context menus ----------------------------------------------------

    private void TabRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TabVM t)
            return;
        var flyout = new MenuFlyout();
        Add(flyout, "Rename", () => BeginRename(t));
        // New group from the current selection + the ACTIVE tab (the open tab is
        // part of the working set even though it was never Ctrl+Clicked), or just
        // this tab when nothing is selected.
        List<TabVM> groupTabs;
        if (_selected.Count > 0 && (_selected.Contains(t) || t.IsActive))
        {
            var set = new HashSet<TabVM>(_selected) { t };
            if (CurrentTab() is TabVM cur) set.Add(cur);
            groupTabs = OrderedTabs().Where(set.Contains).ToList();
        }
        else
        {
            groupTabs = new List<TabVM> { t };
        }
        Add(flyout, groupTabs.Count > 1 ? $"New group ({groupTabs.Count} tabs)" : "New group",
            () => { GroupTabs(groupTabs); ClearMultiSelect(); });
        // Join = split-view the working set side-by-side (capped at MaxPanes).
        if (groupTabs.Count > 1)
        {
            int n = Math.Min(groupTabs.Count, MaxPanes);
            Add(flyout, $"Join tabs ({n})", () => { JoinTabs(groupTabs); ClearMultiSelect(); });
        }
        if (RootContaining(t.Id) is PaneNode splitRoot)
            Add(flyout, "Unjoin tabs", () => UnjoinSplit(splitRoot));
        if (t.Group is not null)
        {
            Add(flyout, "Remove from group", () => RemoveFromGroup(t));
            Add(flyout, "Ungroup", () => Ungroup(t.Group!));
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(flyout, "Close", () => CloseTab(t.Id));
        ShowFlyout(flyout, fe, e);
    }

    // Only reached when a row/group didn't already claim the event (they mark e.Handled).
    private void TabList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        var flyout = new MenuFlyout();
        Add(flyout, "New tab", AddTab);
        Add(flyout, "New group", NewGroupWithTab);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(flyout, "Exit", Close);
        ShowFlyout(flyout, fe, e);
    }

    private void GroupHeader_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TabGroup g)
            return;
        var flyout = new MenuFlyout();
        Add(flyout, "Rename", () => BeginRename(g));
        Add(flyout, g.IsCollapsed ? "Expand" : "Collapse",
            () => { g.IsCollapsed = !g.IsCollapsed; RefreshTabList(); });

        var color = new MenuFlyoutSubItem { Text = "Colour" };
        foreach (var (name, hex) in TabGroup.Swatches)
        {
            var swatch = name;
            var h = hex;
            var item = new MenuFlyoutItem
            {
                Text = swatch,
                Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(TabGroup.ParseHex(h)) },
            };
            item.Click += (_, _) => SetGroupColor(g, h);
            color.Items.Add(item);
        }
        flyout.Items.Add(color);

        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(flyout, "Ungroup", () => Ungroup(g));
        Add(flyout, "Close all tabs", () =>
        {
            foreach (var id in g.Members.Select(m => m.Id).ToList()) CloseTab(id);
        });
        ShowFlyout(flyout, fe, e);
    }

    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            GroupHeader_ContextRequested(fe, null!);
    }

    private static void Add(MenuFlyout f, string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        f.Items.Add(item);
    }

    private static void ShowFlyout(MenuFlyout flyout, FrameworkElement target, ContextRequestedEventArgs? e)
    {
        if (e is not null && e.TryGetPosition(target, out var p))
        {
            flyout.ShowAt(target, new FlyoutShowOptions { Position = p });
            e.Handled = true;
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    // ---- Drag a tab in / out of a group -----------------------------------

    private void TabList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragTab is not null)
            e.AcceptedOperation = DataPackageOperation.Move;
    }

    private void TabList_Drop(object sender, DragEventArgs e)
    {
        var dragged = _dragTab;
        _dragTab = null;
        if (dragged is null)
            return;
        e.Handled = true;
        object? target = ItemAtPoint(e.GetPosition(TabList).Y);
        DropTab(dragged, target);
    }

    /// Reposition `dragged` based on what it was dropped onto:
    ///  - a group header → join that group (appended)
    ///  - a grouped tab  → join that tab's group, just before it
    ///  - an ungrouped tab → become ungrouped, just before it
    ///  - empty space (null) → become ungrouped at the end
    private void DropTab(TabVM dragged, object? target)
    {
        if (ReferenceEquals(target, dragged))
            return;
        DetachFromLayout(dragged);

        if (target is TabGroup g)
        {
            dragged.Group = g;
            g.Members.Add(dragged);
        }
        else if (target is TabVM tt && tt.Group is TabGroup tg)
        {
            dragged.Group = tg;
            int idx = tg.Members.IndexOf(tt);
            tg.Members.Insert(idx < 0 ? tg.Members.Count : idx, dragged);
        }
        else if (target is TabVM ut)
        {
            dragged.Group = null;
            int idx = _layout.IndexOf(ut);
            _layout.Insert(idx < 0 ? _layout.Count : idx, dragged);
        }
        else
        {
            dragged.Group = null;
            _layout.Add(dragged);
        }
        dragged.RefreshGrouping();
        RefreshTabList();
    }

    /// The TabListItems entry whose row contains vertical position `y` (relative to TabList).
    private object? ItemAtPoint(double y)
    {
        foreach (var item in TabListItems)
        {
            if (TabList.ContainerFromItem(item) is not FrameworkElement c)
                continue;
            var top = c.TransformToVisual(TabList).TransformPoint(new Point(0, 0)).Y;
            if (y >= top && y <= top + c.ActualHeight)
                return item;
        }
        return null;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;
            if (FindDescendant(child, name) is FrameworkElement hit)
                return hit;
        }
        return null;
    }

    // ---- Settings ---------------------------------------------------------

    /// Last font family pushed into the core ("" = bundled default, which the
    /// engine starts with — so the startup ApplySettings skips the rebuild).
    private string _appliedFontFamily = "";

    /// Push the current settings into the core + chrome. The titlebar background
    /// tracks the terminal background so they read as one blended surface.
    private void ApplySettings()
    {
        if (_engine == IntPtr.Zero)
            return;
        Native.velo_set_recovery(_engine, _settings.SessionRecovery ? (byte)1 : (byte)0);
        Native.velo_set_font_size(_engine, (float)(_settings.FontSize * _zoom));
        // Font family: pushing rebuilds the font + every atlas, so only on change.
        if (_appliedFontFamily != _settings.FontFamily)
        {
            var fam = _settings.FontFamily ?? "";
            unsafe
            {
                fixed (char* p = fam)
                    Native.velo_set_font_family(_engine, (ushort*)p, (nuint)fam.Length);
            }
            _appliedFontFamily = _settings.FontFamily ?? "";
        }
        ApplyAppFont();
        Native.velo_set_ligatures(_engine, _settings.Ligatures ? (byte)1 : (byte)0);
        byte cursorStyle = _settings.CursorStyle switch
        {
            "Block" => 1,
            "Bar" => 2,
            "Underline" => 3,
            _ => 0,
        };
        Native.velo_set_cursor(_engine, cursorStyle, _settings.CursorBlink ? (byte)1 : (byte)0);
        SetShell(_settings.Shell);
        PushPalette(Themes.ByName(_settings.ThemeName));
        var (r, g, b) = _settings.BackgroundRgb();
        Native.velo_set_bg(_engine, r, g, b);
        Native.velo_set_bg_alpha(_engine, (float)_settings.BackgroundOpacity);

        // With an OPAQUE terminal bg, the whole XAML root gets the same solid
        // color: any hole the swapchain / panels don't cover (sidebar-animation
        // seam, freshly exposed area on maximize) reads as terminal instead of
        // the default #2C2C2C window fill. RootGrid, not ContentRoot: the seam
        // can land on grid areas outside the center column. A translucent
        // terminal keeps it null — an opaque brush under the swapchain would
        // kill the backdrop blur-through (see the XAML note on ContentRoot).
        RootGrid.Background = _settings.BackgroundOpacity >= 0.999
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, r, g, b))
            : null;

        // Same color as the Win32 class brush: Windows uses it to pre-fill the
        // region a maximize exposes, killing the black flash before first paint.
        var brush = CreateSolidBrush((uint)(r | (g << 8) | (b << 16)));
        SetClassLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(this), GCLP_HBRBACKGROUND, brush);
        if (_classBgBrush != IntPtr.Zero)
            DeleteObject(_classBgBrush);
        _classBgBrush = brush;
        UpdatePanelTint();                        // title bar tint + panels (transparent if backdrop-tinted)
        // NOTE: SwapChainPanel does NOT support the Background property — setting
        // it throws COMException 0x80004005 and aborts the entire Loaded handler,
        // leaving the panel blank. The terminal bg is owned by the Rust renderer
        // (velo_set_bg / velo_set_bg_alpha); no XAML brush is needed here.
    }

    /// Push a theme's 16 ANSI colors + fg/cursor/selection to the core.
    private unsafe void PushPalette(VeloTheme t)
    {
        if (_engine == IntPtr.Zero)
            return;
        var bytes = t.PaletteBytes();
        fixed (byte* p = bytes)
            Native.velo_set_palette(_engine, p, (nuint)bytes.Length);
    }

    /// Select a theme: persist it, then re-apply (palette + bg + chrome tint all
    /// follow the theme's background).
    private void ApplyTheme(VeloTheme t)
    {
        _settings.ThemeName = t.Name;
        _settings.BackgroundHex = t.Bg;
        _settings.Save();
        ApplySettings();
        FocusTerminal();
    }

    private unsafe void SetShell(string shell)
    {
        if (_engine == IntPtr.Zero || string.IsNullOrWhiteSpace(shell))
            return;
        fixed (char* p = shell)
            Native.velo_set_shell(_engine, (ushort*)p, (nuint)shell.Length);
    }

    /// Apply the backdrop. Simple, untinted backdrop kinds (Mica / Mica Alt /
    /// Acrylic); the tint comes from the panels' own translucent brush in
    /// UpdatePanelTint, so the terminal, title bar and both panels all composite the
    /// SAME way (one flat tint over the same raw backdrop) and read consistently —
    /// and Mica vs Mica Alt stay visually distinct.
    private void ApplyBackdrop()
    {
        SystemBackdrop = _settings.Backdrop switch
        {
            "MicaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "None" => null,
            _ => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
        };
    }

    /// One translucent brush (bg color + opacity) drives the title bar and both
    /// panels, so they match the terminal's tint over the backdrop.
    private void UpdatePanelTint()
    {
        var (r, g, b) = _settings.BackgroundRgb();
        byte a = (byte)Math.Round(_settings.BackgroundOpacity * 255);
        var surface = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));
        SidebarSurface.Background = surface;
        DetailsSurface.Background = surface;
        // Editor tab strip composites like the rest of the chrome, and its
        // text follows the terminal theme's foreground.
        EditorTabs.Background = surface;
        var fg = Themes.Rgb(Themes.ByName(_settings.ThemeName).Fg);
        var fgBrush = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, fg.R, fg.G, fg.B));
        EditorTabs.Foreground = fgBrush;
        // Palette card: same tint as the chrome, but floored so text stays
        // readable over the dim overlay at very low opacity settings.
        byte pa = (byte)Math.Round(Math.Max(_settings.BackgroundOpacity, 0.85) * 255);
        PaletteCard.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(pa, r, g, b));
        // Floating overlay panels sit over the TERMINAL (not the backdrop), so they
        // must be near-opaque or terminal text bleeds through. Floor high.
        byte oa = (byte)Math.Round(Math.Max(_settings.BackgroundOpacity, 0.97) * 255);
        var overlaySurface = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(oa, r, g, b));
        DetailsOverlayPanel.Background = overlaySurface;
        TabsOverlayPanel.Background = overlaySurface;
    }

    /// Chrome font. Panels (Grid) have no FontFamily and WinUI has no WPF-style
    /// font inheritance, so: override the theme resource (future controls, e.g.
    /// new tab headers from templates) and walk the live tree (existing ones).
    private void ApplyAppFont()
    {
        var fam = _settings.ApplyFontToApp && !string.IsNullOrWhiteSpace(_settings.FontFamily)
            ? new FontFamily(_settings.FontFamily)
            : FontFamily.XamlAutoFontFamily;
        Application.Current.Resources["ContentControlThemeFontFamily"] = fam;
        ApplyFontRecursive(RootGrid, fam);
    }

    private static void ApplyFontRecursive(DependencyObject node, FontFamily fam)
    {
        // Icon glyphs (FontIcon's inner TextBlock, caption/toolbar buttons whose
        // Content is a Segoe MDL2/Fluent codepoint) must keep their symbol font
        // or they render as tofu boxes.
        if (node is FontIcon)
            return;
        if (node is Control c && !KeepsOwnFont(c.FontFamily))
            c.FontFamily = fam;
        else if (node is TextBlock tb && !KeepsOwnFont(tb.FontFamily))
            tb.FontFamily = fam;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            ApplyFontRecursive(VisualTreeHelper.GetChild(node, i), fam);
    }

    /// Fonts an element sets deliberately (icon faces, monospace previews) —
    /// the app-wide chrome font must not override them.
    private static bool KeepsOwnFont(FontFamily? f)
    {
        string s = f?.Source ?? "";
        return s.Contains("Segoe MDL2") || s.Contains("Segoe Fluent") || s.Contains("Consolas");
    }

    /// Installed font families from the core's font db (sorted, deduped).
    private static unsafe string[] ListInstalledFonts()
    {
        nuint len = 0;
        ushort* p = Native.velo_list_fonts(&len);
        if (p == null || len == 0)
            return Array.Empty<string>();
        var joined = new string((char*)p, 0, (int)len);
        Native.velo_free_utf16(p, len);
        return joined.Split('\n');
    }

    private const string DefaultFontLabel = "Default (Cascadia Code NF, bundled)";

    /// Live settings overlay while open (null = closed). An overlay, not a
    /// ContentDialog: needs an X button, Esc/Enter dismissal, and
    /// click-outside-to-close, none of which ContentDialog gives us.
    private Grid? _settingsOverlay;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOverlay != null)
            return;
        // ---- Appearance page -------------------------------------------
        var fontBox = new NumberBox
        {
            Value = _settings.FontSize,
            Minimum = 6,
            Maximum = 72,
            Width = 140,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        // Terminal font: search box + list of installed families, each row
        // previewed in its own face (Windows-settings style).
        string selectedFont = _settings.FontFamily ?? "";
        var allFonts = ListInstalledFonts();
        var fontSearch = new TextBox { PlaceholderText = "Search fonts" };
        var fontList = new ListView
        {
            Height = 200,
            SelectionMode = ListViewSelectionMode.Single,
        };
        void FillFonts()
        {
            var filter = fontSearch.Text?.Trim() ?? "";
            var items = new List<TextBlock>();
            if (filter.Length == 0
                || DefaultFontLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                items.Add(new TextBlock { Text = DefaultFontLabel, Tag = "" });
            foreach (var f in allFonts)
                if (filter.Length == 0 || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    items.Add(new TextBlock { Text = f, Tag = f, FontFamily = new FontFamily(f) });
            fontList.ItemsSource = items;
            fontList.SelectedItem = items.FirstOrDefault(t => (string)t.Tag == selectedFont);
        }
        fontSearch.TextChanged += (_, _) => FillFonts();
        fontList.SelectionChanged += (_, _) =>
        {
            if (fontList.SelectedItem is TextBlock tb)
                selectedFont = (string)tb.Tag;
        };
        FillFonts();

        var applyAppBox = new CheckBox
        {
            MinWidth = 0,
            IsChecked = _settings.ApplyFontToApp,
        };

        var ligBox = new ToggleSwitch { IsOn = _settings.Ligatures };

        var backdropBox = new ComboBox { Width = 140 };
        foreach (var s in new[] { "Mica", "MicaAlt", "Acrylic", "None" })
            backdropBox.Items.Add(s);
        backdropBox.SelectedItem = _settings.Backdrop;

        var bgBox = new TextBox { Width = 120, Text = _settings.BackgroundHex };

        var opacityBox = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Width = 200,
            Value = _settings.BackgroundOpacity * 100,
            StepFrequency = 5,
        };

        var cursorBox = new ComboBox { Width = 190 };
        foreach (var s in new[] { "Default (shell-controlled)", "Block", "Bar", "Underline" })
            cursorBox.Items.Add(s);
        cursorBox.SelectedIndex = _settings.CursorStyle switch
        {
            "Block" => 1,
            "Bar" => 2,
            "Underline" => 3,
            _ => 0,
        };

        var blinkBox = new ToggleSwitch { IsOn = _settings.CursorBlink };

        // Small-caps section headers + dim hint text (Otty-style pages).
        static TextBlock Section(string s) => new()
        {
            Text = s.ToUpperInvariant(),
            FontSize = 12,
            Opacity = 0.55,
            CharacterSpacing = 150,
            Margin = new Thickness(0, 10, 0, -4),
        };
        static TextBlock Hint(string s) => new()
        {
            Text = s,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        };

        // Card row: title + dim description on the left, the control on the
        // right (settings-app style). RowWide stacks the control underneath
        // for wide inputs (font list, sliders, long text boxes).
        static StackPanel RowText(string title, string desc)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
            if (desc.Length > 0)
                text.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                });
            return text;
        }
        static Border Card(UIElement child) => new()
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = child,
        };
        static Border Row(string title, string desc, FrameworkElement ctl)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = RowText(title, desc);
            Grid.SetColumn(text, 0);
            Grid.SetColumn(ctl, 1);
            ctl.VerticalAlignment = VerticalAlignment.Center;
            if (ctl is ToggleSwitch t)
            {
                t.OnContent = null;
                t.OffContent = null;
                t.MinWidth = 0;
                t.Margin = new Thickness(0, 0, -12, 0); // trim the label gutter
            }
            g.Children.Add(text);
            g.Children.Add(ctl);
            return Card(g);
        }
        static Border RowWide(string title, string desc, FrameworkElement ctl)
        {
            var sp = new StackPanel { Spacing = 10 };
            sp.Children.Add(RowText(title, desc));
            sp.Children.Add(ctl);
            return Card(sp);
        }

        var fontPick = new StackPanel { Spacing = 8 };
        fontPick.Children.Add(fontSearch);
        fontPick.Children.Add(fontList);

        var appearance = new StackPanel { Spacing = 8 };
        appearance.Children.Add(Section("Text"));
        appearance.Children.Add(Row("Font size", "Terminal text size in points", fontBox));
        appearance.Children.Add(RowWide("Terminal font", "Each family previews in its own face", fontPick));
        appearance.Children.Add(Row("Apply font to entire app", "Tabs, panels and dialogs use the terminal font too", applyAppBox));
        appearance.Children.Add(Row("Ligatures", "=> != -> collapse into one glyph", ligBox));
        appearance.Children.Add(Section("Cursor"));
        appearance.Children.Add(Row("Cursor style", "Default lets the shell control it (DECSCUSR)", cursorBox));
        appearance.Children.Add(Row("Cursor blink", "", blinkBox));
        appearance.Children.Add(Section("Window"));
        appearance.Children.Add(Row("Backdrop", "Material behind the window (Mica, Acrylic)", backdropBox));
        appearance.Children.Add(Row("Background tint", "Terminal background as #RRGGBB", bgBox));
        appearance.Children.Add(Row("Background opacity", "0 lets the backdrop blur through, 100 is solid", opacityBox));

        // ---- Terminal page ----------------------------------------------
        var shellBox = new ComboBox
        {
            IsEditable = true,
            Width = 200,
            Text = _settings.Shell,
        };
        foreach (var s in new[] { "powershell.exe", "pwsh.exe", "cmd.exe", "wsl.exe" })
            shellBox.Items.Add(s);

        var shellIntBox = new ToggleSwitch { IsOn = _settings.ShellIntegration };
        var recoveryBox = new ToggleSwitch { IsOn = _settings.SessionRecovery };
        var agentChatBox = new ToggleSwitch { IsOn = _settings.RestoreAgentChat };

        var rerunBox = new ComboBox { Width = 170 };
        foreach (var s in new[] { "Off", "Whitelisted", "All" })
            rerunBox.Items.Add(s);
        rerunBox.SelectedItem = _settings.RerunOnRestore;
        if (rerunBox.SelectedIndex < 0)
            rerunBox.SelectedIndex = 1;

        var whitelistBox = new TextBox
        {
            Text = _settings.RerunWhitelist,
            PlaceholderText = "npm cargo node …",
        };

        var terminal = new StackPanel { Spacing = 8 };
        terminal.Children.Add(Section("Shell"));
        terminal.Children.Add(Row("Shell", "Command new tabs launch", shellBox));
        terminal.Children.Add(Row("Shell integration",
            "Injects OSC 7/133 markers into the shell profile for cwd and per-command tracking; applies to new tabs",
            shellIntBox));
        terminal.Children.Add(Section("Session restore"));
        terminal.Children.Add(Row("Session recovery",
            "Tabs live in a background host process and reattach — with their processes still running — after closing the app",
            recoveryBox));
        terminal.Children.Add(Row("Restore agent chat",
            "Resume the sidebar agent conversation when the app reopens",
            agentChatBox));
        terminal.Children.Add(Row("Re-run processes on restore",
            "When a tab's live session is gone, re-launch the command it was running",
            rerunBox));
        terminal.Children.Add(RowWide("Command whitelist",
            "First words eligible for re-run, space separated",
            whitelistBox));

        // ---- Agent page ---------------------------------------------------
        var defAgentBox = new TextBox
        {
            Width = 220,
            PlaceholderText = "empty = first found",
            Text = _settings.DefaultAgent,
        };
        var agentCmdBox = new TextBox
        {
            PlaceholderText = "headless command, prompt on stdin — e.g. claude -p",
            Text = _settings.CustomAgentCommand,
        };
        var agent = new StackPanel { Spacing = 8 };
        agent.Children.Add(Section("Agent panel"));
        agent.Children.Add(Row("Default agent",
            "Exact dropdown name, e.g. Claude Code (Ubuntu)",
            defAgentBox));
        agent.Children.Add(RowWide("Custom agent command",
            "Extra non-interactive entry in the agent dropdown; receives the prompt on stdin. Applies after restart",
            agentCmdBox));

        // ---- Keybindings page (placeholder) -------------------------------
        var keys = new StackPanel { Spacing = 8 };
        keys.Children.Add(Section("Keybindings"));
        keys.Children.Add(Card(Hint("Customizable keybindings are coming soon.")));

        // ---- Sidebar (searchable) + content ------------------------------
        var content = new ScrollViewer
        {
            Content = appearance,
            Height = 480,
            Width = 520,   // fixed: page switches must not resize the dialog
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 16, 0),
        };
        var pages = new (string Name, UIElement Page)[]
        {
            ("Appearance", appearance),
            ("Terminal", terminal),
            ("Agent", agent),
            ("Keybindings", keys),
        };
        // Individual options for search: typing lists matching options; picking
        // one opens its page, scrolls it into view, and focuses it.
        var options = new (string Label, int Page, FrameworkElement El)[]
        {
            ("Font size", 0, fontBox),
            ("Terminal font", 0, fontSearch),
            ("Apply font to entire app", 0, applyAppBox),
            ("Ligatures", 0, ligBox),
            ("Cursor style", 0, cursorBox),
            ("Cursor blink", 0, blinkBox),
            ("Backdrop", 0, backdropBox),
            ("Background color", 0, bgBox),
            ("Background opacity", 0, opacityBox),
            ("Shell", 1, shellBox),
            ("Shell integration", 1, shellIntBox),
            ("Session recovery", 1, recoveryBox),
            ("Restore agent chat", 1, agentChatBox),
            ("Re-run processes on restore", 1, rerunBox),
            ("Command whitelist", 1, whitelistBox),
            ("Default agent", 2, defAgentBox),
            ("Custom agent command", 2, agentCmdBox),
        };
        var navSearch = new TextBox
        {
            PlaceholderText = "Search settings",
            Margin = new Thickness(0, 0, 0, 8),
        };
        var nav = new ListView { SelectionMode = ListViewSelectionMode.Single };
        void FillNav()
        {
            var q = navSearch.Text?.Trim() ?? "";
            nav.Items.Clear();
            if (q.Length == 0)
            {
                foreach (var (name, _) in pages)
                    nav.Items.Add(name);
                nav.SelectedIndex = 0;
                return;
            }
            foreach (var (label, _, _) in options)
                if (label.Contains(q, StringComparison.OrdinalIgnoreCase))
                    nav.Items.Add(label);
            // No auto-select while searching: selection would move focus away
            // from the search box mid-typing.
        }
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedItem is not string sel)
                return;
            var pi = Array.FindIndex(pages, p => p.Name == sel);
            if (pi >= 0)
            {
                content.Content = pages[pi].Page;
                return;
            }
            var oi = Array.FindIndex(options, o => o.Label == sel);
            if (oi >= 0)
            {
                content.Content = pages[options[oi].Page].Page;
                var el = options[oi].El;
                el.StartBringIntoView();
                (el as Control)?.Focus(FocusState.Programmatic);
            }
        };
        navSearch.TextChanged += (_, _) => FillNav();
        FillNav();

        var sidebar = new StackPanel { Width = 190 };
        sidebar.Children.Add(navSearch);
        sidebar.Children.Add(nav);

        var body = new Grid { ColumnSpacing = 20 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(sidebar, 0);
        body.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        body.Children.Add(content);

        // ---- Card + dim overlay ------------------------------------------
        var closeBtn = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTipService.SetToolTip(closeBtn, "Close (Esc)");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);

        var cardInner = new StackPanel { Spacing = 16 };
        cardInner.Children.Add(header);
        cardInner.Children.Add(body);

        // Card bg: theme background lifted a touch so it separates from the
        // terminal behind the dim scrim.
        var (br, bgr, bbl) = _settings.BackgroundRgb();
        static byte Lift(byte v) => (byte)Math.Min(255, v + 14);
        var card = new Border
        {
            Child = cardInner,
            Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, Lift(br), Lift(bgr), Lift(bbl))),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 70, 70, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            MinWidth = 780,
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var overlay = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x80, 0, 0, 0)),
        };
        overlay.Children.Add(card);
        Grid.SetRowSpan(overlay, Math.Max(1, RootGrid.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, RootGrid.ColumnDefinitions.Count));
        Canvas.SetZIndex(overlay, 1000);

        // ---- Live apply + dismissal --------------------------------------
        // No Save/Cancel: every change lands in _settings, applies on the spot,
        // and persists (so a crash/kill can't lose an applied change).
        void Apply()
        {
            _settings.FontSize = double.IsNaN(fontBox.Value) ? _settings.FontSize : fontBox.Value;
            _settings.FontFamily = selectedFont;
            _settings.ApplyFontToApp = applyAppBox.IsChecked == true;
            _settings.Ligatures = ligBox.IsOn;
            _settings.CursorStyle = cursorBox.SelectedIndex switch
            {
                1 => "Block",
                2 => "Bar",
                3 => "Underline",
                _ => "Default",
            };
            _settings.CursorBlink = blinkBox.IsOn;
            _settings.Shell = string.IsNullOrWhiteSpace(shellBox.Text) ? _settings.Shell : shellBox.Text;
            _settings.ShellIntegration = shellIntBox.IsOn;
            _settings.CustomAgentCommand = agentCmdBox.Text.Trim();
            _settings.DefaultAgent = defAgentBox.Text.Trim();
            _settings.SessionRecovery = recoveryBox.IsOn;
            _settings.RestoreAgentChat = agentChatBox.IsOn;
            _settings.RerunOnRestore = rerunBox.SelectedItem as string ?? _settings.RerunOnRestore;
            _settings.RerunWhitelist = whitelistBox.Text.Trim();
            _settings.BackgroundHex = bgBox.Text;
            _settings.BackgroundOpacity = opacityBox.Value / 100.0;
            _settings.Backdrop = backdropBox.SelectedItem as string ?? _settings.Backdrop;
            ApplyBackdrop();
            ApplySettings();
            _settings.Save();
        }
        void Close()
        {
            Apply();
            RootGrid.Children.Remove(overlay);
            _settingsOverlay = null;
            FocusTerminal();
        }
        fontBox.ValueChanged += (_, _) => Apply();
        fontList.SelectionChanged += (_, _) => Apply();
        applyAppBox.Checked += (_, _) => Apply();
        applyAppBox.Unchecked += (_, _) => Apply();
        ligBox.Toggled += (_, _) => Apply();
        cursorBox.SelectionChanged += (_, _) => Apply();
        blinkBox.Toggled += (_, _) => Apply();
        backdropBox.SelectionChanged += (_, _) => Apply();
        opacityBox.ValueChanged += (_, _) => Apply();
        bgBox.LostFocus += (_, _) => Apply();
        shellBox.LostFocus += (_, _) => Apply();
        shellIntBox.Toggled += (_, _) => Apply();
        agentCmdBox.LostFocus += (_, _) => Apply();
        defAgentBox.LostFocus += (_, _) => Apply();
        recoveryBox.Toggled += (_, _) => Apply();
        agentChatBox.Toggled += (_, _) => Apply();
        rerunBox.SelectionChanged += (_, _) => Apply();
        whitelistBox.LostFocus += (_, _) => Apply();

        closeBtn.Click += (_, _) => Close();
        // Click on the dim scrim (not the card) closes.
        overlay.Tapped += (_, args) =>
        {
            if (args.OriginalSource == overlay)
                Close();
        };
        // Esc/Enter close from anywhere inside the card.
        foreach (var k in new[] { Windows.System.VirtualKey.Escape, Windows.System.VirtualKey.Enter })
        {
            var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = k };
            acc.Invoked += (_, args) =>
            {
                args.Handled = true;
                Close();
            };
            card.KeyboardAccelerators.Add(acc);
        }

        _settingsOverlay = overlay;
        RootGrid.Children.Add(overlay);
        navSearch.Focus(FocusState.Programmatic);
    }

    // ---- Rust -> C# callbacks --------------------------------------------
    //
    // The Rust core invokes these from its wakeup-window proc (UI thread). We hop
    // through the DispatcherQueue to mutate XAML state safely. `ctx` is the
    // GCHandle to this window.

    private unsafe Native.VeloCallbacks BuildCallbacks() => new()
    {
        Ctx = GCHandle.ToIntPtr(_selfHandle),
        OnTitleChanged = (IntPtr)(delegate* unmanaged<IntPtr, uint, ushort*, nuint, void>)&OnTitleChanged,
        OnTabClosed = (IntPtr)(delegate* unmanaged<IntPtr, uint, void>)&OnTabClosed,
        OnActiveChanged = (IntPtr)(delegate* unmanaged<IntPtr, uint, void>)&OnActiveChanged,
        OnNewTabRequested = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnNewTabRequested,
        OnCloseTabRequested = (IntPtr)(delegate* unmanaged<IntPtr, uint, void>)&OnCloseTabRequested,
        OnSwitchTabRequested = (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnSwitchTabRequested,
        OnCwdChanged = (IntPtr)(delegate* unmanaged<IntPtr, uint, ushort*, nuint, void>)&OnCwdChanged,
        OnCommand = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte, int, ulong, ushort*, nuint, void>)&OnCommand,
        OnAnim = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnAnim,
        OnEditorDirty = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte, void>)&OnEditorDirty,
        OnLinkHover = (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnLinkHover,
        OnOpenLink = (IntPtr)(delegate* unmanaged<IntPtr, ushort*, nuint, void>)&OnOpenLink,
    };

    private static MainWindow? FromCtx(IntPtr ctx)
        => ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(ctx).Target as MainWindow;

    [UnmanagedCallersOnly]
    private static void OnEditorDirty(IntPtr ctx, uint fileId, byte dirty)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.OnEditorDirtyUi(fileId, dirty != 0));
    }

    [UnmanagedCallersOnly]
    private static void OnAnim(IntPtr ctx)
    {
        // Fired on the UI thread (subclass proc) whenever a render leaves a
        // scroll glide in flight; StartAnimPump is idempotent.
        FromCtx(ctx)?.StartAnimPump();
    }

    [UnmanagedCallersOnly]
    private static void OnLinkHover(IntPtr ctx, byte over)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.SetPaneCursor(over != 0));
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnOpenLink(IntPtr ctx, ushort* text, nuint len)
    {
        string target = new string((char*)text, 0, (int)len);
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.OpenLink(target));
    }

    /// Reflection workaround: `UIElement.ProtectedCursor` is `protected`, so an
    /// arbitrary element outside a UIElement subclass can't set it directly —
    /// there is no public WinUI 3 API for per-element pointer cursors.
    private static readonly System.Reflection.PropertyInfo? s_protectedCursorProp =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// Set the hand/I-beam cursor over the pane area (PaneHost is the actual
    /// pointer hit-test target; the SwapChainPanels themselves are not
    /// hit-test-visible, see PaneHost_PointerMoved).
    private void SetPaneCursor(bool hand)
    {
        try
        {
            var cursor = InputSystemCursor.Create(hand ? InputSystemCursorShape.Hand : InputSystemCursorShape.IBeam);
            s_protectedCursorProp?.SetValue(PaneHost, cursor);
        }
        catch (Exception ex) { Log.Write($"SetPaneCursor failed: {ex.Message}"); }
    }

    /// Open a link target reported by the core. http/https URLs go to the
    /// default browser; everything else (bare paths, and `file://` URIs with
    /// their scheme stripped) is a path — never handed to ShellExecute
    /// directly, since a malicious `file://…/evil.exe` OSC 8 hyperlink must
    /// not be able to execute on click. Paths are converted from a possible
    /// WSL form and revealed in Explorer (reusing InfoReveal_Click's
    /// ToWindowsPath helper).
    private void OpenLink(string target)
    {
        try
        {
            if (target.StartsWith("http://", StringComparison.Ordinal)
                || target.StartsWith("https://", StringComparison.Ordinal))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            if (target.StartsWith("file://", StringComparison.Ordinal))
            {
                // Uri.LocalPath percent-decodes and handles UNC (file://host/share/x)
                // as well as local (file:///mnt/d/x) forms.
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                    return;
                target = uri.LocalPath;
            }
            var win = ToWindowsPath(target);
            if (win is null)
                return;
            if (Directory.Exists(win))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{win}\"") { UseShellExecute = true });
            else if (File.Exists(win))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{win}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"open link failed: {ex.Message}"); }
    }

    private bool _animPumping;
    private long _animLastTicks;

    /// Drive velo_tick from the compositor while a scroll glide is live.
    private void StartAnimPump()
    {
        if (_animPumping)
            return;
        _animPumping = true;
        _animLastTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += AnimPump_Rendering;
    }

    private void AnimPump_Rendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        float dtMs = (float)((now - _animLastTicks) * 1000.0 / Stopwatch.Frequency);
        _animLastTicks = now;
        if (Native.velo_tick(_engine, dtMs) == 0)
        {
            CompositionTarget.Rendering -= AnimPump_Rendering;
            _animPumping = false;
        }
    }

    // ---- Editor mode -------------------------------------------------------

    public ObservableCollection<EditorFileVM> EditorFiles { get; } = new();
    private bool _editorMode;
    private bool _editorAttached;
    private bool _editorMouseDown;
    private bool _suppressEditorTab;
    private DispatcherTimer? _autosave;

    /// Files button toggles editor mode; file clicks in the tree open here.
    private void SetEditorMode(bool on)
    {
        if (_editorMode == on) return;
        _editorMode = on;
        if (on && !_editorAttached)
            AttachEditorPane();
        if (!on && _engine != IntPtr.Zero)
        {
            Native.velo_editor_save_all(_engine);   // flush on exit
            // Close the bottom terminal via its own path: it reflows the shared
            // session back to the main pane's grid (see ToggleEditorTerminal).
            if (EditorTermHost.Visibility == Visibility.Visible)
                ToggleEditorTerminal();
        }
        PaneHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EditorHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditorPill();
        if (on) EditorPanel.Focus(FocusState.Programmatic);
        else FocusTerminal();
    }

    private void EditorPill_Click(object sender, RoutedEventArgs e) => SetEditorMode(true);

    /// Sidebar "Editor" pill: visible while any file is open, filled while editor
    /// mode is active — the way back after a terminal tab click left editor mode.
    private void UpdateEditorPill()
    {
        EditorPillButton.Visibility =
            EditorFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorPillButton.Background = _editorMode
            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private unsafe void AttachEditorPane()
    {
        if (_engine == IntPtr.Zero) return;
        IntPtr sc = IntPtr.Zero;
        uint id = Native.velo_editor_attach(_engine, &sc);
        if (id == uint.MaxValue || sc == IntPtr.Zero)
        {
            Log.Write("AttachEditorPane: velo_editor_attach failed");
            return;
        }
        EditorPanel.As<ISwapChainPanelNative>().SetSwapChain(sc);
        _editorAttached = true;
        PushEditorSize();
    }

    private void EditorPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => PushEditorSize();

    private void PushEditorSize()
    {
        if (!_editorAttached || _engine == IntPtr.Zero) return;
        if (DeferResize())
        {
            _pendingEditorResize = true;
            return;
        }
        PushEditorSizeNow();
    }

    private void PushEditorSizeNow()
    {
        if (!_editorAttached || _engine == IntPtr.Zero) return;
        double sx = EditorPanel.CompositionScaleX > 0 ? EditorPanel.CompositionScaleX : _lastScale;
        double sy = EditorPanel.CompositionScaleY > 0 ? EditorPanel.CompositionScaleY : _lastScale;
        uint w = (uint)Math.Max(1, Math.Round(EditorPanel.ActualWidth * sx));
        uint h = (uint)Math.Max(1, Math.Round(EditorPanel.ActualHeight * sy));
        Native.velo_editor_resize(_engine, w, h);
    }

    /// Open (or refocus) a file in the editor; enters editor mode.
    private unsafe void OpenInEditor(string path)
    {
        if (_engine == IntPtr.Zero) return;
        SetEditorMode(true);
        long id;
        fixed (char* p = path)
            id = Native.velo_editor_open(_engine, (ushort*)p, (nuint)path.Length);
        if (id < 0) { ShellOpen(path); return; } // binary/unreadable: OS fallback
        var fid = (uint)id;
        var vm = EditorFiles.FirstOrDefault(f => f.Id == fid);
        if (vm is null)
        {
            vm = new EditorFileVM { Id = fid, Path = path };
            EditorFiles.Add(vm);
            UpdateEditorPill();
        }
        _suppressEditorTab = true;
        EditorTabs.SelectedItem = vm;
        _suppressEditorTab = false;
        EditorPanel.Focus(FocusState.Programmatic);
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Pill visual tracks selection even when programmatic (suppressed).
        foreach (var r in e.RemovedItems) if (r is EditorFileVM rv) rv.IsSelected = false;
        foreach (var a in e.AddedItems) if (a is EditorFileVM av) av.IsSelected = true;
        if (_suppressEditorTab || _engine == IntPtr.Zero) return;
        if (EditorTabs.SelectedItem is EditorFileVM vm)
        {
            Native.velo_editor_save_all(_engine); // flush on tab switch
            Native.velo_editor_focus_file(_engine, vm.Id);
            EditorPanel.Focus(FocusState.Programmatic);
        }
    }

    private void EditorTabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not uint id) return;
        CloseEditorFile(id);
    }

    private void CloseEditorFile(uint id)
    {
        Native.velo_editor_close_file(_engine, id);
        var vm = EditorFiles.FirstOrDefault(f => f.Id == id);
        if (vm is not null) EditorFiles.Remove(vm);
        UpdateEditorPill();
        if (EditorFiles.Count == 0) SetEditorMode(false);
        else if (EditorTabs.SelectedItem is null)
            EditorTabs.SelectedIndex = EditorFiles.Count - 1;
    }

    private void EditorPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero) return;
        // Ctrl+W closes the focused file (editor mode only).
        if (e.Key == Windows.System.VirtualKey.W && (Modifiers() & 1) != 0)
        {
            if (EditorTabs.SelectedItem is EditorFileVM cur) CloseEditorFile(cur.Id);
            e.Handled = true;
            return;
        }
        // Ctrl+J toggles the bottom terminal panel.
        if (e.Key == Windows.System.VirtualKey.J && (Modifiers() & 1) != 0)
        {
            ToggleEditorTerminal();
            e.Handled = true;
            return;
        }
        if (Native.velo_editor_key(_engine, (uint)e.Key, Modifiers()) != 0)
            e.Handled = true;
    }

    // ---- Editor bottom terminal (Ctrl+J) -----------------------------------

    private uint _editorTermPane = InvalidId;

    /// Show/hide the bottom terminal in editor mode. Lazily creates a core
    /// pane bound to the active tab's session (no new shell is spawned).
    private void ToggleEditorTerminal()
    {
        if (_engine == IntPtr.Zero) return;
        bool show = EditorTermHost.Visibility == Visibility.Collapsed;
        if (show && _editorTermPane == InvalidId)
        {
            IntPtr swap;
            uint pid;
            unsafe
            {
                IntPtr s;
                pid = Native.velo_pane_new(_engine, &s);
                swap = s;
            }
            if (pid == InvalidId || swap == IntPtr.Zero)
            {
                Log.Write("EditorTerm: velo_pane_new failed");
                return;
            }
            _editorTermPane = pid;
            EditorTermPanel.Tag = pid;
            EditorTermHost.Tag = pid;
            EditorTermPanel.As<ISwapChainPanelNative>().SetSwapChain(swap);
        }
        if (show)
        {
            EditorTermHost.Visibility = Visibility.Visible;
            // (Re)bind to the active tab every show — it may have changed.
            if (CurrentTab() is TabVM t)
                Native.velo_pane_bind(_engine, _editorTermPane, t.Id);
            PushPaneSize(EditorTermPanel);
            Native.velo_pane_focus(_engine, _editorTermPane);
            EditorTermHost.Focus(FocusState.Programmatic);
        }
        else
        {
            EditorTermHost.Visibility = Visibility.Collapsed;
            // The bind above reflowed the SESSION (shared with the main pane) to
            // this small grid. Reflow it back to the main pane's grid, or leaving
            // editor mode shows a 12-row terminal squashed into a full-size pane.
            if (CurrentTab() is TabVM cur)
                Native.velo_pane_bind(_engine, MainPaneFor(cur.Id), cur.Id);
            Native.velo_pane_focus(_engine, 0);
            EditorPanel.Focus(FocusState.Programmatic);
        }
    }

    /// Core pane id of the split slot showing tab `tabId` (pane 0 if none does).
    private uint MainPaneFor(uint tabId)
    {
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] == tabId && _slotCore[i] != InvalidId)
                return _slotCore[i];
        return 0;
    }

    private void EditorTerm_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.J && (Modifiers() & 1) != 0)
        {
            ToggleEditorTerminal();
            e.Handled = true;
            return;
        }
        Panel_KeyDown(sender, e); // Tag carries the pane id
    }

    private void EditorTerm_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero || _editorTermPane == InvalidId) return;
        EditorTermHost.Focus(FocusState.Pointer);
        Native.velo_pane_focus(_engine, _editorTermPane);
        ForwardMouse(EditorTermPanel, 0, e);
        e.Handled = true;
    }

    private void EditorTerm_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero || _editorTermPane == InvalidId) return;
        int delta = e.GetCurrentPoint(EditorTermPanel).Properties.MouseWheelDelta;
        int lines = delta * 3 / 120;
        if (lines == 0) return;
        double sx = EditorTermPanel.CompositionScaleX > 0 ? EditorTermPanel.CompositionScaleX : _lastScale;
        double sy = EditorTermPanel.CompositionScaleY > 0 ? EditorTermPanel.CompositionScaleY : _lastScale;
        var p = e.GetCurrentPoint(EditorTermPanel).Position;
        Native.velo_pane_scroll(_engine, _editorTermPane, lines,
            (float)(p.X * sx), (float)(p.Y * sy), Modifiers());
        e.Handled = true;
    }

    private void EditorTerm_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_editorTermPane != InvalidId) PushPaneSize(EditorTermPanel);
    }

    // ---- Sidebar Agent panel ----------------------------------------------
    // Native chat over headless agent CLIs: each send spawns the picked agent
    // non-interactively in the focused tab's cwd (prompt on stdin), so the
    // panel always follows the terminal's directory. Claude resumes its
    // per-directory session via --continue; other agents are stateless.
    // ponytail: whole-response append, no streaming; add stream-json when needed.

    private Process? _agentProc;
    private bool _agentContinue;
    private string _agentCwd = "";
    /// Prompt currently being processed (null = idle). Saved at close so the
    /// question survives the app exiting mid-answer.
    private string? _agentPendingPrompt;

    private AgentProfile? _agentSel;

    /// Fill the picker flyout once and keep the cwd label current.
    private async Task RefreshAgentPanelAsync()
    {
        AgentCwd.Text = CurrentCwd();
        if (AgentPick.Flyout is not null)
        {
            _ = UpdateAgentGreetingAsync();   // time of day may have moved on
            return;
        }
        var profiles = new List<AgentProfile>(await AgentProfiles.AllAsync());
        if (!string.IsNullOrWhiteSpace(_settings.CustomAgentCommand))
            profiles.Add(new AgentProfile("Custom", _settings.CustomAgentCommand, null, "", null));
        if (profiles.Count == 0)
        {
            AgentPick.Content = "No agents found";
            return;
        }
        var flyout = new MenuFlyout();
        foreach (var prof in profiles)
        {
            var p = prof;
            var item = new MenuFlyoutItem { Text = p.Name };
            item.Click += (_, _) =>
            {
                _agentSel = p;
                AgentPick.Content = p.Name;
                _agentContinue = false; // a different agent knows nothing of the last session
                _ = UpdateAgentGreetingAsync();
            };
            flyout.Items.Add(item);
        }
        // Restored chat wins over the configured default (it continues a session).
        var def = profiles.Find(p => p.Name == _restored.AgentName)
               ?? profiles.Find(p => p.Name == _settings.DefaultAgent)
               ?? profiles[0];
        _agentSel = def;
        AgentPick.Content = def.Name;
        AgentPick.Flyout = flyout;
        if (_restored.AgentChat.Count > 0 && _settings.RestoreAgentChat)
        {
            foreach (var m in _restored.AgentChat)
                AppendAgentMsg(m.Text, m.User);
            _agentContinue = _restored.AgentContinue && def.Name == _restored.AgentName;
            _restored.AgentChat.Clear();
            // A question was mid-flight at close: re-ask it (--continue agents
            // resume their session; the user bubble is already in the history).
            if (_restored.AgentPending.Length > 0 && def.Name == _restored.AgentName)
            {
                var pending = _restored.AgentPending;
                _restored.AgentPending = "";
                _ = AgentSendAsync(pending);
            }
        }
        _ = UpdateAgentGreetingAsync();
    }

    /// Empty-chat hero: time-of-day greeting + the user the agent runs as
    /// (Windows username, or the WSL distro's user for distro agents).
    private async Task UpdateAgentGreetingAsync()
    {
        bool empty = AgentChat.Children.Count == 0;
        AgentGreeting.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
            return;
        int h = DateTime.Now.Hour;
        var greet = h < 5 ? "Good night" : h < 12 ? "Good morning"
                  : h < 17 ? "Good afternoon" : h < 21 ? "Good evening" : "Good night";
        var name = _agentSel?.Distro is string d ? await WslUserAsync(d) : Environment.UserName;
        AgentGreetText.Text = $"{greet}, {name}";
    }

    /// Example-prompt chip under the greeting: insert its text into the input.
    private void AgentExample_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string text)
        {
            AgentInput.Text = text;
            AgentInput.Focus(FocusState.Programmatic);
            AgentInput.SelectionStart = text.Length;
        }
    }

    private static readonly Dictionary<string, string> _wslUserCache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<string> WslUserAsync(string distro)
    {
        if (_wslUserCache.TryGetValue(distro, out var hit))
            return hit;
        var user = distro;
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("whoami");
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var raw = (await p.StandardOutput.ReadToEndAsync()).Trim().Trim('\0');
                await p.WaitForExitAsync();
                if (raw.Length > 0)
                    user = raw;
            }
        }
        catch { /* distro unreachable: fall back to its name */ }
        _wslUserCache[distro] = user;
        return user;
    }

    /// + button: wipe the chat and start a fresh session.
    private void AgentNew_Click(object sender, RoutedEventArgs e)
    {
        try { _agentProc?.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
        HideAgentThinking();
        AgentChat.Children.Clear();
        _agentHistory.Clear();
        _agentContinue = false;
        AgentScrollDown.Visibility = Visibility.Collapsed;
        _ = UpdateAgentGreetingAsync();
        AgentInput.Focus(FocusState.Programmatic);
    }

    private void AgentScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => AgentScrollDown.Visibility =
            AgentScroll.VerticalOffset < AgentScroll.ScrollableHeight - 40
                ? Visibility.Visible : Visibility.Collapsed;

    private void AgentScrollDown_Click(object sender, RoutedEventArgs e)
        => AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null);

    private void AgentInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsDown(VirtualKey.Shift))
        {
            e.Handled = true;
            _ = AgentSendAsync();
        }
    }

    /// `resend` re-asks a restored pending question: the user bubble is already
    /// in the replayed history, so it is not appended again.
    private async Task AgentSendAsync(string? resend = null)
    {
        var prompt = resend ?? AgentInput.Text.Trim();
        if (prompt.Length == 0 || _agentProc is not null || _agentSel is not AgentProfile p)
            return;
        var cwd = CurrentCwd();
        if (cwd != _agentCwd)
        {
            _agentCwd = cwd;
            _agentContinue = false; // agent sessions are per-directory
        }
        AgentCwd.Text = cwd;
        AgentGreeting.Visibility = Visibility.Collapsed;
        if (resend is null)
        {
            AgentInput.Text = "";
            AppendAgentMsg(prompt, user: true);
        }
        _agentPendingPrompt = prompt;
        ShowAgentThinking();
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = AgentPsi(p, _agentContinue, cwd);
            using var proc = Process.Start(psi)!;
            _agentProc = proc;
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();
            var outT = proc.StandardOutput.ReadToEndAsync();
            var errT = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var text = (await outT).Trim();
            if (text.Length == 0)
                text = (await errT).Trim();
            if (p.Exe == "ollama")   // reasoning models print their chain inline
                text = System.Text.RegularExpressions.Regex
                    .Replace(text, @"(?is)^\s*thinking(\.{3}|…).*?done thinking\.?", "").Trim();
            HideAgentThinking();
            AppendAgentMsg(text.Length > 0 ? text : $"(no output, exit {proc.ExitCode})",
                user: false, seconds: sw.Elapsed.TotalSeconds);
            if (proc.ExitCode == 0 && p.ContinueArgs is not null)
                _agentContinue = true;
        }
        catch (Exception ex)
        {
            AppendAgentMsg(ex.Message, user: false);
        }
        finally
        {
            _agentProc = null;
            _agentPendingPrompt = null;
            HideAgentThinking();
        }
    }

    // Three staggered pulsing dots appended under the last user message.
    private StackPanel? _agentThinking;

    private void ShowAgentThinking()
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(2, 2, 0, 0),
        };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Opacity = 0.25,
            };
            var anim = new DoubleAnimation
            {
                From = 0.25,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 150),
            };
            Storyboard.SetTarget(anim, dot);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sp.Children.Add(dot);
            sb.Begin();
        }
        _agentThinking = sp;
        AgentChat.Children.Add(sp);
        AgentScroll.UpdateLayout();
        AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null, true);
    }

    private void HideAgentThinking()
    {
        if (_agentThinking is null)
            return;
        AgentChat.Children.Remove(_agentThinking);
        _agentThinking = null;
    }

    /// Headless invocation: WSL agents run via `wsl --cd` (accepts Windows
    /// paths) + a login shell so ~/.local/bin installs resolve; Windows agents
    /// via cmd /c (npm shims are .cmd files, not directly spawnable).
    private static ProcessStartInfo AgentPsi(AgentProfile p, bool cont, string cwd)
    {
        var args = cont && p.ContinueArgs is not null ? p.ContinueArgs : p.Args;
        var cmd = $"{p.Exe} {args}".Trim();
        ProcessStartInfo psi;
        if (p.Distro is string d)
        {
            psi = new ProcessStartInfo("wsl.exe");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(d);
            psi.ArgumentList.Add("--cd");
            psi.ArgumentList.Add(cwd);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(p.Shell);
            psi.ArgumentList.Add("-lic"); // match the probe: rc files set the PATH
            psi.ArgumentList.Add(cmd);
        }
        else
        {
            psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = cwd };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cmd);
        }
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardInputEncoding = Encoding.UTF8;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    /// Chat transcript mirror, persisted in session.json across app restarts.
    private readonly List<(string Text, bool User)> _agentHistory = new();

    /// User prompts get a subtle pill; agent replies are plain selectable text
    /// with an elapsed-time caption underneath.
    private void AppendAgentMsg(string text, bool user, double? seconds = null)
    {
        _agentHistory.Add((text, user));
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (user)
        {
            AgentChat.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Child = tb,
            });
        }
        else
        {
            var reply = new StackPanel { Spacing = 6, Margin = new Thickness(2, 0, 0, 0) };
            reply.Children.Add(tb);
            if (seconds is double s)
                reply.Children.Add(new TextBlock
                {
                    Text = s < 60 ? $"{s:0.0}s" : $"{(int)(s / 60)}m {s % 60:0}s",
                    FontSize = 11,
                    Opacity = 0.5,
                });
            AgentChat.Children.Add(reply);
        }
        AgentScroll.UpdateLayout();
        AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null, true);
    }

    private void EditorPanel_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_engine == IntPtr.Zero) return;
        Native.velo_editor_char(_engine, args.Character);
        args.Handled = true;
    }

    private void EditorPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero) return;
        int delta = e.GetCurrentPoint(EditorPanel).Properties.MouseWheelDelta;
        Native.velo_editor_scroll(_engine, -delta * 3 / 120);
        e.Handled = true;
    }

    private void EditorPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero) return;
        var (x, y) = EditorPhysicalPoint(e);
        _editorMouseDown = true;
        EditorPanel.CapturePointer(e.Pointer);
        Native.velo_editor_mouse(_engine, 0, x, y, Modifiers());
        EditorPanel.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void EditorPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_editorMouseDown || _engine == IntPtr.Zero) return;
        var (x, y) = EditorPhysicalPoint(e);
        Native.velo_editor_mouse(_engine, 1, x, y, Modifiers());
    }

    private void EditorPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _editorMouseDown = false;
        EditorPanel.ReleasePointerCaptures();
        if (_engine == IntPtr.Zero) return;
        var (x, y) = EditorPhysicalPoint(e);
        Native.velo_editor_mouse(_engine, 2, x, y, Modifiers());
    }

    private (float, float) EditorPhysicalPoint(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(EditorPanel).Position;
        double sx = EditorPanel.CompositionScaleX > 0 ? EditorPanel.CompositionScaleX : _lastScale;
        double sy = EditorPanel.CompositionScaleY > 0 ? EditorPanel.CompositionScaleY : _lastScale;
        return ((float)(pt.X * sx), (float)(pt.Y * sy));
    }

    /// Dirty flip from Rust: update the tab dot + restart the 1s autosave timer.
    private void OnEditorDirtyUi(uint fileId, bool dirty)
    {
        var vm = EditorFiles.FirstOrDefault(f => f.Id == fileId);
        if (vm is not null) vm.Dirty = dirty;
        if (!dirty) return;
        if (_autosave is null)
        {
            _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autosave.Tick += (_, _) =>
            {
                _autosave!.Stop();
                if (_engine != IntPtr.Zero)
                    Native.velo_editor_save_all(_engine); // Rust fires dirty=false back
            };
        }
        _autosave.Stop();
        _autosave.Start();
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnTitleChanged(IntPtr ctx, uint id, ushort* utf16, nuint len)
    {
        var w = FromCtx(ctx);
        if (w is null)
            return;
        string title = new string((char*)utf16, 0, (int)len);
        w.DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var t in w.Tabs)
            {
                if (t.Id == id)
                {
                    t.Title = title;
                    break;
                }
            }
            w.UpdateTitleBarTitle();
        });
    }

    /// Mirror the active tab's title into the window (taskbar / alt-tab) title.
    private void UpdateTitleBarTitle()
        => Title = CurrentTab()?.Title ?? "Velo";

    [UnmanagedCallersOnly]
    private static void OnTabClosed(IntPtr ctx, uint id)
    {
        Log.Write($"callback OnTabClosed: id={id}");
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() =>
        {
            // Core already freed the session (EOF path); just re-pack panes + list.
            w.HandleTabGone(id);
            Log.Write($"OnTabClosed (UI): removed id={id}, remaining={w.Tabs.Count}");
        });
    }

    [UnmanagedCallersOnly]
    private static void OnActiveChanged(IntPtr ctx, uint id)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.SelectById(id));
    }

    [UnmanagedCallersOnly]
    private static void OnNewTabRequested(IntPtr ctx)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.AddTab());
    }

    [UnmanagedCallersOnly]
    private static void OnCloseTabRequested(IntPtr ctx, uint id)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.CloseTab(id));
    }

    [UnmanagedCallersOnly]
    private static void OnSwitchTabRequested(IntPtr ctx, byte forward)
    {
        var w = FromCtx(ctx);
        w?.DispatcherQueue.TryEnqueue(() => w.SwitchSelection(forward != 0));
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnCwdChanged(IntPtr ctx, uint id, ushort* utf16, nuint len)
    {
        var w = FromCtx(ctx);
        if (w is null)
            return;
        string cwd = new string((char*)utf16, 0, (int)len);
        w.DispatcherQueue.TryEnqueue(() =>
        {
            Log.Write($"OnCwdChanged: tab={id} cwd='{cwd}'");
            foreach (var t in w.Tabs)
                if (t.Id == id) { t.Cwd = cwd; break; }
            if (w._detailsOpen && w.CurrentTab()?.Id == id)
            {
                w._filesDir = null;   // follow the new cwd
                w.RefreshDetails();
            }
        });
    }

    // phase: 0 = prompt, 1 = command start (text), 2 = command end (exit + dur).
    [UnmanagedCallersOnly]
    private static unsafe void OnCommand(IntPtr ctx, uint id, byte phase, int exit, ulong durMs, ushort* utf16, nuint len)
    {
        var w = FromCtx(ctx);
        if (w is null)
            return;
        string text = len > 0 ? new string((char*)utf16, 0, (int)len) : "";
        w.DispatcherQueue.TryEnqueue(() =>
        {
            TabVM? tab = null;
            foreach (var t in w.Tabs)
                if (t.Id == id) { tab = t; break; }
            if (tab is null)
                return;
            switch (phase)
            {
                case 1:
                    tab.RunningCommand = text;
                    break;
                case 2:
                    var cmd = tab.RunningCommand;
                    if (cmd.Length > 0)
                        tab.AddCommand(new CommandEntry { Command = cmd, Exit = exit, DurMs = (long)durMs, When = DateTime.Now });
                    tab.RunningCommand = "";
                    Log.Write($"OnCommand: tab={id} cmd='{cmd}' exit={exit} dur={durMs}ms");
                    if (w._detailsOpen && w.CurrentTab()?.Id == id
                        && (w._activeDetailsTab == "Outline" || w._activeDetailsTab == "Git"))
                        w.RefreshDetails();
                    break;
            }
        });
    }
}
