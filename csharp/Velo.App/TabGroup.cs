using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Velo.App;

/// <summary>A named, collapsible group of tabs shown as a header row in the tab list.</summary>
public sealed class TabGroup : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Preset swatches offered in the header colour menu (label + hex).</summary>
    public static readonly (string Name, string Hex)[] Swatches =
    {
        ("Grey",   "#8A8A8E"),
        ("Blue",   "#5B8DEF"),
        ("Green",  "#3FB950"),
        ("Yellow", "#D9A441"),
        ("Red",    "#E5534B"),
        ("Purple", "#A371F7"),
        ("Cyan",   "#39B3C6"),
        ("Pink",   "#EC6CB9"),
    };

    private string _name;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Raise(); } }
    }

    private string _colorHex = "#5B8DEF";
    public string ColorHex
    {
        get => _colorHex;
        set { if (_colorHex != value) { _colorHex = value; Raise(); Raise(nameof(ColorBrush)); } }
    }

    /// <summary>Solid brush built from <see cref="ColorHex"/>; bound by the colour dot + member accents.</summary>
    public Brush ColorBrush => new SolidColorBrush(ParseHex(_colorHex));

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { if (_isCollapsed != value) { _isCollapsed = value; Raise(); Raise(nameof(ChevronGlyph)); } }
    }

    // Segoe Fluent: ChevronDown (E70D) expanded, ChevronRight (E76C) collapsed.
    public string ChevronGlyph => _isCollapsed ? "" : "";

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise();
            Raise(nameof(NameVisibility));
            Raise(nameof(EditVisibility));
        }
    }

    public Visibility NameVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    private bool _showSeparator;
    /// <summary>True for every group except the first, so the divider hairline only
    /// appears between consecutive groups (set during the tab-list rebuild).</summary>
    public bool ShowSeparator
    {
        get => _showSeparator;
        set { if (_showSeparator != value) { _showSeparator = value; Raise(); Raise(nameof(SeparatorVisibility)); } }
    }

    public Visibility SeparatorVisibility => _showSeparator ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<TabVM> Members { get; } = new();

    public TabGroup(string name)
    {
        _name = name;
    }

    internal static Color ParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 7)
            return Colors.Gray;
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        return Color.FromArgb(0xFF, r, g, b);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
