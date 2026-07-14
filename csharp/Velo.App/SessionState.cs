using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Velo.App;

/// <summary>Tabs (launch command + cwd) saved at exit, restored on launch:
/// %APPDATA%\velo\session.json.
/// ponytail: shells restart fresh in their old directories — live processes
/// cannot survive the app exiting; that needs a detached PTY-server daemon.</summary>
public sealed class SessionState
{
    public sealed class TabInfo
    {
        public string Cmd { get; set; } = "";
        public string Cwd { get; set; } = "";
        /// velo-pty-host session id; uint.MaxValue = none (spawn fresh).
        public uint HostId { get; set; } = uint.MaxValue;
        /// Command running at close (OSC 133), for opt-in re-run on restore.
        public string Running { get; set; } = "";
    }

    public sealed class AgentMsg
    {
        public string Text { get; set; } = "";
        public bool User { get; set; }
    }

    public List<TabInfo> Tabs { get; set; } = new();

    // Sidebar agent chat, restored with the tabs.
    public List<AgentMsg> AgentChat { get; set; } = new();
    public string AgentName { get; set; } = "";
    public bool AgentContinue { get; set; }
    /// Question in flight when the app closed; re-asked on restore so the
    /// answer isn't lost (the agent process dies with the app).
    public string AgentPending { get; set; } = "";

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "velo", "session.json");

    public static SessionState Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path)) ?? new();
        }
        catch { /* corrupt file: start clean */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch { /* best effort */ }
    }
}
