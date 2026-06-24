using System;
using System.IO;

namespace Velo.App;

/// <summary>
/// Idempotently injects OSC 7 (cwd) + OSC 133 (command marks) emitters into the
/// user's shell profile so Velo can track cwd / command start-end-exit per tab.
/// The block is fenced by marker comments and never double-injected; set the env
/// var VELO_SHELL_INTEGRATION=0 (or the Settings toggle) to make the block a no-op.
/// </summary>
internal static class ShellIntegration
{
    private const string Begin = "# >>> velo integration >>>";
    private const string End = "# <<< velo integration <<<";

    /// Inject for whichever shell `settings.Shell` names. Best-effort; failures
    /// (no profile dir, locked file) are swallowed — integration just stays off.
    public static void Ensure(Settings settings)
    {
        if (!settings.ShellIntegration)
            return;
        try
        {
            var shell = (settings.Shell ?? "").ToLowerInvariant();
            if (shell.Contains("bash") || shell.Contains("wsl"))
                InjectBash();
            else
                InjectPowerShell(shell.Contains("pwsh"));
        }
        catch (Exception ex)
        {
            Log.Ex("ShellIntegration.Ensure", ex);
        }
    }

    /// Append `block` between the markers to `path`, once. If the markers are
    /// already present the file is left untouched.
    private static void InjectFile(string path, string block)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (existing.Contains(Begin))
            return;
        var sep = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        File.AppendAllText(path, $"{sep}{Begin}\n{block}\n{End}\n");
        Log.Write($"ShellIntegration: injected into {path}");
    }

    private static void InjectPowerShell(bool pwsh)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Path.Combine(
            docs,
            pwsh ? "PowerShell" : "WindowsPowerShell",
            "Microsoft.PowerShell_profile.ps1");
        InjectFile(profile, PowerShellBlock);
    }

    private static void InjectBash()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        InjectFile(Path.Combine(home, ".bashrc"), BashBlock);
    }

    // ESC = [char]27, BEL = [char]7. Uses $([char]..) (not `e) for Windows
    // PowerShell 5.1 compatibility. Prompt emits D (prev exit) + A + OSC7 cwd;
    // an Enter key handler emits C;<command-line> just before execution.
    private const string PowerShellBlock = """
if ($env:VELO_SHELL_INTEGRATION -ne '0') {
  $global:__VeloPrompt = $function:prompt
  function global:prompt {
    $code = $LASTEXITCODE; if ($null -eq $code) { $code = 0 }
    $e = [char]27; $b = [char]7
    $h = (Get-Location).Path
    $out = "$e]133;D;$code$b$e]133;A$b$e]7;file://$env:COMPUTERNAME$h$b"
    $out += (& $global:__VeloPrompt)
    $out += "$e]133;B$b"
    $out
  }
  if (Get-Module -ListAvailable PSReadLine) {
    Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
      $line = $null; $cur = $null
      [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cur)
      $e = [char]27; $b = [char]7
      [Console]::Write("$e]133;C;$line$b")
      [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
    }
  }
}
""";

    // DEBUG trap emits C before each command; PROMPT_COMMAND emits D + A + OSC7.
    // ponytail: the DEBUG trap also fires for PROMPT_COMMAND's own commands, so a
    // stray C can precede the prompt — harmless (no matching text of interest),
    // tighten with a re-entrancy guard if the Outline panel shows noise.
    private const string BashBlock = """
if [ "$VELO_SHELL_INTEGRATION" != "0" ]; then
  __velo_preexec() { printf '\033]133;C;%s\007' "$BASH_COMMAND"; }
  trap '__velo_preexec' DEBUG
  __velo_prompt() {
    local e=$?
    printf '\033]133;D;%s\007\033]133;A\007\033]7;file://%s%s\007' "$e" "$HOSTNAME" "$PWD"
  }
  case "$PROMPT_COMMAND" in
    *__velo_prompt*) ;;
    *) PROMPT_COMMAND="__velo_prompt${PROMPT_COMMAND:+;$PROMPT_COMMAND}" ;;
  esac
fi
""";
}
