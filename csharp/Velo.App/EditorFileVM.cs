using System.ComponentModel;

namespace Velo.App;

/// <summary>One open editor file: drives the editor tab strip.</summary>
public sealed class EditorFileVM : INotifyPropertyChanged
{
    // Settable (not init-only): the XAML compiler's generated type info
    // (XamlTypeInfo.g.cs) assigns members through plain setters.
    public uint Id { get; set; }
    public string Path { get; set; } = "";
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
