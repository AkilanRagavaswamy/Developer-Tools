using System.Text;
using System.Windows.Input;
using DevTools.App.Services;
using DevTools.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DevTools.App.Controls;

/// <summary>
/// The one text surface every tool uses, for both input and output. Centralising it is what
/// makes FR-T02, FR-T03, FR-T09 and FR-T10 true of all 32 tools at once: the same toolbar,
/// the same counters, the same editor settings, the same drag-and-drop.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private readonly ISettingsService _settings;
    private ScrollViewer? _editorScrollViewer;
    private bool _suppressTextSync;
    private double _characterWidth;
    private DispatcherTimer? _toastTimer;

    public CodeEditor()
    {
        InitializeComponent();

        _settings = App.GetService<ISettingsService>();

        // Ctrl+F on the editor itself, so it wins over the shell's tool search whenever the
        // caret is in a pane. Routing runs from the focused element up, which is exactly the
        // behaviour wanted: in the text it finds in the text, anywhere else it finds a tool.
        var find = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };

        find.Invoked += (_, args) =>
        {
            ShowFind();
            args.Handled = true;
        };

        KeyboardAccelerators.Add(find);

        // Otherwise hovering anywhere over the editor shows a stray "Ctrl+F" tooltip.
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ------------------------------------------------------------ properties

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CodeEditor),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(CodeEditor), new PropertyMetadata(string.Empty));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(CodeEditor),
        new PropertyMetadata(string.Empty, OnPlaceholderChanged));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public static readonly DependencyProperty FooterTextProperty = DependencyProperty.Register(
        nameof(FooterText), typeof(string), typeof(CodeEditor), new PropertyMetadata(string.Empty));

    public string FooterText
    {
        get => (string)GetValue(FooterTextProperty);
        set => SetValue(FooterTextProperty, value);
    }

    /// <summary>
    /// Hides the editor's own header strip. Used where the surrounding card already names the
    /// pane, so the label is not printed twice a few pixels apart.
    /// </summary>
    public static readonly DependencyProperty ShowHeaderProperty = DependencyProperty.Register(
        nameof(ShowHeader), typeof(Visibility), typeof(CodeEditor),
        new PropertyMetadata(Visibility.Visible, OnShowHeaderChanged));

    public Visibility ShowHeader
    {
        get => (Visibility)GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    private static void OnShowHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeEditor)d).HeaderRow.Visibility = (Visibility)e.NewValue;

    /// <summary>
    /// Whether the editor draws its own card border. Turned off where the editor already sits
    /// inside a card, which would otherwise show two borders a pixel apart.
    /// </summary>
    public static readonly DependencyProperty IsFramedProperty = DependencyProperty.Register(
        nameof(IsFramed), typeof(bool), typeof(CodeEditor),
        new PropertyMetadata(true, OnIsFramedChanged));

    public bool IsFramed
    {
        get => (bool)GetValue(IsFramedProperty);
        set => SetValue(IsFramedProperty, value);
    }

    private static void OnIsFramedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        var framed = (bool)e.NewValue;

        editor.EditorFrame.BorderThickness = framed ? new Thickness(1) : new Thickness(0);
        editor.EditorFrame.CornerRadius = framed
            ? (CornerRadius)Application.Current.Resources["DevToolsCardCornerRadius"]
            : new CornerRadius(0);
    }

    public static readonly DependencyProperty ShowFooterProperty = DependencyProperty.Register(
        nameof(ShowFooter), typeof(Visibility), typeof(CodeEditor),
        new PropertyMetadata(Visibility.Visible));

    public Visibility ShowFooter
    {
        get => (Visibility)GetValue(ShowFooterProperty);
        set => SetValue(ShowFooterProperty, value);
    }

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(CodeEditor),
        new PropertyMetadata(false, OnIsReadOnlyChanged));

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>
    /// The grammar to colour a read-only pane with (FR-T11).
    /// </summary>
    /// <remarks>
    /// Setting it on an editable pane does nothing: colouring applies to output only, so typing
    /// never fights a formatter. See <see cref="ColourisedTextPresenter"/> for why.
    /// </remarks>
    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.Register(
        nameof(Syntax), typeof(SyntaxLanguage), typeof(CodeEditor),
        new PropertyMetadata(SyntaxLanguage.None, OnSyntaxChanged));

    public SyntaxLanguage Syntax
    {
        get => (SyntaxLanguage)GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    public static readonly DependencyProperty ExtraHeaderContentProperty = DependencyProperty.Register(
        nameof(ExtraHeaderContent), typeof(object), typeof(CodeEditor), new PropertyMetadata(null));

    public object? ExtraHeaderContent
    {
        get => GetValue(ExtraHeaderContentProperty);
        set => SetValue(ExtraHeaderContentProperty, value);
    }

    public static readonly DependencyProperty AllowFileDropProperty = DependencyProperty.Register(
        nameof(AllowFileDrop), typeof(bool), typeof(CodeEditor), new PropertyMetadata(true));

    /// <summary>Dropping a text file onto the surface loads it (FR-T10).</summary>
    public bool AllowFileDrop
    {
        get => (bool)GetValue(AllowFileDropProperty);
        set => SetValue(AllowFileDropProperty, value);
    }

    public static readonly DependencyProperty PasteCommandProperty = DependencyProperty.Register(
        nameof(PasteCommand), typeof(ICommand), typeof(CodeEditor), new PropertyMetadata(null, OnCommandChanged));

    public ICommand? PasteCommand
    {
        get => (ICommand?)GetValue(PasteCommandProperty);
        set => SetValue(PasteCommandProperty, value);
    }

    public static readonly DependencyProperty OpenFileCommandProperty = DependencyProperty.Register(
        nameof(OpenFileCommand), typeof(ICommand), typeof(CodeEditor), new PropertyMetadata(null, OnCommandChanged));

    public ICommand? OpenFileCommand
    {
        get => (ICommand?)GetValue(OpenFileCommandProperty);
        set => SetValue(OpenFileCommandProperty, value);
    }

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(CodeEditor), new PropertyMetadata(null, OnCommandChanged));

    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    public static readonly DependencyProperty CopyCommandProperty = DependencyProperty.Register(
        nameof(CopyCommand), typeof(ICommand), typeof(CodeEditor), new PropertyMetadata(null, OnCommandChanged));

    public ICommand? CopyCommand
    {
        get => (ICommand?)GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    public static readonly DependencyProperty SaveCommandProperty = DependencyProperty.Register(
        nameof(SaveCommand), typeof(ICommand), typeof(CodeEditor), new PropertyMetadata(null, OnCommandChanged));

    public ICommand? SaveCommand
    {
        get => (ICommand?)GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    /// <summary>Raised when a file is dropped, for tools that want the bytes rather than text.</summary>
    public event EventHandler<byte[]>? FileDropped;

    // ------------------------------------------------------------ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings.SettingChanged += OnSettingChanged;
        ApplyEditorSettings();
        UpdateCommandVisibility();
        UpdatePlaceholder();
        AttachScrollViewer();
        UpdateGutter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _settings.SettingChanged -= OnSettingChanged;
        ColourHost.ViewChanged -= OnEditorViewChanged;

        if (_editorScrollViewer is not null)
        {
            _editorScrollViewer.ViewChanged -= OnEditorViewChanged;
            _editorScrollViewer = null;
        }

        _toastTimer?.Stop();
        _toastTimer = null;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        ApplyEditorSettings();
        UpdateGutter();
    }

    /// <summary>Font size, wrap and gutter follow Settings for every editor at once (FR-T09).</summary>
    private void ApplyEditorSettings()
    {
        Editor.FontSize = _settings.EditorFontSize;
        GutterText.FontSize = _settings.EditorFontSize;

        // The prompt is drawn in the editor's own font, so it occupies the exact space the
        // text it invites will occupy.
        PlaceholderLabel.FontFamily = Editor.FontFamily;
        PlaceholderLabel.FontSize = _settings.EditorFontSize;
        AlignPlaceholder();

        Editor.TextWrapping = _settings.EditorWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(
            Editor,
            _settings.EditorWordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        GutterHost.Visibility = _settings.EditorLineNumbers ? Visibility.Visible : Visibility.Collapsed;

        UpdateColourisedVisibility();

        _characterWidth = 0;
    }

    private void AttachScrollViewer()
    {
        // The coloured pane's scroller is ours and is always there; wiring it is idempotent
        // because the handler is removed first.
        ColourHost.ViewChanged -= OnEditorViewChanged;
        ColourHost.ViewChanged += OnEditorViewChanged;

        if (_editorScrollViewer is not null)
        {
            return;
        }

        _editorScrollViewer = Editor.FindDescendant<ScrollViewer>();
        if (_editorScrollViewer is not null)
        {
            _editorScrollViewer.ViewChanged += OnEditorViewChanged;
        }
    }

    private void OnEditorViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        SyncGutterOffset();

    /// <summary>
    /// Slides the line numbers to match whichever pane is on screen.
    /// </summary>
    /// <remarks>
    /// There are two scrollers, not one: the <c>TextBox</c> brings its own, and the coloured
    /// read-only pane is a <c>ScrollViewer</c> of ours. Only the first was driving the gutter,
    /// so in a read-only pane — every tool's result — the numbers sat still while the text
    /// moved, and by the second screenful they were labelling the wrong lines.
    /// </remarks>
    private void SyncGutterOffset()
    {
        var offset = ColourHost.Visibility == Visibility.Visible
            ? ColourHost.VerticalOffset
            : _editorScrollViewer?.VerticalOffset ?? 0;

        GutterOffset.Y = -offset;
    }

    // ------------------------------------------------------------ text sync

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        if (editor._suppressTextSync)
        {
            return;
        }

        var value = e.NewValue as string ?? string.Empty;
        if (editor.Editor.Text != value)
        {
            editor._suppressTextSync = true;
            try
            {
                editor.Editor.Text = value;
            }
            finally
            {
                editor._suppressTextSync = false;
            }
        }

        editor.UpdatePlaceholder();
        editor.UpdateGutter();

        // The coloured view mirrors the same text; re-tokenising is cheap next to the run
        // rebuild, and it is skipped entirely while the pane is showing the plain editor.
        if (editor.ColourHost.Visibility == Visibility.Visible)
        {
            editor.Colourised.Text = value;
        }
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTextSync)
        {
            _suppressTextSync = true;
            try
            {
                Text = Editor.Text;
            }
            finally
            {
                _suppressTextSync = false;
            }
        }

        UpdatePlaceholder();
        UpdateGutter();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeEditor)d).UpdatePlaceholder();

    private static void OnSyntaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        editor.Colourised.Grammar = (SyntaxLanguage)e.NewValue;
        editor.UpdateColourisedVisibility();
    }

    /// <summary>
    /// Chooses between the plain editor and the coloured view.
    /// </summary>
    /// <remarks>
    /// Colour is shown only when the pane is read-only <em>and</em> a language is set
    /// <em>and</em> the user has not turned it off. Anything else falls back to the TextBox,
    /// which is why turning colouring off can never leave a tool unusable.
    /// </remarks>
    private void UpdateColourisedVisibility()
    {
        var coloured = IsReadOnly && Syntax != SyntaxLanguage.None && _settings.EditorSyntaxColouring;

        ColourHost.Visibility = coloured ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = coloured ? Visibility.Collapsed : Visibility.Visible;

        if (coloured)
        {
            Colourised.Text = Editor.Text;
            Colourised.WordWrap = _settings.EditorWordWrap;
            Colourised.EditorFontSize = _settings.EditorFontSize;

            // A ScrollViewer that can scroll horizontally measures its child with infinite
            // width, so TextWrapping has nothing to wrap against and the pane scrolls sideways
            // however wrapped the text claims to be. Turning the axis off is what makes wrapping
            // take effect — the same trick the editable TextBox above already needs.
            ColourHost.HorizontalScrollMode = _settings.EditorWordWrap
                ? ScrollMode.Disabled
                : ScrollMode.Auto;

            ColourHost.HorizontalScrollBarVisibility = _settings.EditorWordWrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
        }
    }

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        editor.Editor.IsReadOnly = (bool)e.NewValue;
        editor.UpdateColourisedVisibility();
        editor.UpdateCommandVisibility();
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeEditor)d).UpdateCommandVisibility();

    /// <summary>A toolbar button with no command bound is not shown at all.</summary>
    private void UpdateCommandVisibility()
    {
        PasteButton.Visibility = PasteCommand is null ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = OpenFileCommand is null ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = ClearCommand is null ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = CopyCommand is null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Visibility = SaveCommand is null ? Visibility.Collapsed : Visibility.Visible;

        // Every result pane can hand its text to Scratchpad (SP-41); input panes already hold the user's text.
        ScratchButton.Visibility = IsReadOnly && CanSendToScratchpad ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Keeps the placeholder sitting exactly on top of where the first character lands. The
    /// TextBox draws its content a little inside its own padding — a border width plus room for
    /// the caret — so the offset is the padding plus that constant, measured once and applied
    /// here rather than hard-coded in the markup where a padding change would break it.
    /// </summary>
    private void AlignPlaceholder()
    {
        const double caretInsetX = 4;
        const double caretInsetY = 2;

        PlaceholderLabel.Margin = new Thickness(
            Editor.Padding.Left + caretInsetX,
            Editor.Padding.Top + caretInsetY,
            Editor.Padding.Right + caretInsetX,
            0);
    }

    private void UpdatePlaceholder() =>
        PlaceholderLabel.Visibility =
            string.IsNullOrEmpty(Editor.Text) && !string.IsNullOrEmpty(PlaceholderText)
                ? Visibility.Visible
                : Visibility.Collapsed;


    // ------------------------------------------------------------ find (FR-T12)

    private readonly List<int> _matches = [];
    private int _currentMatch = -1;

    /// <summary>
    /// What the strip reports: the position in the matches, or that there are none.
    /// </summary>
    /// <remarks>
    /// A plain <c>DependencyProperty</c> rather than a view model, because this is the control's
    /// own state and no tool has any business reading it.
    /// </remarks>
    public static readonly DependencyProperty FindStatusProperty = DependencyProperty.Register(
        nameof(FindStatus), typeof(string), typeof(CodeEditor), new PropertyMetadata(string.Empty));

    public string FindStatus
    {
        get => (string)GetValue(FindStatusProperty);
        private set => SetValue(FindStatusProperty, value);
    }

    /// <summary>
    /// Opens the find strip and takes the caret to it.
    /// </summary>
    /// <remarks>
    /// Whatever is selected in the editor seeds the query, which is what makes "select a key,
    /// press Ctrl+F, press Enter" find the next one of those without typing it again.
    /// </remarks>
    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;

        if (Editor.SelectionLength is > 0 and < 120)
        {
            FindQuery.Text = Editor.SelectedText;
        }

        FindQuery.Focus(FocusState.Programmatic);
        FindQuery.SelectAll();

        RefreshMatches();
    }

    /// <summary>Closes the strip, clears the highlights, and hands focus back to the text.</summary>
    public void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _matches.Clear();
        _currentMatch = -1;
        FindStatus = string.Empty;
        Colourised.ClearHighlights();

        if (Editor.Visibility == Visibility.Visible)
        {
            Editor.Focus(FocusState.Programmatic);
        }
    }

    private void OnFindQueryChanged(object sender, TextChangedEventArgs e) => RefreshMatches();

    private void OnFindOptionChanged(object sender, RoutedEventArgs e) => RefreshMatches();

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => HideFind();

    private void OnFindNextClick(object sender, RoutedEventArgs e) => StepMatch(1);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => StepMatch(-1);

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                StepMatch(IsShiftDown() ? -1 : 1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Escape:
                HideFind();
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Finds every occurrence and shows them all at once.
    /// </summary>
    /// <remarks>
    /// All of them, not just the next one: the count is the useful part when you are asking
    /// whether a key appears once or forty times, and highlighting the rest is what turns the
    /// answer into something you can see without pressing Enter forty times.
    /// </remarks>
    private void RefreshMatches()
    {
        _matches.Clear();
        _currentMatch = -1;

        var query = FindQuery.Text;
        var text = Editor.Text;

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            FindStatus = string.Empty;
            Colourised.ClearHighlights();
            return;
        }

        var comparison = FindMatchCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var at = 0;

        while (at <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, at, comparison);

            if (found < 0)
            {
                break;
            }

            _matches.Add(found);
            at = found + query.Length;

            if (_matches.Count >= MaxMatches)
            {
                break;
            }
        }

        if (_matches.Count == 0)
        {
            FindStatus = "no matches";
            Colourised.ClearHighlights();
            return;
        }

        Colourised.Highlight(_matches, query.Length);
        _currentMatch = 0;
        ShowCurrentMatch();
    }

    /// <summary>
    /// A cap on how many matches are tracked at once.
    /// </summary>
    /// <remarks>
    /// Searching "e" in a 5 MB document finds hundreds of thousands of positions, and
    /// highlighting them all is both useless and slow. The count says it stopped counting.
    /// </remarks>
    private const int MaxMatches = 2_000;

    private void StepMatch(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _currentMatch = ((_currentMatch + delta) % _matches.Count + _matches.Count) % _matches.Count;
        ShowCurrentMatch();
    }

    private void ShowCurrentMatch()
    {
        if (_currentMatch < 0 || _currentMatch >= _matches.Count)
        {
            return;
        }

        var start = _matches[_currentMatch];
        var length = FindQuery.Text.Length;

        FindStatus = _matches.Count >= MaxMatches
            ? $"{_currentMatch + 1} of {MaxMatches}+"
            : $"{_currentMatch + 1} of {_matches.Count}";

        if (Editor.Visibility == Visibility.Visible)
        {
            // Focus stays in the find box, so the selection is only visible because the editor
            // is told to paint it unfocused (see the markup), and the TextBox will not scroll to
            // a selection it does not own focus for — that has to be done by hand.
            Editor.Select(start, length);
            ScrollEditorTo(start);
            UpdateGutter();
        }
        else
        {
            Colourised.ScrollTo(ColourHost, start);
        }
    }

    /// <summary>
    /// Scrolls the plain editor so the character at <paramref name="offset"/> sits a third of
    /// the way down the viewport.
    /// </summary>
    /// <remarks>
    /// The character's rectangle is taken relative to the first character's, which gives its
    /// position within the content whether or not the TextBox reports rectangles already
    /// shifted by the current scroll offset.
    /// </remarks>
    private void ScrollEditorTo(int offset)
    {
        AttachScrollViewer();

        if (_editorScrollViewer is null || string.IsNullOrEmpty(Editor.Text))
        {
            return;
        }

        try
        {
            var origin = Editor.GetRectFromCharacterIndex(0, trailingEdge: false);
            var target = Editor.GetRectFromCharacterIndex(Math.Min(offset, Editor.Text.Length - 1), trailingEdge: false);

            var y = target.Top - origin.Top - (_editorScrollViewer.ViewportHeight / 3);
            var x = _settings.EditorWordWrap
                ? (double?)null
                : Math.Max(0, target.Left - origin.Left - (_editorScrollViewer.ViewportWidth / 3));

            _editorScrollViewer.ChangeView(x, Math.Max(0, y), null, disableAnimation: true);
        }
        catch (ArgumentException)
        {
            // Not laid out yet; the selection is still made, it just is not scrolled to.
        }
    }

    // ------------------------------------------------------------ gutter

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            UpdateGutter();
        }
    }


    /// <summary>
    /// Clips the gutter to the editor surface.
    /// </summary>
    /// <remarks>
    /// The line numbers live in a <c>Canvas</c> and are scrolled by a render transform, and
    /// neither a Canvas nor a Border clips by default — so a scrolled gutter painted its numbers
    /// straight over the footer underneath it. A rectangular clip is all that is needed, and
    /// <see cref="UIElement.Clip"/> only accepts a rectangle anyway.
    /// </remarks>
    private void OnGutterHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        GutterHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

    private void UpdateGutter()
    {
        if (GutterHost.Visibility != Visibility.Visible)
        {
            return;
        }

        var columns = 0;
        if (_settings.EditorWordWrap)
        {
            var width = Editor.ActualWidth - Editor.Padding.Left - Editor.Padding.Right - 16;
            columns = (int)Math.Floor(width / Math.Max(1, MeasureCharacterWidth()));
        }

        // Typing within a line leaves the numbers as they were; re-assigning them would still
        // make the gutter lay itself out again on every keystroke.
        var numbers = GutterBuilder.Build(Editor.Text, _settings.EditorWordWrap, columns);
        if (!string.Equals(GutterText.Text, numbers, StringComparison.Ordinal))
        {
            GutterText.Text = numbers;
        }

        // Widen the gutter once the line count needs more digits, so numbers never clip.
        var lineCount = Core.Text.TextUtil.CountLines(Editor.Text, keepTrailingEmpty: true);
        var digits = Math.Max(2, lineCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        GutterClip.Width = 16 + (digits * MeasureCharacterWidth());
    }

    /// <summary>
    /// Width of one character in the editor's monospace font, measured once per font change.
    /// Ten characters are measured and divided, which cancels out rounding.
    /// </summary>
    private double MeasureCharacterWidth()
    {
        if (_characterWidth > 0)
        {
            return _characterWidth;
        }

        var probe = new TextBlock
        {
            FontFamily = GutterText.FontFamily,
            FontSize = _settings.EditorFontSize,
            Text = "0000000000",
        };

        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _characterWidth = Math.Max(1, probe.DesiredSize.Width / 10);
        return _characterWidth;
    }

    // ------------------------------------------------------------ caret

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ShowFooter != Visibility.Visible)
        {
            return;
        }

        var (line, column) = Core.Text.TextUtil.OffsetToLineColumn(Editor.Text, Editor.SelectionStart);
        var selected = Editor.SelectionLength;

        CaretText.Text = selected > 0
            ? $"Ln {line}, Col {column} · {selected:N0} selected"
            : $"Ln {line}, Col {column}";
    }

    // ------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!AllowFileDrop || IsReadOnly || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open in DevTools";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = false;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!AllowFileDrop || IsReadOnly || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is not StorageFile file)
            {
                return;
            }

            var buffer = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(bytes);
            }

            FileDropped?.Invoke(this, bytes);

            if (bytes.Length > Core.Limits.MaxInputBytes)
            {
                return;
            }

            Text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(bytes);
        }
        catch (Exception)
        {
            // An unreadable drop is simply ignored; the file dialog reports real failures.
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ------------------------------------------------------------ copy toast

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Editor.Text))
        {
            return;
        }

        ShowCopiedToast();
    }

    /// <summary>A brief, non-blocking confirmation that the copy happened (FR-T03).</summary>
    private void ShowCopiedToast(string text = "Copied")
    {
        ToastText.Text = text;
        CopiedToast.Visibility = Visibility.Visible;
        CopiedToast.Opacity = 1;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _toastTimer.Tick += (s, _) =>
        {
            CopiedToast.Opacity = 0;
            CopiedToast.Visibility = Visibility.Collapsed;
            (s as DispatcherTimer)?.Stop();
        };
        _toastTimer.Start();
    }

    /// <summary>Moves keyboard focus into the text surface.</summary>
    public void FocusEditor() => Editor.Focus(FocusState.Programmatic);

    /// <summary>Whether a read-only pane offers Send to Scratchpad. On by default.</summary>
    public bool CanSendToScratchpad { get; set; } = true;

    /// <summary>The selected text, or empty when nothing is selected.</summary>
    public string SelectedText => Editor.SelectedText ?? string.Empty;

    /// <summary>Where the caret is, as a character offset into <see cref="Text"/>.</summary>
    public int CaretIndex
    {
        get => Editor.SelectionStart;
        set => Editor.Select(Math.Clamp(value, 0, Editor.Text.Length), 0);
    }

    /// <summary>Types <paramref name="text"/> at the caret, replacing any selection, as a paste would.</summary>
    public void InsertAtCaret(string text)
    {
        if (IsReadOnly)
        {
            return;
        }

        var start = Editor.SelectionStart;
        Editor.SelectedText = text;
        Editor.Select(start + text.Length, 0);
    }

    private async void OnSendToScratchpadClick(object sender, RoutedEventArgs e)
    {
        var text = Editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var tool = App.GetService<IToolChrome>().Active?.Title ?? "a tool";
            var language = Syntax switch
            {
                SyntaxLanguage.Json => Core.Scratch.ScratchLanguage.Json,
                SyntaxLanguage.Xml => Core.Scratch.ScratchLanguage.Xml,
                SyntaxLanguage.Sql => Core.Scratch.ScratchLanguage.Sql,
                SyntaxLanguage.CSharp => Core.Scratch.ScratchLanguage.CSharp,
                _ => Core.Scratch.ScratchLanguage.Plain,
            };

            await App.GetService<IScratchpadStore>().CreateFromToolAsync(text, tool, language);
            ShowCopiedToast("Sent to Scratchpad");
        }
        catch (Exception ex)
        {
            App.LogError("Send to Scratchpad", ex);
        }
    }
}
