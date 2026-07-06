using System.ComponentModel;

namespace Velo.App;

/// <summary>One open editor file: drives the editor tab strip.</summary>
public sealed class EditorFileVM : INotifyPropertyChanged
{
    public uint Id { get; init; }
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);

    private bool _dirty;
    public bool Dirty
    {
        get => _dirty;
        set
        {
            if (_dirty == value) return;
            _dirty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Dirty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirtyVisibility)));
        }
    }

    public Microsoft.UI.Xaml.Visibility DirtyVisibility =>
        _dirty ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
