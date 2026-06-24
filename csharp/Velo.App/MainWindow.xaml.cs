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
    /// Vertical-tab list, bound to the sidebar ListView.
    public ObservableCollection<TabVM> Tabs { get; } = new();

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
        Log.Write("ctor: enter");
        InitializeComponent();
        Log.Write($"ctor: InitializeComponent done. Content={Content?.GetType().Name ?? "null"}");

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
        // emitters are live in tab 1's profile load.
        ShellIntegration.Ensure(_settings);
        Log.Write("ctor: ShellIntegration done");
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
        => Log.Write($"PaneHost_Loaded: size={PaneHost.ActualWidth}x{PaneHost.ActualHeight}");

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
        // Instant, both ways: the column width is the only thing that moves, so the
        // terminal resizes exactly once (crisp, no flash) and the panel + its content
        // appear/disappear in the same frame (no slide lag, no lingering border).
        SidebarSurface.Width = open ? SidebarWidth : 0;
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
        _detailsOpen = open;
        AnimateWidth(DetailsToggleBar, open ? BarOpen : BarClosed);
        DetailsSurface.Width = open ? SidebarWidth : 0;   // instant, see SetSidebar
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
        if ((Content as FrameworkElement)?.FindName("DetailsBodyHost") is null)   // realizes the x:Load subtree
            return;
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

    private void InfoReveal_Click(object sender, RoutedEventArgs e) => ShellOpen(CurrentCwd());
    private void InfoOpenCode_Click(object sender, RoutedEventArgs e) => ShellExec("code", CurrentCwd());

    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    private static void ShellExec(string file, string arg)
    {
        try { Process.Start(new ProcessStartInfo { FileName = file, Arguments = $"\"{arg}\"", UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ---- Files (flat per-directory listing) ----
    private sealed class FileRow
    {
        public string Path = "";
        public bool IsDir;
        public string Name = "";
        // ponytail: list renders via ToString — no DataTemplate.
        public override string ToString() => (IsDir ? "📁 " : "📄 ") + Name;
    }

    private void RefreshFiles()
    {
        if (!_detailsRealized)
            return;
        var dir = _filesDir ?? CurrentCwd();
        _filesDir = dir;
        FilesTree.Items.Clear();
        if (!Directory.Exists(dir))
            return;
        var filter = FilesFind.Text ?? "";
        try
        {
            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Where(fi => _showHidden || (fi.Attributes & FileAttributes.Hidden) == 0)
                .Where(fi => filter.Length == 0 || fi.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fi => fi is DirectoryInfo)
                .ThenBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var fi in entries)
                FilesTree.Items.Add(new FileRow { Path = fi.FullName, IsDir = fi is DirectoryInfo, Name = fi.Name });
        }
        catch { /* permission etc. */ }
    }

    private void FilesFind_TextChanged(object sender, TextChangedEventArgs e) => RefreshFiles();
    private void FilesRefresh_Click(object sender, RoutedEventArgs e) => RefreshFiles();
    private void FilesHidden_Click(object sender, RoutedEventArgs e) { _showHidden = !_showHidden; RefreshFiles(); }

    private void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        var p = Directory.GetParent(_filesDir ?? CurrentCwd());
        if (p is not null) { _filesDir = p.FullName; RefreshFiles(); }
    }

    private void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FilesTree.SelectedItem is not FileRow r)
            return;
        if (r.IsDir) { _filesDir = r.Path; RefreshFiles(); }
        else ShellOpen(r.Path);
    }

    // ---- Git (porcelain status of the cwd's repo) ----
    private async Task RefreshGitAsync()
    {
        if (!_detailsRealized)
            return;
        var dir = CurrentCwd();
        GitBranch.Text = "…";
        GitSummary.Text = "";
        GitFiles.ItemsSource = null;

        var status = await RunGitAsync(dir, "status --porcelain=v1 --branch");
        if (string.IsNullOrWhiteSpace(status))
        {
            GitBranch.Text = "Not a git repository";
            return;
        }

        var lines = status.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string branch = "—", summary = "";
        var files = new List<string>();
        foreach (var ln in lines)
        {
            if (ln.StartsWith("## "))
            {
                var b = ln.Substring(3);
                int dots = b.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? b.Substring(0, dots) : b;
                int br = b.IndexOf('[');
                if (br >= 0) summary = b.Substring(br).Trim('[', ']');
            }
            else
            {
                files.Add(ln);
            }
        }
        GitBranch.Text = branch;
        GitSummary.Text = files.Count == 0
            ? (summary.Length > 0 ? $"clean · {summary}" : "clean")
            : (summary.Length > 0 ? $"Changes ({files.Count}) · {summary}" : $"Changes ({files.Count})");
        GitFiles.ItemsSource = files;
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
        => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Focus the PaneHost Grid, not the SwapChainPanel: the latter has no
                // Background and isn't hit-test-visible, so Focus() returns false and no
                // KeyDown/CharacterReceived ever fire (the "can't type" bug).
                if (PaneHost is null)
                {
                    Log.Write("FocusTerminal: PaneHost null, skip");
                    return;
                }
                bool ok = PaneHost.Focus(FocusState.Programmatic);
                var xr = Content?.XamlRoot;
                var f = xr is null ? "no-xamlroot"
                    : FocusManager.GetFocusedElement(xr)?.GetType().Name ?? "null";
                Log.Write($"FocusTerminal: PaneHost Focus()={ok} focused={f}");
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
        Native.velo_char(_engine, e.Character);
    }

    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        var panel = (SwapChainPanel)sender;
        uint id = PaneId(panel);
        _focusedPane = (int)id;
        PaneHost.Focus(FocusState.Pointer);   // SwapChainPanel can't take focus; keep it on the host
        panel.CapturePointer(e.Pointer);
        Native.velo_pane_focus(_engine, id);
        ForwardMouse(panel, 0, e);
    }

    /// Click anywhere in the pane area focuses the pane under the pointer. The
    /// SwapChainPanels composite their swapchain via DComp (no XAML brush), so they
    /// are not hit-test-visible and their own PointerPressed never fires — without
    /// this, clicking the terminal can't focus it and the user can't type.
    private void PaneHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        var pos = e.GetCurrentPoint(PaneHost).Position;
        for (int i = 0; i < _paneCount && i < _panels.Length; i++)
        {
            var p = _panels[i];
            if (p.Visibility != Visibility.Visible)
                continue;
            var r = p.TransformToVisual(PaneHost)
                     .TransformBounds(new Windows.Foundation.Rect(0, 0, p.ActualWidth, p.ActualHeight));
            if (pos.X >= r.X && pos.X <= r.X + r.Width && pos.Y >= r.Y && pos.Y <= r.Y + r.Height)
            {
                _focusedPane = i;
                PaneHost.Focus(FocusState.Pointer);   // SwapChainPanel can't take focus
                Native.velo_pane_focus(_engine, PaneId(p));
                break;
            }
        }
    }

    private void Panel_PointerMoved(object sender, PointerRoutedEventArgs e)
        => ForwardMouse((SwapChainPanel)sender, 1, e);

    private void Panel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var panel = (SwapChainPanel)sender;
        ForwardMouse(panel, 2, e);
        panel.ReleasePointerCapture(e.Pointer);
    }

    private void ForwardMouse(SwapChainPanel panel, uint kind, PointerRoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero)
            return;
        double sx = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : _lastScale;
        double sy = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : _lastScale;
        var p = e.GetCurrentPoint(panel).Position;
        Native.velo_pane_mouse(_engine, PaneId(panel), kind, (float)(p.X * sx), (float)(p.Y * sy));
    }

    // ---- Tab actions ------------------------------------------------------

    private void AddTab_Click(object sender, RoutedEventArgs e) => AddTab();

    private void AddTab()
    {
        if (_engine == IntPtr.Zero)
            return;
        uint id = Native.velo_tab_new(_engine);
        Log.Write($"AddTab: velo_tab_new -> {id}");
        if (id == uint.MaxValue)
        {
            Log.Write("AddTab: ABORT, velo_tab_new returned uint.MaxValue (shell spawn failed?)");
            return;
        }
        var vm = new TabVM(id, "PowerShell");
        Tabs.Add(vm);
        TabList.SelectedItem = vm; // drives velo_tab_set_active via SelectionChanged
        FocusTerminal();
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is uint id)
            CloseTab(id);
    }

    // Close button is hidden until its tab row is hovered.
    private void TabRow_PointerEntered(object sender, PointerRoutedEventArgs e) => SetRowClose(sender, 1);
    private void TabRow_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowClose(sender, 0);

    private static void SetRowClose(object sender, double opacity)
    {
        if (sender is not Grid g)
            return;
        foreach (var child in g.Children)
        {
            if (child is Button b)
            {
                b.Opacity = opacity;
                return;
            }
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
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].Id == id)
            {
                Tabs.RemoveAt(i);
                return;
            }
        }
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _engine == IntPtr.Zero)
            return;
        if (TabList.SelectedItem is TabVM t)
        {
            // Binds the focused pane to this tab (core), so track it per pane.
            Native.velo_tab_set_active(_engine, t.Id);
            _slotTab[_focusedPane] = t.Id;
            _filesDir = null;            // Files panel follows the new tab's cwd
            if (_detailsOpen) RefreshDetails();
            FocusTerminal();
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
        byte a = (byte)Math.Round(_settings.BackgroundOpacity * 255);
        var surface = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));
        ContentRoot.Background = null;            // transparent: swapchain owns the body tint
        TitleBar.Background = surface;            // thin bar matches the panels
        SidebarSurface.Background = surface;
        DetailsSurface.Background = surface;
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

    /// Apply the backdrop. "Blur intensity" maps to discrete kinds (Mica < Mica
    /// Alt < Acrylic). ponytail: continuous tint-opacity needs a manual
    /// DesktopAcrylicController; add that if a slider is required.
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
    };

    private static MainWindow? FromCtx(IntPtr ctx)
        => ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(ctx).Target as MainWindow;

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
        });
    }

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
                        tab.CommandHistory.Add(new CommandEntry { Command = cmd, Exit = exit, DurMs = (long)durMs, When = DateTime.Now });
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
