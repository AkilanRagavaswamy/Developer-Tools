using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>A span of a line to highlight: the words that changed within a changed line.</summary>
public readonly record struct HighlightSpan(int Start, int Length);

/// <summary>One row of the text diff, as either view shows it.</summary>
/// <remarks>A plain class in a file of its own, for the same XAML-tooling reason as <see cref="JsonDiffRowView"/>.</remarks>
public sealed class TextDiffRowView
{
    public string LeftLine { get; init; } = string.Empty;

    public string LeftText { get; init; } = string.Empty;

    public string RightLine { get; init; } = string.Empty;

    public string RightText { get; init; } = string.Empty;

    public Brush? LeftBackground { get; init; }

    public Brush? RightBackground { get; init; }

    public IReadOnlyList<HighlightSpan> LeftSpans { get; init; } = [];

    public IReadOnlyList<HighlightSpan> RightSpans { get; init; } = [];

    /// <summary>Inline view: "+", "−" or a space.</summary>
    public string Marker { get; init; } = " ";

    public string Text { get; init; } = string.Empty;

    public Brush? Background { get; init; }

    public IReadOnlyList<HighlightSpan> Spans { get; init; } = [];

    /// <summary>Inline view: removed words on a "−" row, added words on a "+" row.</summary>
    public Brush? SpanBrush { get; init; }

    /// <summary>Which difference the row belongs to, or -1 where the two sides agree.</summary>
    public int DifferenceIndex { get; init; } = -1;
}

/// <summary>
/// Paints highlight spans behind a <see cref="TextBlock"/>'s text with <see cref="TextHighlighter"/>,
/// so a changed line shows exactly which words changed without splitting it into runs.
/// </summary>
public static class DiffHighlight
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(object), typeof(DiffHighlight), new PropertyMetadata(null, OnChanged));

    public static object? GetSpans(DependencyObject d) => d.GetValue(SpansProperty);

    public static void SetSpans(DependencyObject d, object? value) => d.SetValue(SpansProperty, value);

    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(DiffHighlight), new PropertyMetadata(null, OnChanged));

    public static Brush? GetBrush(DependencyObject d) => (Brush?)d.GetValue(BrushProperty);

    public static void SetBrush(DependencyObject d, Brush? value) => d.SetValue(BrushProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.TextHighlighters.Clear();

        if (GetSpans(block) is not IReadOnlyList<HighlightSpan> { Count: > 0 } spans || GetBrush(block) is not { } brush)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = brush };
        foreach (var span in spans)
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        }

        block.TextHighlighters.Add(highlighter);
    }
}
