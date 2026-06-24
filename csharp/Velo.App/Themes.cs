using System;

namespace Velo.App;

/// <summary>One color theme: 16 ANSI colors + fg/bg/cursor/selection, all
/// "#RRGGBB". Bundled, VS-Code-style palettes (Dracula, Nord, …).</summary>
public sealed record VeloTheme(
    string Name,
    string[] Ansi,      // 16 entries: 0-7 normal, 8-15 bright
    string Fg,
    string Bg,
    string Cursor,
    string Selection);

public static class Themes
{
    /// First entry is the default (matches Velo's original Windows look).
    public static readonly VeloTheme[] All =
    {
        new("Velo Dark",
            new[] { "#0C0C0C","#C50F1F","#13A10E","#C19C00","#0037DA","#881798","#3A96DD","#CCCCCC",
                    "#767676","#E74856","#16C60C","#F9F1A5","#3B78FF","#B4009E","#61D6D6","#F2F2F2" },
            Fg:"#D0D0D0", Bg:"#1E1E1E", Cursor:"#D0D0D0", Selection:"#3A4A66"),

        new("Dracula",
            new[] { "#21222C","#FF5555","#50FA7B","#F1FA8C","#BD93F9","#FF79C6","#8BE9FD","#F8F8F2",
                    "#6272A4","#FF6E6E","#69FF94","#FFFFA5","#D6ACFF","#FF92DF","#A4FFFF","#FFFFFF" },
            Fg:"#F8F8F2", Bg:"#282A36", Cursor:"#F8F8F2", Selection:"#44475A"),

        new("One Dark",
            new[] { "#282C34","#E06C75","#98C379","#E5C07B","#61AFEF","#C678DD","#56B6C2","#ABB2BF",
                    "#5C6370","#E06C75","#98C379","#E5C07B","#61AFEF","#C678DD","#56B6C2","#FFFFFF" },
            Fg:"#ABB2BF", Bg:"#282C34", Cursor:"#528BFF", Selection:"#3E4451"),

        new("Nord",
            new[] { "#3B4252","#BF616A","#A3BE8C","#EBCB8B","#81A1C1","#B48EAD","#88C0D0","#E5E9F0",
                    "#4C566A","#BF616A","#A3BE8C","#EBCB8B","#81A1C1","#B48EAD","#8FBCBB","#ECEFF4" },
            Fg:"#D8DEE9", Bg:"#2E3440", Cursor:"#D8DEE9", Selection:"#434C5E"),

        new("Monokai",
            new[] { "#272822","#F92672","#A6E22E","#F4BF75","#66D9EF","#AE81FF","#A1EFE4","#F8F8F2",
                    "#75715E","#F92672","#A6E22E","#F4BF75","#66D9EF","#AE81FF","#A1EFE4","#F9F8F5" },
            Fg:"#F8F8F2", Bg:"#272822", Cursor:"#F8F8F2", Selection:"#49483E"),

        new("Tokyo Night",
            new[] { "#15161E","#F7768E","#9ECE6A","#E0AF68","#7AA2F7","#BB9AF7","#7DCFFF","#A9B1D6",
                    "#414868","#F7768E","#9ECE6A","#E0AF68","#7AA2F7","#BB9AF7","#7DCFFF","#C0CAF5" },
            Fg:"#C0CAF5", Bg:"#1A1B26", Cursor:"#C0CAF5", Selection:"#33467C"),

        new("Solarized Dark",
            new[] { "#073642","#DC322F","#859900","#B58900","#268BD2","#D33682","#2AA198","#EEE8D5",
                    "#002B36","#CB4B16","#586E75","#657B83","#839496","#6C71C4","#93A1A1","#FDF6E3" },
            Fg:"#839496", Bg:"#002B36", Cursor:"#93A1A1", Selection:"#073642"),

        new("GitHub Dark",
            new[] { "#484F58","#FF7B72","#3FB950","#D29922","#58A6FF","#BC8CFF","#39C5CF","#B1BAC4",
                    "#6E7681","#FFA198","#56D364","#E3B341","#79C0FF","#D2A8FF","#56D4DD","#F0F6FC" },
            Fg:"#C9D1D9", Bg:"#0D1117", Cursor:"#C9D1D9", Selection:"#163356"),

        new("GitHub Light",
            new[] { "#24292E","#D73A49","#28A745","#DBAB09","#0366D6","#5A32A1","#1B7C83","#6A737D",
                    "#959DA5","#CB2431","#22863A","#B08800","#005CC5","#5A32A1","#3192AA","#D1D5DA" },
            Fg:"#24292F", Bg:"#FFFFFF", Cursor:"#24292F", Selection:"#C8E1FF"),
    };

    public static VeloTheme ByName(string? name)
        => Array.Find(All, t => t.Name == name) ?? All[0];

    /// 57-byte palette payload for velo_set_palette: 16 ANSI + fg + cursor +
    /// selection as RGB triples (background goes through velo_set_bg).
    public static byte[] PaletteBytes(this VeloTheme t)
    {
        var b = new byte[19 * 3];
        for (int i = 0; i < 16; i++)
            WriteRgb(b, i, t.Ansi[i]);
        WriteRgb(b, 16, t.Fg);
        WriteRgb(b, 17, t.Cursor);
        WriteRgb(b, 18, t.Selection);
        return b;
    }

    private static void WriteRgb(byte[] dst, int triple, string hex)
    {
        var (r, g, bl) = Rgb(hex);
        dst[triple * 3] = r;
        dst[triple * 3 + 1] = g;
        dst[triple * 3 + 2] = bl;
    }

    /// Parse "#RRGGBB" → bytes (falls back to mid-gray on a bad value).
    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6
            && byte.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (0x80, 0x80, 0x80);
    }
}
