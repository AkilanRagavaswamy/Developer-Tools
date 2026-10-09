using System.Text;
using DevTools.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>
/// Shows a <see cref="TextDiffResult"/> side by side or inline, with the words that changed
/// inside a changed line highlighted, and steps through the differences one at a time.
/// </summary>
public sealed partial class TextDiffView : UserControl
{
    private readonly List<int> _differenceStarts = [];
    private int _current = -1;

    public TextDiffView()
    {
        InitializeComponent();

        // Row colours come from the theme, so they have to be rebuilt when it changes.
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(TextDiffResult), typeof(TextDiffView), new PropertyMetadata(null, (d, _) => ((TextDiffView)d).Rebuild()));

    public TextDiffResult? Result
    {
        get => (TextDiffResult?)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    public static readonly DependencyProperty IsInlineProperty = DependencyProperty.Register(
        nameof(IsInline), typeof(bool), typeof(TextDiffView), new PropertyMetadata(false, (d, _) => ((TextDiffView)d).Rebuild()));

    public bool IsInline
    {
        get => (bool)GetValue(IsInlineProperty);
        set => SetValue(IsInlineProperty, value);
    }

    public static readonly DependencyProperty LeftHeaderProperty = DependencyProperty.Register(
        nameof(LeftHeader), typeof(string), typeof(TextDiffView), new PropertyMetadata("Original"));

    public string LeftHeader
    {
        get => (string)GetValue(LeftHeaderProperty);
        set => SetValue(LeftHeaderProperty, value);
    }

    public static readonly DependencyProperty RightHeaderProperty = DependencyProperty.Register(
        nameof(RightHeader), typeof(string), typeof(TextDiffView), new PropertyMetadata("Changed"));

    public string RightHeader
    {
        get => (string)GetValue(RightHeaderProperty);
        set => SetValue(RightHeaderProperty, value);
    }

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(string), typeof(TextDiffView), new PropertyMetadata(string.Empty));

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(string), typeof(TextDiffView), new PropertyMetadata(string.Empty));

    public string Position
    {
        get => (string)GetValue(PositionProperty);
        private set => SetValue(PositionProperty, value);
    }

    private void Rebuild()
    {
        _differenceStarts.Clear();
        _current = -1;

        var result = Result;
        SplitList.Visibility = IsInline ? Visibility.Collapsed : Visibility.Visible;
        SplitHeader.Visibility = IsInline ? Visibility.Collapsed : Visibility.Visible;
        InlineList.Visibility = IsInline ? Visibility.Visible : Visibility.Collapsed;

        if (result is null)
        {
            SplitList.ItemsSource = null;
            InlineList.ItemsSource = null;
            Position = string.Empty;
            return;
        }

        var added = AddedSwatch.Background;
        var removed = RemovedSwatch.Background;
        var addedStrong = AddedStrongSwatch.Background;
        var removedStrong = RemovedStrongSwatch.Background;

        var rows = new List<TextDiffRowView>(result.Segments.Count);
        var difference = -1;
        var inDifference = false;

        foreach (var segment in result.Segments)
        {
            var changed = segment.Kind != DiffChangeKind.Unchanged;
            if (changed && !inDifference)
            {
                difference++;
                _differenceStarts.Add(rows.Count);
            }

            inDifference = changed;
            var index = changed ? difference : -1;

            var (leftSpans, rightSpans) = segment.Kind == DiffChangeKind.Modified
                ? WordSpans(segment)
                : ([], []);

            var left = segment.LeftLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var right = segment.RightLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            if (!IsInline)
            {
                rows.Add(new TextDiffRowView
                {
                    LeftLine = left,
                    RightLine = right,
                    LeftText = segment.LeftText ?? string.Empty,
                    RightText = segment.RightText ?? string.Empty,
                    LeftBackground = segment.Kind is DiffChangeKind.Deleted or DiffChangeKind.Modified ? removed : null,
                    RightBackground = segment.Kind is DiffChangeKind.Inserted or DiffChangeKind.Modified ? added : null,
                    LeftSpans = leftSpans,
                    RightSpans = rightSpans,
                    DifferenceIndex = index,
                });

                continue;
            }

            switch (segment.Kind)
            {
                case DiffChangeKind.Unchanged:
                    rows.Add(new TextDiffRowView { LeftLine = left, RightLine = right, Text = segment.LeftText ?? string.Empty });
                    break;

                case DiffChangeKind.Deleted:
                    rows.Add(new TextDiffRowView { LeftLine = left, Marker = "−", Text = segment.LeftText ?? string.Empty, Background = removed, DifferenceIndex = index });
                    break;

                case DiffChangeKind.Inserted:
                    rows.Add(new TextDiffRowView { RightLine = right, Marker = "+", Text = segment.RightText ?? string.Empty, Background = added, DifferenceIndex = index });
                    break;

                default:
                    rows.Add(new TextDiffRowView
                    {
                        LeftLine = left, Marker = "−", Text = segment.LeftText ?? string.Empty, Background = removed,
                        Spans = leftSpans, SpanBrush = removedStrong, DifferenceIndex = index,
                    });
                    rows.Add(new TextDiffRowView
                    {
                        RightLine = right, Marker = "+", Text = segment.RightText ?? string.Empty, Background = added,
                        Spans = rightSpans, SpanBrush = addedStrong, DifferenceIndex = index,
                    });
                    break;
            }
        }

        if (IsInline)
        {
            SplitList.ItemsSource = null;
            InlineList.ItemsSource = rows;
        }
        else
        {
            InlineList.ItemsSource = null;
            SplitList.ItemsSource = rows;
        }

        Position = _differenceStarts.Count == 0
            ? "no differences"
            : _differenceStarts.Count == 1 ? "1 difference" : $"{_differenceStarts.Count:N0} differences";
    }

    /// <summary>
    /// The changed words of a modified line, as spans on each side. Deleted runs belong to the
    /// left line and inserted runs to the right; if the runs do not rebuild the line exactly,
    /// no spans are shown rather than misplaced ones.
    /// </summary>
    private static (IReadOnlyList<HighlightSpan> Left, IReadOnlyList<HighlightSpan> Right) WordSpans(DiffSegment segment)
    {
        if (segment.WordDiff.Count == 0)
        {
            return ([], []);
        }

        var left = new StringBuilder();
        var right = new StringBuilder();
        var leftSpans = new List<HighlightSpan>();
        var rightSpans = new List<HighlightSpan>();

        foreach (var run in segment.WordDiff)
        {
            switch (run.Kind)
            {
                case DiffChangeKind.Deleted:
                    leftSpans.Add(new HighlightSpan(left.Length, run.Text.Length));
                    left.Append(run.Text);
                    break;

                case DiffChangeKind.Inserted:
                    rightSpans.Add(new HighlightSpan(right.Length, run.Text.Length));
                    right.Append(run.Text);
                    break;

                default:
                    left.Append(run.Text);
                    right.Append(run.Text);
                    break;
            }
        }

        var leftOk = string.Equals(left.ToString(), segment.LeftText, StringComparison.Ordinal);
        var rightOk = string.Equals(right.ToString(), segment.RightText, StringComparison.Ordinal);
        return (leftOk ? leftSpans : [], rightOk ? rightSpans : []);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Step(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int delta)
    {
        if (_differenceStarts.Count == 0)
        {
            return;
        }

        _current = _current < 0
            ? (delta > 0 ? 0 : _differenceStarts.Count - 1)
            : ((_current + delta) % _differenceStarts.Count + _differenceStarts.Count) % _differenceStarts.Count;

        var list = IsInline ? InlineList : SplitList;
        if (list.ItemsSource is IReadOnlyList<TextDiffRowView> rows)
        {
            list.ScrollIntoView(rows[_differenceStarts[_current]], ScrollIntoViewAlignment.Leading);
        }

        Position = $"{_current + 1} of {_differenceStarts.Count:N0}";
    }
}
