using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Velo.App;

/// <summary>A launchable shell shown in the new-tab dropdown. <paramref name="Icon"/>
/// is the SVG filename under Assets/ShellIcons.</summary>
public sealed record ShellProfile(string Name, string Command, string Icon);

/// <summary>Discovers installed shells (PowerShell, cmd, pwsh, WSL distros) for the
/// new-tab menu. Result is cached after the first probe.</summary>
public static class ShellProfiles
{

    private static List<ShellProfile>? _cache;

    public static IReadOnlyList<ShellProfile> All() => _cache ??= Discover();

    private static readonly Dictionary<string, Task<string>> _shellKindCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _shellKindLock = new();

    /// <summary>The distro's login shell ("zsh", "bash", ...), probed once and cached.
    /// Runs the probe process off the UI thread; falls back to "bash" if it fails
    /// (no WSL, distro not running, etc).</summary>
    public static Task<string> DefaultShellForAsync(string distro)
    {
        lock (_shellKindLock)
        {
            if (_shellKindCache.TryGetValue(distro, out var cached))
                return cached;
            var task = ProbeShellAsync(distro);
            _shellKindCache[distro] = task;
            return task;
        }
    }

    private static async Task<string> ProbeShellAsync(string distro)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // wsl -e passes the Linux process's stdout through as raw UTF-8
                // (only `wsl -l` emits UTF-16LE). Decoding this as Unicode turned
                // "zsh"/"bash" into CJK garbage.
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("basename \"$SHELL\"");
            using var p = Process.Start(psi);
            if (p is null)
                return "bash";
            var raw = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var name = raw.Trim().Trim('\0');
            return name.Length > 0 ? name : "bash";
        }
        catch { return "bash"; } // WSL not installed, distro unreachable, etc
    }

    private static List<ShellProfile> Discover()
    {
        var list = new List<ShellProfile>
        {
            new("Windows PowerShell", "powershell.exe", "powershell.svg"),
            new("Command Prompt", "cmd.exe", "cmd.svg"),
        };
        if (OnPath("pwsh.exe"))
            list.Add(new("PowerShell", "pwsh.exe", "powershell.svg"));
        foreach (var distro in WslDistros())
            list.Add(new(distro, $"wsl.exe -d {distro}", IconForDistro(distro)));
        return list;
    }

    // Ubuntu gets its circle-of-friends; every other distro shares the Tux penguin.
    private static string IconForDistro(string distro) =>
        distro.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) ? "ubuntu.svg" : "linux.svg";

    private static bool OnPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator))
        {
            try { if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, exe))) return true; }
            catch { /* malformed PATH entry */ }
        }
        return false;
    }

    internal static IEnumerable<string> WslDistros()
    {
        string raw;
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode, // wsl emits UTF-16LE
            };
            using var p = Process.Start(psi);
            if (p is null)
                yield break;
            raw = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
        }
        catch { yield break; } // WSL not installed → no distros

        foreach (var line in raw.Split('\n'))
        {
            var name = line.Trim().Trim('\0');
            if (name.Length > 0)
                yield return name;
        }
    }
}
