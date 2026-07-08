using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Velo.App;

/// <summary>One command run in a tab (OSC 133): text, exit code, duration.</summary>
public sealed class CommandEntry
{
    public string Command { get; set; } = "";
    public int Exit { get; set; }
    public long DurMs { get; set; }
    public DateTime When { get; set; }

    // Display helpers (Outline list renders via ToString — no x:Bind template).
    public string WhenText => When.ToString("HH:mm");
    public string Meta => Exit == 0 ? $"{DurMs} ms" : $"exit {Exit} · {DurMs} ms";
    public override string ToString() => $"{Command}    {WhenText} · {Meta}";
}

/// <summary>One vertical-tab entry: stable id + live title + status badge.</summary>
public sealed class TabVM : INotifyPropertyChanged
{
    public uint Id { get; }

    private string _title;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(); } }
    }

    // ponytail: badge plumbed but always "" (idle) this pass; real source
    // (OSC 9/777 or a shell hook) is a follow-up. Glyph e.g. "" running.
    private string _badge = "";
    public string Badge
    {
        get => _badge;
        set { if (_badge != value) { _badge = value; Raise(); } }
    }

    private string _shellKind = "";
    /// <summary>Short shell label (e.g. "pwsh", "cmd", "bash", "zsh").</summary>
    public string ShellKind
    {
        get => _shellKind;
        set { if (_shellKind != value) { _shellKind = value; Raise(); } }
    }

    private string _iconFile = "powershell.svg";
    /// <summary>SVG filename under Assets/ShellIcons — the same brand icons the
    /// new-tab dropdown shows (ShellProfile.Icon).</summary>
    public string IconFile
    {
        get => _iconFile;
        set { if (_iconFile != value) { _iconFile = value; Raise(); Raise(nameof(IconSource)); } }
    }

    public Microsoft.UI.Xaml.Media.ImageSource IconSource =>
        new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri($"ms-appx:///Assets/ShellIcons/{_iconFile}"));

    // Shell integration (OSC 7 / 133). Cwd updates on `cd`; CommandHistory
    // grows as commands run. The detail panels (later phases) bind to these.
    private string _cwd = "";
    public string Cwd
    {
        get => _cwd;
        set { if (_cwd != value) { _cwd = value; Raise(); } }
    }

    /// Command currently running (set on OSC 133;C, cleared on D), for a badge/readout.
    private string _runningCommand = "";
    public string RunningCommand
    {
        get => _runningCommand;
        set { if (_runningCommand != value) { _runningCommand = value; Raise(); } }
    }

    public const int MaxCommandHistory = 500;

    public ObservableCollection<CommandEntry> CommandHistory { get; } = new();

    /// <summary>Append a command, dropping the oldest entries past <see cref="MaxCommandHistory"/>.</summary>
    public void AddCommand(CommandEntry entry)
    {
        CommandHistory.Add(entry);
        while (CommandHistory.Count > MaxCommandHistory)
            CommandHistory.RemoveAt(0);
    }

    // ---- Grouping / selection / rename UI state (pure C#, see TabGroup) ----

    /// <summary>Group this tab belongs to, or null when ungrouped. Set by the grouping logic.</summary>
    public TabGroup? Group { get; set; }

    private bool _isMultiSelected;
    /// <summary>Part of the Ctrl+Click selection set awaiting a group (Ctrl+G) action.</summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set { if (_isMultiSelected != value) { _isMultiSelected = value; Raise(); Raise(nameof(FillOpacity)); } }
    }

    private bool _isActive;
    /// <summary>The active (currently shown) tab. Drives the row fill exactly like
    /// hover / multi-select — one shared layer, so the three look identical.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Raise(); Raise(nameof(FillOpacity)); } }
    }

    private bool _isHovered;
    /// <summary>Pointer is over the row.</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered != value) { _isHovered = value; Raise(); Raise(nameof(FillOpacity)); } }
    }

    /// <summary>The single row-fill opacity. Never stacked: any combination is
    /// still just one fill. Multi-select is deliberately dimmer than the active
    /// tab so a pending Ctrl+Click set reads differently from the open tab.</summary>
    public double FillOpacity =>
        (_isActive || _isHovered) ? 1.0 :
        _isMultiSelected ? 0.45 : 0.0;

    private bool _isEditing;
    /// <summary>Inline rename in progress: the row swaps its title TextBlock for a TextBox.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise();
            Raise(nameof(TitleVisibility));
            Raise(nameof(EditVisibility));
        }
    }

    public Visibility TitleVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Left accent bar: a group's colour when grouped, transparent otherwise.</summary>
    private string _accentHex = "";
    public string AccentHex
    {
        get => _accentHex;
        set { if (_accentHex != value) { _accentHex = value; Raise(); Raise(nameof(AccentBrush)); Raise(nameof(AccentVisibility)); } }
    }

    public Brush AccentBrush => string.IsNullOrEmpty(_accentHex)
        ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        : new SolidColorBrush(TabGroup.ParseHex(_accentHex));

    public Visibility AccentVisibility => string.IsNullOrEmpty(_accentHex) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Left margin: grouped tabs indent so the group block reads as nested.</summary>
    public Thickness RowIndent => Group is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);

    /// <summary>Refresh the accent + indent after the group membership changes.</summary>
    public void RefreshGrouping()
    {
        AccentHex = Group?.ColorHex ?? "";
        Raise(nameof(RowIndent));
    }

    public TabVM(uint id, string title)
    {
        Id = id;
        _title = title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One tab-list row representing a split group: its member tabs render
/// as side-by-side mini pills (browser-style). Rebuilt by RefreshTabList.</summary>
public sealed class SplitRowVM
{
    public ObservableCollection<TabVM> Members { get; } = new();

    /// <summary>Row body built by MainWindow.BuildSplitPills (equal-width pills with
    /// hover-close). Rebuilt on every RefreshTabList so order/fill stay current.</summary>
    public UIElement? Content { get; set; }
}
