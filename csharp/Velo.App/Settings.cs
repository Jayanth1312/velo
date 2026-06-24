using System;
using System.IO;
using System.Text.Json;

namespace Velo.App;

/// <summary>Persisted user settings: %APPDATA%\velo\settings.json.</summary>
public sealed class Settings
{
    public double FontSize { get; set; } = 13.0;
    public string Shell { get; set; } = "powershell.exe";
    /// "#RRGGBB" terminal background (surface clear color / tint).
    public string BackgroundHex { get; set; } = "#1E1E1E";
    /// Terminal background opacity: 0 = fully transparent (Mica/acrylic blurs
    /// through the terminal), 1 = opaque. Values in between tint the blur.
    public double BackgroundOpacity { get; set; } = 1.0;
    /// One of: Mica, MicaAlt, Acrylic, None.
    public string Backdrop { get; set; } = "Mica";
    /// Active color theme name (see Themes.All). Drives the ANSI palette + the
    /// terminal/chrome background.
    public string ThemeName { get; set; } = "Velo Dark";
    /// Inject OSC 7 / 133 shell-integration into the shell profile on startup
    /// (cwd + per-command tracking). False = never touch the profile.
    public bool ShellIntegration { get; set; } = true;

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
