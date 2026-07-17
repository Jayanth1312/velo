using System;
using System.IO;
using System.Text.Json;

namespace Velo.App;

/// <summary>Persisted user settings: %APPDATA%\velo\settings.json.</summary>
public sealed class Settings
{
    public double FontSize { get; set; } = 13.0;
    /// Terminal font family; "" = bundled Cascadia Code NF default.
    public string FontFamily { get; set; } = "";
    /// Also use FontFamily for the app chrome (tabs, dialogs), not just the grid.
    public bool ApplyFontToApp { get; set; } = false;
    public string Shell { get; set; } = "powershell.exe";
    /// "#RRGGBB" terminal background (surface clear color / tint).
    public string BackgroundHex { get; set; } = "#1E1E1E";
    /// Terminal background opacity: 0 = fully transparent (Mica/acrylic blurs
    /// through the terminal), 1 = opaque. Values in between tint the blur.
    public double BackgroundOpacity { get; set; } = 1.0;
    /// One of: Mica, MicaAlt, Acrylic, None.
    public string Backdrop { get; set; } = "Mica";
    /// Programming ligatures (=> != ->) in the terminal grid.
    public bool Ligatures { get; set; } = true;
    /// Cursor shape: Default (shell-controlled via DECSCUSR), Block, Bar, Underline.
    public string CursorStyle { get; set; } = "Default";
    /// Blink the terminal cursor.
    public bool CursorBlink { get; set; } = false;
    /// Finishing a mouse selection (drag / double-click) copies it to the clipboard.
    public bool CopyOnSelect { get; set; } = true;
    /// Active color theme name (see Themes.All). Drives the ANSI palette + the
    /// terminal/chrome background.
    public string ThemeName { get; set; } = "Velo Dark";
    /// Inject OSC 7 / 133 shell-integration into the shell profile on startup
    /// (cwd + per-command tracking). False = never touch the profile.
    public bool ShellIntegration { get; set; } = true;
    /// Extra launch command shown in the sidebar Agent panel ("" = none).
    public string CustomAgentCommand { get; set; } = "";
    /// Agent preselected in the sidebar panel, by display name ("" = first found).
    public string DefaultAgent { get; set; } = "";
    /// Run tabs in the detached PTY host so their processes survive app close.
    public bool SessionRecovery { get; set; } = true;
    /// Resume the sidebar agent conversation on launch.
    public bool RestoreAgentChat { get; set; } = true;
    /// Re-launch the command a tab was running when its live session is gone:
    /// "Off", "Whitelisted", or "All".
    public string RerunOnRestore { get; set; } = "Whitelisted";
    /// First-token prefixes eligible for re-run (space separated).
    public string RerunWhitelist { get; set; } = "npm pnpm yarn cargo node python ping";

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "velo", "settings.json");

    public static Settings Load()
    {
        try
        {
            var p = Path;
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(p)) ?? new Settings();
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var p = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    /// Parse "#RRGGBB" to bytes; falls back to dark gray on a bad value.
    public (byte R, byte G, byte B) BackgroundRgb()
    {
        var s = BackgroundHex.TrimStart('#');
        if (s.Length == 6
            && byte.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (0x1E, 0x1E, 0x1E);
    }
}
