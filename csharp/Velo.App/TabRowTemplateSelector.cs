using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Velo.App;

/// <summary>Picks the group-header / split-row / tab-row template for the mixed tab list.</summary>
public sealed partial class TabRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TabTemplate { get; set; }
    public DataTemplate? GroupHeaderTemplate { get; set; }
    public DataTemplate? SplitTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        TabGroup => GroupHeaderTemplate,
        SplitRowVM => SplitTemplate,
        _ => TabTemplate,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
