using System.ComponentModel;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Velo.App;

/// Phosphor icon geometries (from the *.svg in the repo root). viewBox 0 0 256 256.
/// Each call returns a FRESH Geometry: a WinUI Geometry can't be assigned to more
/// than one Path.Data (single-parent), so tree rows must NOT share one instance.
/// We parse through the real XAML parser (XamlReader) — the path mini-language
/// converter — because XamlBindingHelper.ConvertValue yields a Geometry that
/// Path.Data rejects ("value does not fall within the expected range").
internal static class Icons
{
    private const string Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string FileData =
        "M213.66 82.34l-56-56A8 8 0 0 0 152 24H56a16 16 0 0 0-16 16v176a16 16 0 0 0 16 16h144a16 16 0 0 0 16-16V88a8 8 0 0 0-2.34-5.66M160 51.31L188.69 80H160ZM200 216H56V40h88v48a8 8 0 0 0 8 8h48z";
    private const string FolderClosedData =
        "M216 72h-84.69L104 44.69A15.88 15.88 0 0 0 92.69 40H40a16 16 0 0 0-16 16v144.62A15.41 15.41 0 0 0 39.39 216h177.5A15.13 15.13 0 0 0 232 200.89V88a16 16 0 0 0-16-16M40 56h52.69l16 16H40Z";
    private const string FolderOpenData =
        "M245 110.64a16 16 0 0 0-13-6.64h-16V88a16 16 0 0 0-16-16h-69.33l-27.73-20.8a16.14 16.14 0 0 0-9.6-3.2H40a16 16 0 0 0-16 16v144a8 8 0 0 0 8 8h179.1a8 8 0 0 0 7.59-5.47l28.49-85.47a16.05 16.05 0 0 0-2.18-14.42M93.34 64l27.73 20.8a16.12 16.12 0 0 0 9.6 3.2H200v16h-53.57a16 16 0 0 0-8.88 2.69l-20 13.31H69.42a15.94 15.94 0 0 0-14.86 10.06L40 166.46V64Z";

    private static Geometry Parse(string d)
    {
        // XamlReader parents the Geometry to this temp Path; a parented Geometry
        // can't be assigned to another Path.Data ("value does not fall within the
        // expected range"). Detach it before handing it out.
        var tmp = (Path)XamlReader.Load($"<Path xmlns=\"{Ns}\" Data=\"{d}\"/>");
        var geo = tmp.Data;
        tmp.Data = null;
        return geo;
    }

    public static Geometry NewFile() => Parse(FileData);
    public static Geometry NewFolderClosed() => Parse(FolderClosedData);
    public static Geometry NewFolderOpen() => Parse(FolderOpenData);
}

/// One row in the Files tree. Owns its own Geometry instances (no sharing), and
/// swaps the folder icon closed/open as it expands.
public sealed class FileItem : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }

    private bool _isOpen;

    public void SetOpen(bool open)
    {
        if (_isOpen == open) return;
        _isOpen = open;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    // Fresh Geometry every read: a WinUI Geometry is single-parent, so a cached
    // instance still attached to a recycled TreeView container's Path throws
    // "value does not fall within the expected range" when re-bound. Never cache.
    public Geometry Icon => IsDir
        ? (_isOpen ? Icons.NewFolderOpen() : Icons.NewFolderClosed())
        : Icons.NewFile();

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// One changed-file row in the Git panel: status badge + left-truncated path.
public sealed class GitFileRow
{
    public string Status { get; init; } = "";        // M / A / D / R / ? …
    public Brush? StatusBrush { get; init; }
    public string Display { get; init; } = "";        // "…tail/of/path.cs"
    public string FullPath { get; init; } = "";       // tooltip + git arg
}
