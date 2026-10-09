using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class XmlFormatterPage : ToolPageBase
{
    public XmlFormatterPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<XmlFormatterViewModel>();
        Tool = ViewModel;
    }

    public XmlFormatterViewModel ViewModel { get; }
}
