using DevTools.Core.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>
/// Two JSON documents laid against each other, a row at a time (FR-J28).
/// </summary>
/// <remarks>
/// The tree view answers "what changed"; this one answers "what do these two documents look
/// like next to each other", which is the question you have when you are reading a payload that
/// should have matched and did not. Differences are counted by kind, each kind can be hidden,
/// and the navigator walks whatever is left one difference at a time.
/// </remarks>
public sealed partial class JsonDiffView : UserControl
{
    public JsonDiffView()
    {
        InitializeComponent();

        // Nothing is built until the control is loaded. A checkbox that starts ticked raises
        // Checked while the XAML is still being read, and the handler for that runs before the
        // named fields it reads have been assigned — which the parser reports, confusingly, as
        // a failure to set IsChecked.
        Loaded += (_, _) =>
        {
            _ready = true;
            Rebuild();
        };

        // The brushes are resolved per theme, so the rows are rebuilt when it changes.
        ActualThemeChanged += (_, _) => Rebuild();
    }

    private bool _ready;

    // ------------------------------------------------------------ what to show

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(JsonDiffLayout), typeof(JsonDiffView),
        new PropertyMetadata(null, static (d, _) => ((JsonDiffView)d).Rebuild()));

    public JsonDiffLayout? Layout
    {
        get => (JsonDiffLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static readonly DependencyProperty LeftHeaderProperty = DependencyProperty.Register(
        nameof(LeftHeader), typeof(string), typeof(JsonDiffView), new PropertyMetadata("Original"));

    public string LeftHeader
    {
        get => (string)GetValue(LeftHeaderProperty);
        set => SetValue(LeftHeaderProperty, value);
    }

    public static readonly DependencyProperty RightHeaderProperty = DependencyProperty.Register(
        nameof(RightHeader), typeof(string), typeof(JsonDiffView), new PropertyMetadata("Changed"));

    public string RightHeader
    {
        get => (string)GetValue(RightHeaderProperty);
        set => SetValue(RightHeaderProperty, value);
    }

    // ------------------------------------------------------------ row colours

    /// <summary>
    /// The four row colours, set from markup so they re-resolve against this element's theme.
    /// </summary>
    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(JsonDiffView),
            new PropertyMetadata(null, static (d, _) => ((JsonDiffView)d).Rebuild()));

    public static readonly DependencyProperty MissingBrushProperty = RegisterBrush(nameof(MissingBrush));

    public Brush? MissingBrush
    {
        get => (Brush?)GetValue(MissingBrushProperty);
        set => SetValue(MissingBrushProperty, value);
    }

    public static readonly DependencyProperty TypeBrushProperty = RegisterBrush(nameof(TypeBrush));

    public Brush? TypeBrush
    {
        get => (Brush?)GetValue(TypeBrushProperty);
        set => SetValue(TypeBrushProperty, value);
    }

    public static readonly DependencyProperty ValueBrushProperty = RegisterBrush(nameof(ValueBrush));

    public Brush? ValueBrush
    {
        get => (Brush?)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public static readonly DependencyProperty CurrentBrushProperty = RegisterBrush(nameof(CurrentBrush));

    public Brush? CurrentBrush
    {
        get => (Brush?)GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    // ------------------------------------------------------------ header text

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        private set => SetValue(SummaryProperty, value);
    }

    public static readonly DependencyProperty MissingLabelProperty = DependencyProperty.Register(
        nameof(MissingLabel), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string MissingLabel
    {
        get => (string)GetValue(MissingLabelProperty);
        private set => SetValue(MissingLabelProperty, value);
    }

    public static readonly DependencyProperty TypeLabelProperty = DependencyProperty.Register(
        nameof(TypeLabel), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string TypeLabel
    {
        get => (string)GetValue(TypeLabelProperty);
        private set => SetValue(TypeLabelProperty, value);
    }

    public static readonly DependencyProperty ValueLabelProperty = DependencyProperty.Register(
        nameof(ValueLabel), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string ValueLabel
    {
        get => (string)GetValue(ValueLabelProperty);
        private set => SetValue(ValueLabelProperty, value);
    }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string Position
    {
        get => (string)GetValue(PositionProperty);
        private set => SetValue(PositionProperty, value);
    }

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(JsonDiffView), new PropertyMetadata(string.Empty));

    public string Note
    {
        get => (string)GetValue(NoteProperty);
        private set => SetValue(NoteProperty, value);
    }

    public static readonly DependencyProperty HasNoteProperty = DependencyProperty.Register(
        nameof(HasNote), typeof(Visibility), typeof(JsonDiffView), new PropertyMetadata(Visibility.Collapsed));

    public Visibility HasNote
    {
        get => (Visibility)GetValue(HasNoteProperty);
        private set => SetValue(HasNoteProperty, value);
    }

    // ------------------------------------------------------------ building

    private readonly List<JsonDiffRowView> _rows = [];
    private IReadOnlyList<JsonDifference> _visible = [];
    private int _current = -1;

    private void Rebuild()
    {
        if (!_ready)
        {
            return;
        }

        var layout = Layout ?? JsonDiffLayout.Empty;

        Summary = layout.Summary;
        MissingLabel = Label(layout.MissingProperties, "missing property", "missing properties");
        TypeLabel = Label(layout.IncorrectTypes, "incorrect type", "incorrect types");
        ValueLabel = Label(layout.UnequalValues, "unequal value", "unequal values");

        _visible = [.. layout.Differences.Where(Allowed)];
        _current = _visible.Count > 0 ? 0 : -1;

        _rows.Clear();

        foreach (var row in layout.Rows)
        {
            _rows.Add(new JsonDiffRowView
            {
                LeftLine = Number(row.LeftLine),
                LeftText = row.LeftText,
                RightLine = Number(row.RightLine),
                RightText = row.RightText,
                RowBackground = BrushFor(row),
                DifferenceIndex = row.DifferenceIndex,
            });
        }

        RowList.ItemsSource = null;
        RowList.ItemsSource = _rows;

        UpdateCurrent(scroll: false);
    }

    private static string Label(int count, string one, string many) =>
        $"{count} {(count == 1 ? one : many)}";

    private static string Number(int? line) =>
        line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private bool Allowed(JsonDifference difference) => difference.Category switch
    {
        JsonDiffCategory.MissingProperty => MissingFilter.IsChecked == true,
        JsonDiffCategory.IncorrectType => TypeFilter.IsChecked == true,
        JsonDiffCategory.UnequalValue => ValueFilter.IsChecked == true,
        _ => false,
    };

    /// <summary>
    /// The colour a row is painted, or none when it matches.
    /// </summary>
    /// <remarks>
    /// A hidden category paints nothing: unticking "unequal values" should quieten those rows
    /// as well as take them out of the count, or the filter only half works.
    /// </remarks>
    private Brush? BrushFor(JsonDiffRow row) => row.Category switch
    {
        JsonDiffCategory.MissingProperty when MissingFilter.IsChecked == true => MissingBrush,
        JsonDiffCategory.IncorrectType when TypeFilter.IsChecked == true => TypeBrush,
        JsonDiffCategory.UnequalValue when ValueFilter.IsChecked == true => ValueBrush,
        _ => null,
    };

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Rebuild();

    // ------------------------------------------------------------ navigation

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Step(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int delta)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        _current = ((_current + delta) % _visible.Count + _visible.Count) % _visible.Count;
        UpdateCurrent(scroll: true);
    }

    /// <summary>Clicking a difference row makes it the current one.</summary>
    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not JsonDiffRowView row || row.DifferenceIndex < 0)
        {
            return;
        }

        for (var i = 0; i < _visible.Count; i++)
        {
            if (_visible[i].Index == row.DifferenceIndex)
            {
                _current = i;
                UpdateCurrent(scroll: false);
                return;
            }
        }
    }

    private void UpdateCurrent(bool scroll)
    {
        if (_current < 0 || _current >= _visible.Count)
        {
            Position = _visible.Count == 0 ? "none" : string.Empty;
            Note = string.Empty;
            HasNote = Visibility.Collapsed;
            return;
        }

        var difference = _visible[_current];

        Position = $"{_current + 1} of {_visible.Count}";
        Note = difference.Note;
        HasNote = Visibility.Visible;

        if (scroll && difference.Row < _rows.Count)
        {
            RowList.ScrollIntoView(_rows[difference.Row], ScrollIntoViewAlignment.Leading);
        }
    }
}
