using System.Text;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DevTools.App.Views.Tools;

public sealed partial class SvgToXamlPage : ToolPageBase
{
    public SvgToXamlPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SvgToXamlViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public SvgToXamlViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SvgToXamlViewModel.PreviewXaml):
                RenderConverted();
                break;

            case nameof(SvgToXamlViewModel.PreviewSvg):
                _ = RenderSourceAsync();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Renders the emitted XAML by actually loading it (FR-V10).
    /// </summary>
    /// <remarks>
    /// This is the preview and the self-test at once: if <see cref="XamlReader.Load"/> refuses
    /// the string, the tool says so immediately rather than letting output that looks plausible
    /// but will not load reach the user's project.
    /// </remarks>
    private void RenderConverted()
    {
        var xaml = ViewModel.PreviewXaml;

        if (string.IsNullOrWhiteSpace(xaml))
        {
            PreviewHost.Content = null;
            ViewModel.PreviewError = null;
            ViewModel.OutputLoads = false;
            return;
        }

        try
        {
            PreviewHost.Content = XamlReader.Load(xaml) as UIElement;
            ViewModel.PreviewError = null;
            ViewModel.OutputLoads = true;
        }
        catch (Exception ex)
        {
            PreviewHost.Content = null;
            ViewModel.PreviewError = ex.Message;
            ViewModel.OutputLoads = false;
        }
    }

    /// <summary>
    /// Renders the input with the platform's own SVG renderer, so the two previews can be
    /// compared directly.
    /// </summary>
    /// <remarks>
    /// Deliberately not this tool's parser: a source preview drawn by the converter would agree
    /// with the converted preview by construction and could never show a difference, which is
    /// the one thing the pair of previews exists to do. <c>SvgImageSource</c> is an independent
    /// renderer, so when the two pictures differ, the difference is real.
    /// </remarks>
    private async Task RenderSourceAsync()
    {
        var svg = ViewModel.PreviewSvg;

        if (string.IsNullOrWhiteSpace(svg))
        {
            SourcePreview.Source = null;
            ViewModel.SourceError = null;
            return;
        }

        try
        {
            var source = new SvgImageSource();

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(Encoding.UTF8.GetBytes(svg));
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            var status = await source.SetSourceAsync(stream);

            if (status == SvgImageSourceLoadStatus.Success)
            {
                SourcePreview.Source = source;
                ViewModel.SourceError = null;
            }
            else
            {
                SourcePreview.Source = null;
                ViewModel.SourceError = Describe(status);
            }
        }
        catch (Exception ex)
        {
            SourcePreview.Source = null;
            ViewModel.SourceError = ex.Message;
            App.LogError("SVG source preview", ex);
        }
    }

    /// <summary>
    /// What a failed load means in words. The renderer only reports a category, so this says
    /// which part of the document to look at rather than repeating the category back.
    /// </summary>
    private static string Describe(SvgImageSourceLoadStatus status) => status switch
    {
        SvgImageSourceLoadStatus.InvalidFormat =>
            "Windows could not parse this as SVG. The conversion below may still have worked — " +
            "check the XAML preview.",

        SvgImageSourceLoadStatus.NetworkError =>
            "The document references something that would have to be fetched. " +
            "External references are not followed.",

        _ =>
            "Windows could not render this document.",
    };
}
