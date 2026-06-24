using System;
using Microsoft.UI.Xaml;

namespace Velo.App;

/// <summary>One command-palette row: label + optional shortcut hint + the action
/// to run. <see cref="KeepOpen"/> rows (e.g. "Theme…") navigate within the
/// palette instead of dismissing it (shown with a trailing chevron, otty-style).
/// <see cref="Checked"/> toggle rows show a leading check glyph.</summary>
public sealed class PaletteItem
{
    public string Label { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public bool Checked { get; set; }
    public bool KeepOpen { get; set; }
    public Action? Action { get; set; }

    // x:Bind visibility helpers for the row template.
    public Visibility CheckVis => Checked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShortcutVis => string.IsNullOrEmpty(Shortcut) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChevronVis => KeepOpen ? Visibility.Visible : Visibility.Collapsed;
}
