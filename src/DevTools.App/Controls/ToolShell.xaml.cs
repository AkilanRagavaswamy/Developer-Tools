using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace DevTools.App.Controls;

/// <summary>
/// The chrome wrapped around every tool page: the single options row, the shared message
/// banner, the busy indicator, and the content slot (FR-T01, FR-T05).
/// </summary>
/// <remarks>
/// The name, description, favourite and reset used to live here as a 90 px page header. They
/// are now in the title bar, which each page reaches through <see cref="Services.IToolChrome"/>
/// — so this control no longer knows what tool it is wrapping, only what it has to show.
/// </remarks>
[ContentProperty(Name = nameof(ToolContent))]
public sealed partial class ToolShell : UserControl
{
    private readonly Services.IToolChrome _chrome = App.GetService<Services.IToolChrome>();

    public ToolShell()
    {
        InitializeComponent();

        Loaded += (_, _) => _chrome.SetOptions(OptionsContent as FrameworkElement);

        // Only if it is still ours. Navigation loads the incoming page before it unloads the
        // outgoing one, so an unconditional clear here would throw away the new tool's row.
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_chrome.Options, OptionsContent))
            {
                _chrome.SetOptions(null);
            }
        };
    }

    public static readonly DependencyProperty ToolContentProperty = DependencyProperty.Register(
        nameof(ToolContent), typeof(object), typeof(ToolShell), new PropertyMetadata(null));

    public object? ToolContent
    {
        get => GetValue(ToolContentProperty);
        set => SetValue(ToolContentProperty, value);
    }

    /// <summary>
    /// The tool's options row, shown in the band above the page. Leave it unset for none.
    /// </summary>
    /// <remarks>
    /// Declared by the page but never parented here — see <see cref="Services.IToolChrome"/>.
    /// A `*` column in the row takes the leftover width, so a field like the formatter's
    /// JSONPath box still stretches.
    /// </remarks>
    public static readonly DependencyProperty OptionsContentProperty = DependencyProperty.Register(
        nameof(OptionsContent), typeof(object), typeof(ToolShell), new PropertyMetadata(null));

    public object? OptionsContent
    {
        get => GetValue(OptionsContentProperty);
        set => SetValue(OptionsContentProperty, value);
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ToolShell), new PropertyMetadata(null));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty MessageSeverityProperty = DependencyProperty.Register(
        nameof(MessageSeverity), typeof(ToolMessageSeverity), typeof(ToolShell),
        new PropertyMetadata(ToolMessageSeverity.Informational));

    public ToolMessageSeverity MessageSeverity
    {
        get => (ToolMessageSeverity)GetValue(MessageSeverityProperty);
        set => SetValue(MessageSeverityProperty, value);
    }

    public static readonly DependencyProperty HasMessageProperty = DependencyProperty.Register(
        nameof(HasMessage), typeof(bool), typeof(ToolShell), new PropertyMetadata(false));

    public bool HasMessage
    {
        get => (bool)GetValue(HasMessageProperty);
        set => SetValue(HasMessageProperty, value);
    }

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(ToolShell), new PropertyMetadata(false));

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
}
