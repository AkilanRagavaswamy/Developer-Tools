using DevTools.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DevTools.App.Controls;

/// <summary>
/// The two-pane container every transform tool uses. It reflows from side-by-side to stacked
/// below <see cref="StackBreakpoint"/>, and its splitter position is remembered across
/// sessions (FR-T08). The splitter is keyboard-operable, not mouse-only (FR-S16).
/// </summary>
public sealed partial class ToolSplitPanel : UserControl
{
    /// <summary>Below this width the panes stack vertically instead of sitting side by side.</summary>
    public const double StackBreakpoint = 900;

    private const double SplitterThickness = 10;
    private const double MinimumRatio = 0.15;
    private const double MaximumRatio = 0.85;
    private const double KeyboardStep = 0.02;

    private readonly ISettingsService _settings;
    private bool _isStacked;
    private double _ratio;

    public ToolSplitPanel()
    {
        InitializeComponent();

        _settings = App.GetService<ISettingsService>();
        _ratio = _settings.SplitterRatio;

        Loaded += (_, _) => ApplyLayout(force: true);
    }

    public static readonly DependencyProperty FirstPaneProperty = DependencyProperty.Register(
        nameof(FirstPane), typeof(object), typeof(ToolSplitPanel), new PropertyMetadata(null));

    public object? FirstPane
    {
        get => GetValue(FirstPaneProperty);
        set => SetValue(FirstPaneProperty, value);
    }

    public static readonly DependencyProperty SecondPaneProperty = DependencyProperty.Register(
        nameof(SecondPane), typeof(object), typeof(ToolSplitPanel), new PropertyMetadata(null));

    public object? SecondPane
    {
        get => GetValue(SecondPaneProperty);
        set => SetValue(SecondPaneProperty, value);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(force: false);

    private void ApplyLayout(bool force)
    {
        var shouldStack = ActualWidth > 0 && ActualWidth < StackBreakpoint;

        if (!force && shouldStack == _isStacked)
        {
            UpdateRatio();
            return;
        }

        _isStacked = shouldStack;

        if (_isStacked)
        {
            // Vertical: one column, three rows.
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            SecondColumn.Width = new GridLength(0);

            Grid.SetRow(FirstPresenter, 0);
            Grid.SetColumn(FirstPresenter, 0);
            Grid.SetRow(Splitter, 1);
            Grid.SetColumn(Splitter, 0);
            Grid.SetRow(SecondPresenter, 2);
            Grid.SetColumn(SecondPresenter, 0);

            Splitter.Height = SplitterThickness;
            Splitter.Width = double.NaN;
            Splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            Splitter.VerticalAlignment = VerticalAlignment.Center;
            ProtectedCursor = null;

            if (Splitter.FindDescendant<Border>("Grip") is { } horizontalGrip)
            {
                horizontalGrip.Height = 3;
                horizontalGrip.Width = 48;
            }
        }
        else
        {
            // Horizontal: three columns, one row.
            SplitterColumn.Width = new GridLength(SplitterThickness);

            Grid.SetRow(FirstPresenter, 0);
            Grid.SetColumn(FirstPresenter, 0);
            Grid.SetRow(Splitter, 0);
            Grid.SetColumn(Splitter, 1);
            Grid.SetRow(SecondPresenter, 0);
            Grid.SetColumn(SecondPresenter, 2);

            Splitter.Width = SplitterThickness;
            Splitter.Height = double.NaN;
            Splitter.HorizontalAlignment = HorizontalAlignment.Center;
            Splitter.VerticalAlignment = VerticalAlignment.Stretch;

            if (Splitter.FindDescendant<Border>("Grip") is { } verticalGrip)
            {
                verticalGrip.Width = 3;
                verticalGrip.Height = 48;
            }
        }

        UpdateRatio();
    }

    private void UpdateRatio()
    {
        var first = new GridLength(_ratio, GridUnitType.Star);
        var second = new GridLength(1 - _ratio, GridUnitType.Star);

        if (_isStacked)
        {
            FirstRow.Height = first;
            SplitterRow.Height = new GridLength(SplitterThickness);
            SecondRow.Height = second;
        }
        else
        {
            FirstRow.Height = new GridLength(1, GridUnitType.Star);
            SplitterRow.Height = new GridLength(0);
            SecondRow.Height = new GridLength(0);
            FirstColumn.Width = first;
            SecondColumn.Width = second;
        }
    }

    private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        var total = _isStacked ? ActualHeight : ActualWidth;
        if (total <= 0)
        {
            return;
        }

        var delta = _isStacked ? e.VerticalChange : e.HorizontalChange;
        SetRatio(_ratio + (delta / total));
    }

    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var handled = true;

        switch (e.Key)
        {
            case VirtualKey.Left when !_isStacked:
            case VirtualKey.Up when _isStacked:
                SetRatio(_ratio - KeyboardStep);
                break;
            case VirtualKey.Right when !_isStacked:
            case VirtualKey.Down when _isStacked:
                SetRatio(_ratio + KeyboardStep);
                break;
            case VirtualKey.Home:
                SetRatio(MinimumRatio);
                break;
            case VirtualKey.End:
                SetRatio(MaximumRatio);
                break;
            case VirtualKey.Escape:
                SetRatio(0.5);
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    private void SetRatio(double value)
    {
        _ratio = Math.Clamp(value, MinimumRatio, MaximumRatio);
        _settings.SplitterRatio = _ratio;
        UpdateRatio();
    }
}
