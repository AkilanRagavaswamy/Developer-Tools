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

        Loaded += (_, _) => _chrome.SetOptions(OptionsContent as FrameworkElement, OptionsWidth);

        // Only if it is still ours. Navigation loads the incoming page before it unloads the
        // outgoing one, so an unconditional clear here would throw away the new tool's row.
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_chrome.Options, OptionsContent))
            {
                _chrome.SetOptions(null, 0);
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
    /// The tool's options row. Leave it unset and the title bar carries nothing but the name.
    /// </summary>
    /// <remarks>
    /// Declared by the page but never parented here — see <see cref="Services.IToolChrome"/>.
    /// The row carries no text labels, which is what keeps it narrow enough for the title bar;
    /// it keeps a `*` column where it has one, and the shell gives it the leftover width, so a
    /// field like the formatter's JSONPath box still stretches.
    /// </remarks>
    public static readonly DependencyProperty OptionsContentProperty = DependencyProperty.Register(
        nameof(OptionsContent), typeof(object), typeof(ToolShell), new PropertyMetadata(null));

    public object? OptionsContent
    {
        get => GetValue(OptionsContentProperty);
        set => SetValue(OptionsContentProperty, value);
    }

    /// <summary>
    /// How wide the options row needs to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shell puts the row in the title bar when this much room is free beside the tool's
    /// name and the window commands, and in a band of its own when it is not. Declared here
    /// rather than measured because a row that has never been shown measures to nothing — see
    /// <see cref="Services.IToolChrome.OptionsWidth"/>.
    /// </para>
    /// <para>
    /// Add up what the row contains at its narrowest: each control's width or MinWidth, plus
    /// the column spacing between them. Round up a little. Too small and the row goes to the
    /// title bar and is clipped; too large and it never goes up at all, which is only a wasted
    /// row. Err high.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty OptionsWidthProperty = DependencyProperty.Register(
        nameof(OptionsWidth), typeof(double), typeof(ToolShell), new PropertyMetadata(0d));

    public double OptionsWidth
    {
        get => (double)GetValue(OptionsWidthProperty);
        set => SetValue(OptionsWidthProperty, value);
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
