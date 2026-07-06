using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private int _titleHover;            // # of chrome regions currently hovered
    private readonly Settings _settings = Settings.Load();

    // Manual title-bar drag: the whole bar is a passthrough hover strip (so it
    // reveals the toggles everywhere), so dragging is started by hand here.
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Split view (up to 4 panes in a 2x2 grid) ----
    private const uint InvalidId = uint.MaxValue;
    private const int MaxPanes = 4;
    private SwapChainPanel[] _panels = System.Array.Empty<SwapChainPanel>(); // set in Loaded
    private readonly uint[] _slotCore = { 0, InvalidId, InvalidId, InvalidId };   // core pane id per slot
    private readonly uint[] _slotTab = { InvalidId, InvalidId, InvalidId, InvalidId }; // tab shown per slot
    private readonly IntPtr[] _slotSwap = new IntPtr[MaxPanes];  // pending swapchain to bind once visible
    private int _paneCount = 1;          // active panes
    private bool _splitVertical;         // 2-pane orientation (true = top/bottom)
    private int _focusedPane;            // slot with focus
    private TabVM? _dragTab;             // tab being dragged from the list
    private enum Edge { Left, Right, Top, Bottom }

    private OverlappedPresenter? _presenter;
    private InputNonClientPointerSource? _nonClient;

    public MainWindow()
    {
        Log.Write("ctor: enter [build:geom-unparent+simple-backdrop]");
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

        // Frameless: drop the system title bar (keep the resize border), then
        // extend our content into the whole window.
        _presenter = AppWindow.Presenter as OverlappedPresenter;
        _presenter?.SetBorderAndTitleBar(true, false);
        ExtendsContentIntoTitleBar = true;
        Log.Write("ctor: titlebar done, calling ApplyBackdrop");
        ApplyBackdrop();
        Log.Write("ctor: ApplyBackdrop done");

        _nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        AppWindow.Changed += (_, args) => { if (args.DidPresenterChange || args.DidSizeChange) UpdateMaxGlyph(); };

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
            AddTab();
            FocusTerminal();
            Log.Write("Loaded: complete");
        }
        catch (Exception ex)
        {
            Log.Ex("TerminalPanel_Loaded", ex);
            throw;
        }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        Log.Write($"OnClosed: window closing. Tabs={Tabs.Count}\n{Environment.StackTrace}");
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

    /// Push a panel's physical pixel size to its core pane (resize + reflow).
    private void PushPaneSize(SwapChainPanel panel)
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

    /// Core pane id stored in each SwapChainPanel's Tag (defaults to pane 0).
    private static uint PaneId(object panel)
        => panel is FrameworkElement fe && fe.Tag is uint id ? id : 0u;

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

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDragRegions();

    /// Make the whole top strip Passthrough (client) so XAML — not the OS caption —
    /// owns it: that lets TitleBarHover reveal the toggles on hover anywhere along
    /// the bar and start window drag by hand (see TitleBarHover_PointerPressed).
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
        _sidebarOpen = open;
        AnimateWidth(ToggleBar, open ? BarOpen : BarClosed);
        // Animate the column width instead of an instant jump: a 280px one-frame jump
        // exposes a misaligned strip at the terminal↔sidebar seam (the swapchain's
        // DComp present lags the XAML column commit by a frame → the color flash). At
        // ~a few px per frame that desync is sub-pixel and reads as motion, not a flash.
        AnimateWidth(SidebarSurface, open ? SidebarWidth : 0);
        FocusTerminal();
    }

    /// Animate a FrameworkElement.Width (a layout/dependent property, hence
    /// EnableDependentAnimation). Drives both the panel columns and the toggle glyph.
    private static void AnimateWidth(FrameworkElement el, double to)
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
        Log.Write($"SetDetails: open={open} tab={_activeDetailsTab} realized={_detailsRealized}");
        _detailsOpen = open;
        AnimateWidth(DetailsToggleBar, open ? BarOpen : BarClosed);
        AnimateWidth(DetailsSurface, open ? SidebarWidth : 0);   // animated, see SetSidebar
        if (open)
            EnsureDetailsRealized();   // build + fill the panels on first open
        FocusTerminal();
    }

    private void DetailsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            SelectDetailsTab(b);
        if (!_detailsOpen)
            SetDetails(true);
        // Files button doubles as the editor-mode toggle: first click opens the
        // tree + editor surface, clicking again returns to the terminal.
        if (ReferenceEquals(sender, FilesTabButton))
        {
            SetEditorMode(!_editorMode);
            return; // SetEditorMode owns focus either way
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
        };
        foreach (var (btn, label) in labels)
        {
            btn.Background = btn == selected ? pill : null;
            label.Visibility = btn == selected ? Visibility.Visible : Visibility.Collapsed;
        }
        if (selected.Tag is string tag)
            _activeDetailsTab = tag;
        // Panels live under x:Load="False" — only touch them once realized
        // (i.e. after the panel has been opened at least once).
        if (_detailsRealized)
        {
            ApplyDetailsTab();
            RefreshDetails();
        }
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
        var win = ToWindowsPath(CurrentTab()?.Cwd) ?? Environment.CurrentDirectory;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{win}\"") { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    /// Best-effort Windows path for a shell cwd: maps WSL /mnt/&lt;drive&gt;/… to
    /// &lt;DRIVE&gt;:\…; leaves an already-Windows path untouched.
    private static string? ToWindowsPath(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
            return null;
        if (cwd.Length >= 7 && cwd[0] == '/' && cwd.StartsWith("/mnt/", StringComparison.Ordinal)
            && char.IsLetter(cwd[5]) && cwd[6] == '/')
        {
            var rest = cwd.Substring(7).Replace('/', '\\');
            return $"{char.ToUpperInvariant(cwd[5])}:\\{rest}";
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

    // Toggles are hidden at rest; hovering anywhere on the title bar (or any chrome
    // region in it) fades BOTH toggles in. Reference-counted so moving between the
    // hover strip and a button doesn't flicker them off.
    private void Chrome_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _titleHover++;
        ShowToggles(true);
    }

    private void Chrome_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _titleHover = Math.Max(0, _titleHover - 1);
        if (_titleHover == 0)
            ShowToggles(false);
    }

    private void ShowToggles(bool visible)
    {
        AnimateOpacity(ToggleHost, visible ? 1 : 0);
        AnimateOpacity(ToggleHostLeft, visible ? 1 : 0);
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

    private static void AnimateOpacity(FrameworkElement el, double to)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

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

        // Route input to the pane that actually holds XAML focus (core no-ops if
        // already focused, so this just keeps the two in sync for splits).
        Native.velo_pane_focus(_engine, PaneId(sender));

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
        Native.velo_pane_focus(_engine, PaneId(sender)); // route to the focused pane
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
        int i = PaneAt(e.GetCurrentPoint(PaneHost).Position);
        if (i < 0)
            return;
        _focusedPane = i;
        PaneHost.Focus(FocusState.Pointer);   // SwapChainPanel can't take focus
        Native.velo_pane_focus(_engine, PaneId(_panels[i]));
        _mousePane = i;
        PaneHost.CapturePointer(e.Pointer);
        ForwardMouse(_panels[i], 0, e);
    }

    private void PaneHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        int i = _mousePane >= 0 ? _mousePane : PaneAt(e.GetCurrentPoint(PaneHost).Position);
        if (i < 0)
            return;
        ForwardMouse(_panels[i], 1, e);
    }

    private void PaneHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
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
        var vm = new TabVM(id, profile?.Name ?? "PowerShell");
        ApplyShellKind(vm, profile?.Command ?? _settings.Shell);
        return vm;
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
            return;
        }
        if (command.Contains("cmd", StringComparison.OrdinalIgnoreCase))
        {
            vm.ShellKind = "cmd";
            return;
        }
        var dIdx = command.IndexOf("-d ", StringComparison.OrdinalIgnoreCase);
        if (dIdx < 0)
            return;
        var distro = command[(dIdx + 3)..].Trim();
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
            else if (child is TextBlock { Name: "ShellKindText" } tb) tb.Opacity = (1 - opacity) * 0.5;
        }
    }

    private void CloseTab(uint id)
    {
        if (_engine == IntPtr.Zero)
            return;
        Native.velo_tab_close(_engine, id);   // core frees the session + unbinds panes
        Log.Write($"CloseTab: id={id}, remaining={Tabs.Count - 1}");
        HandleTabGone(id);
    }

    /// Shared cleanup after a tab disappears (user close or shell exit): drop it
    /// from the list and from any pane, then re-pack the remaining panes so only the
    /// closed pane goes away (the others keep their split).
    private void HandleTabGone(uint id)
    {
        RemoveTab(id);
        bool wasInPane = RemovePaneTab(id);
        if (Tabs.Count == 0)
        {
            Close();
            return;
        }
        if (!wasInPane)
            return;
        // Pane 0 must always show a tab; backfill from selection / first tab.
        if (_slotTab[0] == InvalidId)
            _slotTab[0] = (TabList.SelectedItem as TabVM)?.Id ?? Tabs[0].Id;
        ApplyLayout();
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] != InvalidId)
                Native.velo_pane_bind(_engine, _slotCore[i], _slotTab[i]);
        if (_slotTab[_focusedPane] != InvalidId)
            SelectById(_slotTab[_focusedPane]);
    }

    /// Remove `id` from the active pane set and compact the slots below it. Returns
    /// whether it was shown in a pane.
    private bool RemovePaneTab(uint id)
    {
        int k = -1;
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] == id) { k = i; break; }
        if (k < 0)
            return false;
        for (int i = k; i < _paneCount - 1; i++)
            _slotTab[i] = _slotTab[i + 1];
        _slotTab[_paneCount - 1] = InvalidId;
        _paneCount = Math.Max(1, _paneCount - 1);
        if (_focusedPane >= _paneCount)
            _focusedPane = 0;
        return true;
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
            // Picking a terminal tab leaves editor mode (workspace state
            // survives in the engine; re-entering shows the same files).
            SetEditorMode(false);
            // Binds the focused pane to this tab (core), so track it per pane.
            Native.velo_tab_set_active(_engine, t.Id);
            _slotTab[_focusedPane] = t.Id;
            _filesDir = null;            // Files panel follows the new tab's cwd
            if (_detailsOpen) RefreshDetails();
            FocusTerminal();
        }
        else if (TabList.SelectedItem is TabGroup)
        {
            // Group-header rows aren't a tab: bounce selection back to the active tab so
            // the header doesn't show the selected fill.
            _suppressSelection = true;
            TabList.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _suppressSelection = false;
        }
    }

    // ---- Split view (drag a tab onto an edge → up to 4 panes) ------------

    private void TabList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragTab = e.Items.Count > 0 ? e.Items[0] as TabVM : null;
        if (_dragTab is not null)
            e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragTab = null;
        HideDropZone();
    }

    private void PaneHost_DragOver(object sender, DragEventArgs e)
    {
        if (_dragTab is null || _paneCount >= MaxPanes)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        ShowDropZone(EdgeFromPoint(e.GetPosition(PaneHost)));
    }

    private void PaneHost_DragLeave(object sender, DragEventArgs e) => HideDropZone();

    private void PaneHost_Drop(object sender, DragEventArgs e)
    {
        var t = _dragTab;
        _dragTab = null;
        var edge = EdgeFromPoint(e.GetPosition(PaneHost));
        HideDropZone();
        if (t is null)
            return;
        // Defer (Low priority = after the drag's modal loop fully unwinds): the OS
        // drag-drop is still completing on this thread. Doing the swapchain create +
        // blocking GPU resize here wedges the drag's input loop and freezes the whole
        // app.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AddPane(t, edge));
    }

    /// Nearest edge to the pointer (Windows-snap style).
    private Edge EdgeFromPoint(Point p)
    {
        double rx = PaneHost.ActualWidth > 0 ? p.X / PaneHost.ActualWidth : 0.5;
        double ry = PaneHost.ActualHeight > 0 ? p.Y / PaneHost.ActualHeight : 0.5;
        double dl = rx, dr = 1 - rx, dt = ry, db = 1 - ry;
        double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (m == dl) return Edge.Left;
        if (m == dr) return Edge.Right;
        if (m == dt) return Edge.Top;
        return Edge.Bottom;
    }

    private void ShowDropZone(Edge edge)
    {
        SplitOverlay.Visibility = Visibility.Visible;
        DropZone.Opacity = 1;
        double w = PaneHost.ActualWidth, h = PaneHost.ActualHeight;
        switch (edge)
        {
            case Edge.Left:
                DropZone.HorizontalAlignment = HorizontalAlignment.Left;
                DropZone.VerticalAlignment = VerticalAlignment.Stretch;
                DropZone.Width = w / 2; DropZone.Height = double.NaN; break;
            case Edge.Right:
                DropZone.HorizontalAlignment = HorizontalAlignment.Right;
                DropZone.VerticalAlignment = VerticalAlignment.Stretch;
                DropZone.Width = w / 2; DropZone.Height = double.NaN; break;
            case Edge.Top:
                DropZone.HorizontalAlignment = HorizontalAlignment.Stretch;
                DropZone.VerticalAlignment = VerticalAlignment.Top;
                DropZone.Height = h / 2; DropZone.Width = double.NaN; break;
            default:
                DropZone.HorizontalAlignment = HorizontalAlignment.Stretch;
                DropZone.VerticalAlignment = VerticalAlignment.Bottom;
                DropZone.Height = h / 2; DropZone.Width = double.NaN; break;
        }
    }

    private void HideDropZone()
    {
        SplitOverlay.Visibility = Visibility.Collapsed;
        DropZone.Opacity = 0;
    }

    /// Add a pane showing the dropped tab. The first split's orientation comes from
    /// the drop edge; further panes fill the 2x2 grid. Capped at 4 panes.
    private void AddPane(TabVM dropped, Edge edge)
    {
        if (_engine == IntPtr.Zero || _paneCount >= MaxPanes)
            return;

        int slot = _paneCount;
        if (!CreateCorePane(slot))
            return;
        if (_paneCount == 1)
            _splitVertical = edge is Edge.Top or Edge.Bottom;

        _paneCount++;
        _slotTab[slot] = dropped.Id;
        if (_slotTab[0] == InvalidId)
            _slotTab[0] = (TabList.SelectedItem as TabVM)?.Id ?? (Tabs.Count > 0 ? Tabs[0].Id : InvalidId);

        ApplyLayout();                 // make slot visible + size every pane
        BindSwapchain(slot);           // SetSwapChain now the panel is realized
        PushPaneSize(_panels[slot]);   // size once more with the swapchain bound

        // Bind sessions to panes (backbuffers now exist → renders real content).
        for (int i = 0; i < _paneCount; i++)
            if (_slotTab[i] != InvalidId)
                Native.velo_pane_bind(_engine, _slotCore[i], _slotTab[i]);
    }

    /// Arrange the active panels in the 2x2 grid per pane count + orientation.
    private void ApplyLayout()
    {
        for (int i = 0; i < MaxPanes; i++)
            _panels[i].Visibility = i < _paneCount ? Visibility.Visible : Visibility.Collapsed;

        void Place(int i, int r, int c, int rs, int cs)
        {
            var p = _panels[i];
            Grid.SetRow(p, r); Grid.SetColumn(p, c);
            Grid.SetRowSpan(p, rs); Grid.SetColumnSpan(p, cs);
        }

        switch (_paneCount)
        {
            case 1:
                Place(0, 0, 0, 2, 2);
                break;
            case 2:
                if (_splitVertical) { Place(0, 0, 0, 1, 2); Place(1, 1, 0, 1, 2); }
                else { Place(0, 0, 0, 2, 1); Place(1, 0, 1, 2, 1); }
                break;
            case 3: // left full height, right column split top/bottom
                Place(0, 0, 0, 2, 1); Place(1, 0, 1, 1, 1); Place(2, 1, 1, 1, 1);
                break;
            default: // 4 quadrants
                Place(0, 0, 0, 1, 1); Place(1, 0, 1, 1, 1); Place(2, 1, 0, 1, 1); Place(3, 1, 1, 1, 1);
                break;
        }

        PaneHost.UpdateLayout();        // force layout so ActualWidth is valid below
        for (int i = 0; i < _paneCount; i++)
            PushPaneSize(_panels[i]);
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
                    foreach (var m in g.Members) TabListItems.Add(m);
            }
        }
        foreach (var entry in _layout)
            if (entry is TabVM t)
                TabListItems.Add(t);
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
        GroupTabs(OrderedTabs().Where(_selected.Contains).ToList());
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
        // New group from the current selection (≥2 selected) or just this tab.
        var groupTabs = _selected.Count >= 2 && _selected.Contains(t)
            ? OrderedTabs().Where(_selected.Contains).ToList()
            : new List<TabVM> { t };
        Add(flyout, groupTabs.Count > 1 ? $"New group ({groupTabs.Count} tabs)" : "New group",
            () => { GroupTabs(groupTabs); ClearMultiSelect(); });
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

    /// Push the current settings into the core + chrome. The titlebar background
    /// tracks the terminal background so they read as one blended surface.
    private void ApplySettings()
    {
        if (_engine == IntPtr.Zero)
            return;
        Native.velo_set_font_size(_engine, (float)(_settings.FontSize * _zoom));
        SetShell(_settings.Shell);
        PushPalette(Themes.ByName(_settings.ThemeName));
        var (r, g, b) = _settings.BackgroundRgb();
        Native.velo_set_bg(_engine, r, g, b);
        Native.velo_set_bg_alpha(_engine, (float)_settings.BackgroundOpacity);

        // One brush drives the title bar + both panels so they share a single tint
        // layer that exactly matches the terminal (which the swapchain composites as
        // ONE layer over the backdrop). ContentRoot stays transparent — see its XAML
        // note — so the terminal isn't darkened by a second tint layer behind it.
        ContentRoot.Background = null;            // transparent: swapchain owns the body tint
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
        TitleBar.Background = surface;
        SidebarSurface.Background = surface;
        DetailsSurface.Background = surface;
        // Editor tab strip composites like the rest of the chrome, and its
        // text follows the terminal theme's foreground.
        EditorTabs.Background = surface;
        var fg = Themes.Rgb(Themes.ByName(_settings.ThemeName).Fg);
        var fgBrush = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, fg.R, fg.G, fg.B));
        EditorTabs.Foreground = fgBrush;
        TitleBarTitle.Foreground = fgBrush;
        // Palette card: same tint as the chrome, but floored so text stays
        // readable over the dim overlay at very low opacity settings.
        byte pa = (byte)Math.Round(Math.Max(_settings.BackgroundOpacity, 0.85) * 255);
        PaletteCard.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(pa, r, g, b));
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var fontBox = new NumberBox
        {
            Header = "Font size (pt)",
            Value = _settings.FontSize,
            Minimum = 6,
            Maximum = 72,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var shellBox = new ComboBox
        {
            Header = "Shell",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Text = _settings.Shell,
        };
        foreach (var s in new[] { "powershell.exe", "pwsh.exe", "cmd.exe", "wsl.exe" })
            shellBox.Items.Add(s);

        var bgBox = new TextBox { Header = "Terminal background / tint (#RRGGBB)", Text = _settings.BackgroundHex };

        var opacityBox = new Slider
        {
            Header = "Terminal background opacity (blur)",
            Minimum = 0,
            Maximum = 100,
            Value = _settings.BackgroundOpacity * 100,
            StepFrequency = 5,
        };

        var backdropBox = new ComboBox { Header = "Backdrop", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "Mica", "MicaAlt", "Acrylic", "None" })
            backdropBox.Items.Add(s);
        backdropBox.SelectedItem = _settings.Backdrop;

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(fontBox);
        panel.Children.Add(shellBox);
        panel.Children.Add(bgBox);
        panel.Children.Add(opacityBox);
        panel.Children.Add(backdropBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Lower the terminal opacity to let the backdrop blur show "
                 + "through the terminal (0 = full blur, 100 = solid).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            FocusTerminal();
            return;
        }

        _settings.FontSize = double.IsNaN(fontBox.Value) ? _settings.FontSize : fontBox.Value;
        _settings.Shell = string.IsNullOrWhiteSpace(shellBox.Text) ? _settings.Shell : shellBox.Text;
        _settings.BackgroundHex = bgBox.Text;
        _settings.BackgroundOpacity = opacityBox.Value / 100.0;
        _settings.Backdrop = backdropBox.SelectedItem as string ?? _settings.Backdrop;
        _settings.Save();

        ApplyBackdrop();
        ApplySettings();
        FocusTerminal();
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
            Native.velo_editor_save_all(_engine);   // flush on exit
        PaneHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EditorHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) EditorPanel.Focus(FocusState.Programmatic);
        else FocusTerminal();
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
        if (Native.velo_editor_key(_engine, (uint)e.Key, Modifiers()) != 0)
            e.Handled = true;
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

    /// Mirror the active tab's title into the title-bar center.
    private void UpdateTitleBarTitle()
        => TitleBarTitle.Text = CurrentTab()?.Title ?? "";

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
