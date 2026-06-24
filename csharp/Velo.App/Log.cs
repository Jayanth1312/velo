using System;
using System.IO;

namespace Velo.App;

/// Dead-simple file logger. This is a WinExe (GUI subsystem) so stdout/stderr
/// are invisible under `dotnet run`; we append to a file instead.
/// ponytail: drop this once the black-screen/close bug is found.
internal static class Log
{
    private static readonly object Gate = new();

    /// velo-debug.log in the process working directory (= the folder you ran
    /// `dotnet run` from, normally the repo root D:\velo). Falls back to %TEMP%.
    public static string Path { get; } = ResolvePath();

    private static string ResolvePath()
    {
        try { return System.IO.Path.Combine(Directory.GetCurrentDirectory(), "velo-debug.log"); }
        catch { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velo-debug.log"); }
    }

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public static void Ex(string where, Exception e) =>
        Write($"EXCEPTION @ {where}: {e}");
}
