using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Velo.App;

/// <summary>Picks the group-header vs tab-row template for the mixed tab list.</summary>
public sealed partial class TabRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TabTemplate { get; set; }
    public DataTemplate? GroupHeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is TabGroup ? GroupHeaderTemplate : TabTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
