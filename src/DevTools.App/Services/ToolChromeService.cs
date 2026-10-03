using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;

namespace DevTools.App.Services;

/// <summary>
/// Holds the tool the content frame is currently showing, so the title bar can carry that
/// tool's name and its favourite, reset and run commands.
/// </summary>
/// <remarks>
/// The page header used to sit above every tool: an icon, a title, a one-line description and
/// three buttons, about 90 px of chrome repeated on six pages. Folding it into the 48 px title
/// bar hands that space back to the tool, but the title bar lives in the shell and the commands
/// live in the page — so something has to carry one to the other. This is that something, and
/// it is deliberately the only coupling between them.
/// </remarks>
public interface IToolChrome
{
    /// <summary>The tool on screen, or <see langword="null"/> on Home and Settings.</summary>
    ToolViewModelBase? Active { get; }

    event EventHandler? ActiveChanged;

    void SetActive(ToolViewModelBase? tool);

    /// <summary>
    /// The active tool's options row, on its way to the title bar (FR-T01).
    /// </summary>
    /// <remarks>
    /// The page declares the row, the shell decides where it is shown, and the shell is the
    /// only thing that ever parents it — a <see cref="UIElement"/> has one parent, so two
    /// controls both trying to host it is an exception waiting to happen.
    /// </remarks>
    FrameworkElement? Options { get; }

    /// <summary>What that row needs across.</summary>
    /// <remarks>
    /// Declared by the page rather than measured. A row that has not yet been in a visual tree
    /// has no templates applied and measures to almost nothing, and measuring it after a layout
    /// pass means it is already somewhere — which is the question. See
    /// <c>ToolShell.OptionsWidth</c> for how a page arrives at the number.
    /// </remarks>
    double OptionsWidth { get; }


    event EventHandler? OptionsChanged;

    void SetOptions(FrameworkElement? options, double width);
}

public sealed class ToolChromeService : IToolChrome
{
    public ToolViewModelBase? Active { get; private set; }

    public event EventHandler? ActiveChanged;

    public void SetActive(ToolViewModelBase? tool)
    {
        if (ReferenceEquals(Active, tool))
        {
            return;
        }

        Active = tool;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public FrameworkElement? Options { get; private set; }

    public double OptionsWidth { get; private set; }


    public event EventHandler? OptionsChanged;

    public void SetOptions(FrameworkElement? options, double width)
    {
        if (ReferenceEquals(Options, options))
        {
            return;
        }

        Options = options;
        OptionsWidth = width;
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
