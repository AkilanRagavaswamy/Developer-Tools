using DevTools.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>
/// A read-only, selectable text view with syntax colouring (FR-T11).
/// </summary>
/// <remarks>
/// <para>
/// Only <em>output</em> is coloured. Editable panes stay a plain <see cref="TextBox"/>, because
/// colouring an editable surface in WinUI means either a <c>RichEditBox</c> — which fights every
/// keystroke and mangles pasted text — or reimplementing text editing. Colouring what the user
/// reads and leaving what they type alone gets the whole benefit for none of that cost.
/// </para>
/// <para>
/// Colours come from theme resources, so High Contrast and dark mode are handled by the same
/// dictionary as everything else rather than by hard-coded hex values.
/// </para>
/// </remarks>
public sealed partial class ColourisedTextPresenter : ContentControl
{
    private readonly RichTextBlock _block;

    public ColourisedTextPresenter()
    {
        _block = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            LineHeight = 18,
        };

        Content = _block;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        ActualThemeChanged += (_, _) => Rebuild();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ColourisedTextPresenter),
        new PropertyMetadata(string.Empty, static (d, _) => ((ColourisedTextPresenter)d).Rebuild()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty GrammarProperty = DependencyProperty.Register(
        nameof(Grammar), typeof(SyntaxLanguage), typeof(ColourisedTextPresenter),
        new PropertyMetadata(SyntaxLanguage.None, static (d, _) => ((ColourisedTextPresenter)d).Rebuild()));

    public SyntaxLanguage Grammar
    {
        get => (SyntaxLanguage)GetValue(GrammarProperty);
        set => SetValue(GrammarProperty, value);
    }

    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(ColourisedTextPresenter),
        new PropertyMetadata(false, static (d, e) =>
            ((ColourisedTextPresenter)d)._block.TextWrapping =
                (bool)e.NewValue ? TextWrapping.Wrap : TextWrapping.NoWrap));

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize), typeof(double), typeof(ColourisedTextPresenter),
        new PropertyMetadata(13.0, static (d, e) =>
        {
            var presenter = (ColourisedTextPresenter)d;
            presenter._block.FontSize = (double)e.NewValue;
            presenter._block.LineHeight = (double)e.NewValue * 1.45;
        }));

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }


    // ---------------------------------------------------------------- syntax brushes

    /// <summary>
    /// The eight token colours, as properties the host sets with <c>{ThemeResource}</c>.
    /// </summary>
    /// <remarks>
    /// They were being read from <c>Application.Current.Resources</c>, which resolves against
    /// the <em>application's</em> requested theme. The theme switch sets
    /// <see cref="FrameworkElement.RequestedTheme"/> on the window's root element instead, so
    /// that lookup never changed: switching to light left the output pane painted in the dark
    /// palette, which on a white background is very nearly invisible. A <c>{ThemeResource}</c>
    /// on a property re-resolves when the element's actual theme changes, which is the whole
    /// point of it.
    /// </remarks>
    public static readonly DependencyProperty PropertyBrushProperty = Register(nameof(PropertyBrush));

    public Brush? PropertyBrush
    {
        get => (Brush?)GetValue(PropertyBrushProperty);
        set => SetValue(PropertyBrushProperty, value);
    }

    public static readonly DependencyProperty StringBrushProperty = Register(nameof(StringBrush));

    public Brush? StringBrush
    {
        get => (Brush?)GetValue(StringBrushProperty);
        set => SetValue(StringBrushProperty, value);
    }

    public static readonly DependencyProperty NumberBrushProperty = Register(nameof(NumberBrush));

    public Brush? NumberBrush
    {
        get => (Brush?)GetValue(NumberBrushProperty);
        set => SetValue(NumberBrushProperty, value);
    }

    public static readonly DependencyProperty KeywordBrushProperty = Register(nameof(KeywordBrush));

    public Brush? KeywordBrush
    {
        get => (Brush?)GetValue(KeywordBrushProperty);
        set => SetValue(KeywordBrushProperty, value);
    }

    public static readonly DependencyProperty CommentBrushProperty = Register(nameof(CommentBrush));

    public Brush? CommentBrush
    {
        get => (Brush?)GetValue(CommentBrushProperty);
        set => SetValue(CommentBrushProperty, value);
    }

    public static readonly DependencyProperty PunctuationBrushProperty = Register(nameof(PunctuationBrush));

    public Brush? PunctuationBrush
    {
        get => (Brush?)GetValue(PunctuationBrushProperty);
        set => SetValue(PunctuationBrushProperty, value);
    }

    public static readonly DependencyProperty ElementBrushProperty = Register(nameof(ElementBrush));

    public Brush? ElementBrush
    {
        get => (Brush?)GetValue(ElementBrushProperty);
        set => SetValue(ElementBrushProperty, value);
    }

    public static readonly DependencyProperty AttributeBrushProperty = Register(nameof(AttributeBrush));

    public Brush? AttributeBrush
    {
        get => (Brush?)GetValue(AttributeBrushProperty);
        set => SetValue(AttributeBrushProperty, value);
    }

    /// <summary>Every brush repaints the text when it changes, which is what a theme switch does.</summary>
    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(
            name, typeof(Brush), typeof(ColourisedTextPresenter),
            new PropertyMetadata(null, static (d, _) => ((ColourisedTextPresenter)d).Rebuild()));


    // ---------------------------------------------------------------- find highlighting

    /// <summary>The colour behind a match, set from markup like the token colours.</summary>
    public static readonly DependencyProperty MatchHighlightBrushProperty =
        DependencyProperty.Register(
            nameof(MatchHighlightBrush), typeof(Brush), typeof(ColourisedTextPresenter),
            new PropertyMetadata(null));

    public Brush? MatchHighlightBrush
    {
        get => (Brush?)GetValue(MatchHighlightBrushProperty);
        set => SetValue(MatchHighlightBrushProperty, value);
    }

    /// <summary>
    /// Marks every match.
    /// </summary>
    /// <remarks>
    /// Through <c>RichTextBlock.TextHighlighters</c>, which is the only way: a <c>Run</c> has no
    /// background in WinUI, so highlighting by splitting the coloured runs is not available even
    /// in principle. The ranges are character offsets into the same string the tokenizer saw, so
    /// they line up with whatever the caller searched.
    /// </remarks>
    public void Highlight(IReadOnlyList<int> starts, int length)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ClearHighlights();

        if (starts.Count == 0 || length <= 0 || MatchHighlightBrush is null)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = MatchHighlightBrush };

        foreach (var start in starts)
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = length });
        }

        _block.TextHighlighters.Add(highlighter);
    }

    public void ClearHighlights() => _block.TextHighlighters.Clear();

    /// <summary>
    /// Scrolls the host so the character at <paramref name="offset"/> is on screen.
    /// </summary>
    /// <remarks>
    /// <c>RichTextBlock</c> will not report where a character ended up, so the position is
    /// worked out from the line the offset falls on and the line height the block was given.
    /// With wrapping on a long line that lands a little short, which is why the target sits a
    /// third of the way down the viewport rather than at the top — near enough either way, and
    /// the match is highlighted so the eye finds it from there.
    /// </remarks>
    public void ScrollTo(ScrollViewer host, int offset)
    {
        ArgumentNullException.ThrowIfNull(host);

        var text = Text;

        if (string.IsNullOrEmpty(text) || offset <= 0)
        {
            host.ChangeView(null, 0, null);
            return;
        }

        var line = 0;
        var limit = Math.Min(offset, text.Length);

        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        var target = (line * _block.LineHeight) - (host.ViewportHeight / 3);
        host.ChangeView(null, Math.Max(0, target), null);
    }

    private void Rebuild()
    {
        _block.Blocks.Clear();

        var text = Text;

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var tokens = SyntaxTokenizer.Tokenize(text, Grammar);
        var paragraph = new Paragraph();

        if (tokens.Count == 0)
        {
            paragraph.Inlines.Add(new Run { Text = text });
            _block.Blocks.Add(paragraph);
            return;
        }

        foreach (var token in tokens)
        {
            if (token.Length <= 0 || token.Start + token.Length > text.Length)
            {
                continue;
            }

            var run = new Run { Text = text.Substring(token.Start, token.Length) };

            if (BrushFor(token.Kind) is { } brush)
            {
                run.Foreground = brush;
            }

            paragraph.Inlines.Add(run);
        }

        _block.Blocks.Add(paragraph);
    }

    private Brush? BrushFor(TokenKind kind) => kind switch
    {
        TokenKind.PropertyName => PropertyBrush,
        TokenKind.String => StringBrush,
        TokenKind.Number => NumberBrush,
        TokenKind.Keyword => KeywordBrush,
        TokenKind.Comment => CommentBrush,
        TokenKind.Punctuation => PunctuationBrush,
        TokenKind.ElementName => ElementBrush,
        TokenKind.AttributeName => AttributeBrush,
        _ => null,
    };
}
