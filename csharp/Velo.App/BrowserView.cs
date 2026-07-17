using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Velo.App;

/// <summary>An in-terminal browser pane: a Chrome-style chrome bar (back / forward /
/// home / address / reload / open-external / settings / maximize / close) over a
/// WebView2. One per browser tab; kept alive (collapsed) off-screen so the page
/// state survives tab switches. Reuses the Edge/WebView2 runtime already on Win10+.</summary>
public sealed class BrowserView : Grid, IDisposable
{
    private const string Home = "https://www.google.com/";

    private readonly WebView2 _web = new();
    private readonly TextBox _url = new();
    private readonly Button _back;
    private readonly Button _fwd;
    private readonly TabVM _tab;
    private readonly Action _onClose;
    private readonly Action _onMaximize;
    private bool _disposed;

    public BrowserView(TabVM tab, Action onClose, Action onMaximize, string? initialUrl = null)
    {
        _tab = tab;
        _onClose = onClose;
        _onMaximize = onMaximize;

        Background = new SolidColorBrush(Hex(0x1B, 0x1B, 0x1B));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _back = IconBtn("\uE72B", "Back", (_, _) => _web.CoreWebView2?.GoBack());
        _fwd = IconBtn("\uE72A", "Forward", (_, _) => _web.CoreWebView2?.GoForward());
        var chrome = BuildChrome();
        SetRow(chrome, 0);
        Children.Add(chrome);

        SetRow(_web, 1);
        Children.Add(_web);

        _web.CoreWebView2Initialized += OnCoreInitialized;
        InitWeb(string.IsNullOrWhiteSpace(initialUrl) ? Home : initialUrl!);
    }

    /// Init the core with an explicit persistent user-data folder before the
    /// first navigation. The implicit default lands next to the exe — unwritable
    /// installs fail init silently and cookies/logins reset between sessions,
    /// which read as "browser tabs come back reset" on restore.
    private async void InitWeb(string url)
    {
        try
        {
            var udf = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "velo", "WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(null, udf);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Ex("BrowserView.InitWeb", ex);
            // Fall back to the default environment rather than a dead pane.
            try { await _web.EnsureCoreWebView2Async(); }
            catch (Exception ex2) { Log.Ex("BrowserView.InitWeb(default)", ex2); return; }
        }
        if (_disposed) return;
        _web.Source = new Uri(url);
    }

    /// Move keyboard focus into the page.
    public void FocusWeb() => _web.Focus(FocusState.Programmatic);

    // ---- Chrome ----------------------------------------------------------

    private Grid BuildChrome()
    {
        var bar = new Grid { Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Hex(0x2B, 0x2B, 0x2B)) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Glyph("\uE774")); // globe
        left.Children.Add(_back);
        left.Children.Add(_fwd);
        left.Children.Add(IconBtn("\uE80F", "Home", (_, _) => _web.Source = new Uri(Home)));
        SetColumn(left, 0);
        bar.Children.Add(left);

        _url.Foreground = new SolidColorBrush(Colors.White);
        _url.Background = new SolidColorBrush(Hex(0x1B, 0x1B, 0x1B));
        _url.BorderThickness = new Thickness(0);
        _url.CornerRadius = new CornerRadius(4);
        _url.Padding = new Thickness(8, 4, 8, 4);
        _url.Margin = new Thickness(6, 0, 6, 0);
        _url.VerticalAlignment = VerticalAlignment.Center;
        _url.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { Navigate(_url.Text); e.Handled = true; }
        };
        // Click/tab into the URL bar selects the whole address (browser UX).
        // The TextBox places its caret on pointer-release AFTER GotFocus, so
        // re-select on the first release; later clicks place the caret normally.
        // ponytail: a click-DRAG on that first click also ends select-all —
        // per-press drag detection if that ever bothers anyone.
        bool selectAllOnRelease = false;
        _url.GotFocus += (_, _) => { _url.SelectAll(); selectAllOnRelease = true; };
        _url.LostFocus += (_, _) => selectAllOnRelease = false;
        _url.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            if (!selectAllOnRelease) return;
            selectAllOnRelease = false;
            _url.SelectAll();
        }), true);
        SetColumn(_url, 1);
        bar.Children.Add(_url);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(IconBtn("\uE72C", "Reload", (_, _) => _web.CoreWebView2?.Reload()));
        right.Children.Add(IconBtn("\uE8A7", "Open in system browser", (_, _) => OpenExternal()));
        right.Children.Add(SettingsBtn());
        right.Children.Add(IconBtn("\uE922", "Maximize pane", (_, _) => _onMaximize()));
        right.Children.Add(IconBtn("\uE711", "Close tab", (_, _) => _onClose()));
        SetColumn(right, 2);
        bar.Children.Add(right);

        return bar;
    }

    private Button SettingsBtn()
    {
        var b = IconBtn("\uE713", "Settings", null);
        var menu = new MenuFlyout();
        MenuItem(menu, "Copy address", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_url.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        });
        MenuItem(menu, "Open in system browser", OpenExternal);
        MenuItem(menu, "Reload", () => _web.CoreWebView2?.Reload());
        b.Flyout = menu;
        return b;
    }

    private static void MenuItem(MenuFlyout menu, string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private Button IconBtn(string glyph, string tip, RoutedEventHandler? onClick)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(1, 0, 1, 0),
        };
        ToolTipService.SetToolTip(b, tip);
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    private static FontIcon Glyph(string g) => new()
    {
        Glyph = g,
        FontSize = 14,
        Foreground = new SolidColorBrush(Colors.White),
        Margin = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- Navigation ------------------------------------------------------

    private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs e)
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        core.SourceChanged += (_, _) => { _url.Text = core.Source; TrackUrl(core.Source); RefreshNav(); };
        core.HistoryChanged += (_, _) => RefreshNav();
        core.DocumentTitleChanged += (_, _) =>
            _tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New Tab" : core.DocumentTitle;
        _url.Text = core.Source;
        TrackUrl(core.Source);
        RefreshNav();
    }

    /// Session-persisted URL. Only real pages: the core reports about:blank
    /// during init and failed navigations, and writing that through clobbered
    /// the restored URL — the tab then "reset" on the next launch.
    private void TrackUrl(string s)
    {
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _tab.BrowserUrl = s;
    }

    private void RefreshNav()
    {
        var core = _web.CoreWebView2;
        _back.IsEnabled = core?.CanGoBack ?? false;
        _fwd.IsEnabled = core?.CanGoForward ?? false;
    }

    /// URL if it parses / looks like a host, otherwise a Google search.
    private void Navigate(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return;
        Uri uri;
        if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
            uri = abs;
        else if (s.Contains('.') && !s.Contains(' '))
            uri = new Uri("https://" + s);
        else
            uri = new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(s));
        _web.Source = uri;
    }

    private void OpenExternal()
    {
        try { Process.Start(new ProcessStartInfo(_url.Text) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Ex("BrowserView.OpenExternal", ex); }
    }

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _web.Close(); } catch { /* already gone */ }
    }
}
