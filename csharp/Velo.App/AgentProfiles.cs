using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Velo.App;

/// <summary>A headless-capable terminal AI agent for the sidebar Agent panel.
/// <paramref name="Args"/> is the non-interactive invocation (prompt on stdin),
/// pinned to the agent's small/cheap model where it has one;
/// <paramref name="ContinueArgs"/> resumes the previous session, null = stateless.
/// <paramref name="Distro"/> non-null = runs inside that WSL distro via
/// <paramref name="Shell"/> (the distro's login shell, so PATH matches probing).</summary>
public sealed record AgentProfile(string Name, string Exe, string? Distro, string Args, string? ContinueArgs, string Shell = "sh")
{
    public override string ToString() => Name; // ComboBox display
}

/// <summary>Discovers installed agents (Claude Code, Codex, OpenCode, Gemini) on
/// the Windows PATH and inside WSL distros. Cached after the first probe.</summary>
public static class AgentProfiles
{
    // Args pin small models to save tokens (claude→haiku, gemini→flash) and
    // auto-approve tool use so headless runs can actually create/edit files:
    // claude acceptEdits + Write/Edit/Bash, codex --full-auto (sandboxed
    // workspace writes), gemini --yolo.
    // ponytail: codex/opencode left on their configured default model, no stable small-model id
    private const string ClaudeTools = "--permission-mode acceptEdits --allowedTools Write Edit Bash";
    private static readonly (string Exe, string Name, string Args, string? Cont)[] Known =
    {
        ("claude", "Claude Code", $"-p --model haiku {ClaudeTools}", $"-p --continue --model haiku {ClaudeTools}"),
        ("codex", "Codex", "exec --full-auto -", "exec resume --last --full-auto -"),
        ("opencode", "OpenCode", "run", null),
        ("gemini", "Gemini CLI", "-m gemini-2.5-flash --yolo", null),
    };

    private static Task<List<AgentProfile>>? _cache;

    public static Task<List<AgentProfile>> AllAsync() => _cache ??= DiscoverAsync();

    private static async Task<List<AgentProfile>> DiscoverAsync()
    {
        var list = new List<AgentProfile>();
        foreach (var (exe, name, args, cont) in Known)
            if (OnPath(exe))
                list.Add(new(name, exe, null, args, cont));
        // Ollama shows up only when installed AND at least one model is pulled.
        if (OnPath("ollama") && await OllamaModelAsync(null) is string wm)
            list.Add(new($"Ollama ({wm})", "ollama", null, $"run {wm}", null));
        foreach (var distro in ShellProfiles.WslDistros())
        {
            // Probe (and later launch) through the distro's own login shell:
            // sh skips .bashrc/.zshrc, hiding nvm/npm-installed agents.
            var shell = await ShellProfiles.DefaultShellForAsync(distro);
            foreach (var exe in await WslAgentsAsync(distro, shell))
            {
                var k = Array.Find(Known, k => k.Exe == exe);
                list.Add(new($"{k.Name ?? exe} ({distro})", exe, distro, k.Args ?? "", k.Cont, shell));
            }
            if (await OllamaModelAsync(distro, shell) is string lm)
                list.Add(new($"Ollama {lm} ({distro})", "ollama", distro, $"run {lm}", null, shell));
        }
        return list;
    }

    /// First pulled model from `ollama list` (line 2, first column), or null
    /// when ollama is missing / has no models. null distro = Windows.
    private static async Task<string?> OllamaModelAsync(string? distro, string shell = "sh")
    {
        try
        {
            ProcessStartInfo psi;
            if (distro is null)
            {
                psi = new ProcessStartInfo("cmd.exe");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("ollama list");
            }
            else
            {
                psi = new ProcessStartInfo("wsl.exe");
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(distro);
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(shell);
                psi.ArgumentList.Add("-lic");
                psi.ArgumentList.Add("ollama list 2>/dev/null");
            }
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var raw = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var lines = raw.Split('\n');
            for (int i = 1; i < lines.Length; i++)   // line 0 = header
            {
                var tok = lines[i].Trim().Split(' ', '\t')[0];
                if (tok.Length > 0)
                    return tok;
            }
        }
        catch { /* not installed / daemon down */ }
        return null;
    }

    /// npm globals install `.cmd` shims, native installers `.exe` — check both.
    private static bool OnPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                if (File.Exists(Path.Combine(dir, exe + ".exe")) ||
                    File.Exists(Path.Combine(dir, exe + ".cmd")))
                    return true;
            }
            catch { /* malformed PATH entry */ }
        }
        return false;
    }

    /// One probe process per distro: prints the name of each known agent found.
    private static async Task<List<string>> WslAgentsAsync(string distro, string shell)
    {
        var found = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, // see ShellProfiles.ProbeShellAsync
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(shell);
            psi.ArgumentList.Add("-lic"); // login+interactive: sources rc files (nvm et al.)
            var names = string.Join(' ', Array.ConvertAll(Known, k => k.Exe));
            psi.ArgumentList.Add($"for a in {names}; do command -v $a >/dev/null 2>&1 && echo $a; done");
            using var p = Process.Start(psi);
            if (p is null)
                return found;
            var raw = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in raw.Split('\n'))
            {
                var name = line.Trim().Trim('\0');
                if (name.Length > 0)
                    found.Add(name);
            }
        }
        catch { /* WSL not installed, distro unreachable */ }
        return found;
    }
}
