using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Velo.App;

/// <summary>One command run in a tab (OSC 133): text, exit code, duration.</summary>
public sealed class CommandEntry
{
    public string Command { get; set; } = "";
    public int Exit { get; set; }
    public long DurMs { get; set; }
    public DateTime When { get; set; }

    // Display helpers (Outline list renders via ToString — no x:Bind template).
    public string WhenText => When.ToString("HH:mm");
    public string Meta => Exit == 0 ? $"{DurMs} ms" : $"exit {Exit} · {DurMs} ms";
    public override string ToString() => $"{Command}    {WhenText} · {Meta}";
}

/// <summary>One vertical-tab entry: stable id + live title + status badge.</summary>
public sealed class TabVM : INotifyPropertyChanged
{
    public uint Id { get; }

    private string _title;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(); } }
    }

    // ponytail: badge plumbed but always "" (idle) this pass; real source
    // (OSC 9/777 or a shell hook) is a follow-up. Glyph e.g. "" running.
    private string _badge = "";
    public string Badge
    {
        get => _badge;
        set { if (_badge != value) { _badge = value; Raise(); } }
    }

    // Shell integration (OSC 7 / 133). Cwd updates on `cd`; CommandHistory
    // grows as commands run. The detail panels (later phases) bind to these.
    private string _cwd = "";
    public string Cwd
    {
        get => _cwd;
        set { if (_cwd != value) { _cwd = value; Raise(); } }
    }

    /// Command currently running (set on OSC 133;C, cleared on D), for a badge/readout.
    private string _runningCommand = "";
    public string RunningCommand
    {
        get => _runningCommand;
        set { if (_runningCommand != value) { _runningCommand = value; Raise(); } }
    }

    public ObservableCollection<CommandEntry> CommandHistory { get; } = new();

    public TabVM(uint id, string title)
    {
        Id = id;
        _title = title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
