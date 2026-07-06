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

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedBrush)));
        }
    }

    /// Pill fill: the ListViewItemPresenter draws square backgrounds no matter
    /// what CornerRadius says, so the template's rounded Border binds this.
    public Microsoft.UI.Xaml.Media.Brush SelectedBrush => new
        Microsoft.UI.Xaml.Media.SolidColorBrush(_isSelected
            ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    public event PropertyChangedEventHandler? PropertyChanged;
}
