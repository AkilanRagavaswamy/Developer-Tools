using DevTools.App.Models;
using DevTools.App.Services;
using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace DevTools.App.Views;

/// <summary>
/// The application shell: title bar, navigation pane, content frame, clipboard suggestion
/// banner and command palette. It owns the app-level keyboard shortcuts (FR-S14).
/// </summary>
public sealed partial class ShellPage : UserControl
{
    private const string HomeTag = "__home";
    private const string SettingsTag = "__settings";
    private const string FavoritesHeaderTag = "__favorites";
    private const string GroupTagPrefix = "__group:";

    private readonly INavigationService _navigation;
    private readonly ToolCatalog _catalog;
    private readonly ISettingsService _settings;
    private readonly IProtocolActivationService _protocol;
    private readonly IShellContext _shell;
    private readonly IToolChrome _chrome;

    private bool _suppressNavSelection;

    public ShellPage()
    {
        InitializeComponent();

        ViewModel = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        _catalog = App.GetService<ToolCatalog>();
        _settings = App.GetService<ISettingsService>();
        _protocol = App.GetService<IProtocolActivationService>();
        _shell = App.GetService<IShellContext>();
        _chrome = App.GetService<IToolChrome>();

        _chrome.OptionsChanged += OnChromeOptionsChanged;
        TitleBarDragArea.SizeChanged += (_, _) => PlaceOptions();

        ApplyPaneMode(_settings.NavigationPaneOpen);

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += OnNavigated;

        RegisterAccelerators();

        PointerPressed += OnPointerPressed;
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Handed to <c>Window.SetTitleBar</c> so this row behaves as the caption bar.</summary>
    public UIElement TitleBarElement => TitleBarDragArea;

    public async Task InitializeAsync()
    {
        UpdateCaptionInset();

        await ViewModel.InitializeAsync();
        BuildNavigation();

        // A devtools: URI that launched the app opens that tool instead of Home (FR-S18).
        if (_protocol.TakePendingToolId() is { } launchToolId)
        {
            _navigation.NavigateToTool(launchToolId);
        }
        else
        {
            _navigation.NavigateTo(typeof(HomePage));
        }

        _protocol.ToolRequested += OnProtocolToolRequested;

        await RunSmartDetectAsync();
    }

    private void OnProtocolToolRequested(object? sender, string toolId) =>
        _navigation.NavigateToTool(toolId);

    public Task RunSmartDetectAsync() => ViewModel.DetectClipboardAsync();

    // ------------------------------------------------------------- navigation

    /// <summary>
    /// Builds the pane: Home, the favorites group when there are any, then each category
    /// holding its tools, with Settings in the footer. Rebuilt whenever favorites change.
    /// </summary>
    private void BuildNavigation()
    {
        _suppressNavSelection = true;
        try
        {
            Nav.MenuItems.Clear();

            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = "Home",
                Icon = new FontIcon { Glyph = "" },
                Tag = HomeTag,
            });

            //if (ViewModel.Favorites.Count > 0)
            //{
            //    var favorites = new NavigationViewItem
            //    {
            //        Content = "Favorites",
            //        Icon = new FontIcon { Glyph = "" },
            //        Tag = FavoritesHeaderTag,
            //        SelectsOnInvoked = false,
            //        IsExpanded = true,
            //    };

            //    foreach (var tool in ViewModel.Favorites)
            //    {
            //        favorites.MenuItems.Add(CreateToolItem(tool));
            //    }

            //    Nav.MenuItems.Add(favorites);
            //}

            Nav.MenuItems.Add(new NavigationViewItemSeparator());

            // Tools sit under their category. The groups start expanded, so every tool is still
            // one click away; in the compact pane a group opens as a flyout of its tools.
            foreach (var category in ToolCategoryInfo.All)
            {
                if (!_catalog.ByCategory.TryGetValue(category, out var tools) || tools.Count == 0)
                {
                    continue;
                }

                var group = new NavigationViewItem
                {
                    Content = ToolCategoryInfo.NavigationName(category),
                    Icon = new FontIcon { Glyph = ToolCategoryInfo.Glyph(category) },
                    Tag = GroupTagPrefix + category,
                    SelectsOnInvoked = false,
                    IsExpanded = true,
                };

                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(group, ToolCategoryInfo.NavigationName(category));

                foreach (var tool in tools)
                {
                    group.MenuItems.Add(CreateToolItem(tool));
                }

                Nav.MenuItems.Add(group);
            }

            Nav.FooterMenuItems.Clear();
            Nav.FooterMenuItems.Add(new NavigationViewItem
            {
                Content = "Settings",
                Icon = new FontIcon { Glyph = "" },
                Tag = SettingsTag,
            });
        }
        finally
        {
            _suppressNavSelection = false;
        }

        SyncSelection(_navigation.CurrentToolId, _navigation.CurrentPageType);
    }

    private static NavigationViewItem CreateToolItem(ToolDescriptor tool)
    {
        var item = new NavigationViewItem
        {
            Content = tool.Name,
            Icon = new FontIcon { Glyph = tool.Glyph, FontFamily = tool.GlyphFont },
            Tag = tool.Id,
        };

        ToolTipService.SetToolTip(item, tool.Description);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, tool.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, tool.Description);
        return item;
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_suppressNavSelection || args.InvokedItemContainer?.Tag is not string tag)
        {
            return;
        }

        NavigateByTag(tag);
    }

    private void NavigateByTag(string tag)
    {
        switch (tag)
        {
            case HomeTag:
                _navigation.NavigateTo(typeof(HomePage));
                break;
            case SettingsTag:
                _navigation.NavigateTo(typeof(SettingsPage));
                break;
            case FavoritesHeaderTag:
                break;
            default:
                if (!tag.StartsWith("__", StringComparison.Ordinal))
                {
                    _navigation.NavigateToTool(tag);
                }

                break;
        }
    }

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        SyncSelection(e.ToolId, e.PageType);

        // The favorites group is part of the pane, so it has to be rebuilt when it changes.
        if (ViewModel.Favorites.Count != CountFavoriteItems())
        {
            BuildNavigation();
        }
    }

    private int CountFavoriteItems()
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem { Tag: FavoritesHeaderTag } favorites)
            {
                return favorites.MenuItems.Count;
            }
        }

        return 0;
    }

    /// <summary>Highlights the pane entry matching the page the frame actually shows.</summary>
    private void SyncSelection(string? toolId, Type? pageType)
    {
        _suppressNavSelection = true;
        try
        {
            if (pageType == typeof(SettingsPage))
            {
                Nav.SelectedItem = Nav.FooterMenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string?)i.Tag == SettingsTag);
                return;
            }

            if (pageType == typeof(HomePage))
            {
                Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string?)i.Tag == HomeTag);
                return;
            }

            if (toolId is null)
            {
                return;
            }

            // Prefer the entry under its category over the duplicate under Favorites, so the
            // pane expands to show where the tool actually lives.
            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            {
                if ((string?)item.Tag == FavoritesHeaderTag)
                {
                    continue;
                }

                foreach (var child in item.MenuItems.OfType<NavigationViewItem>())
                {
                    if ((string?)child.Tag == toolId)
                    {
                        item.IsExpanded = true;
                        Nav.SelectedItem = child;
                        return;
                    }
                }
            }
        }
        finally
        {
            _suppressNavSelection = false;
        }
    }

    // ------------------------------------------------------------- title bar

    /// <summary>
    /// Keeps the title-bar commands clear of the caption buttons.
    /// </summary>
    /// <remarks>
    /// The space the minimise, maximise and close buttons occupy is not a constant: it scales
    /// with the display, and it differs again in right-to-left layout. A hard-coded margin was
    /// right at 100% and crowded the buttons into the close box at 150%, so the real figure is
    /// read from the window and converted out of physical pixels.
    /// </remarks>
    public void UpdateCaptionInset()
    {
        if (_shell.Window?.AppWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        var scale = XamlRoot?.RasterizationScale ?? 1.0;

        if (scale <= 0)
        {
            scale = 1.0;
        }

        TitleBarCommands.Margin = new Thickness(0, 0, titleBar.RightInset / scale, 0);
    }

    /// <summary>
    /// The pane toggle moved into the title bar, so <c>NavigationView</c>'s own hamburger is
    /// off and this drives it. The choice sticks, because someone who wants the names wants
    /// them next launch too.
    /// </summary>
    /// <summary>
    /// Switches the pane between the icon rail and the full list.
    /// </summary>
    /// <remarks>
    /// Done by changing <c>PaneDisplayMode</c> rather than by closing the pane. In
    /// <c>Left</c> mode <c>NavigationView</c> treats itself as expanded and puts
    /// <c>IsPaneOpen</c> back to true when the template is applied, so setting that alone does
    /// nothing. <c>LeftCompact</c> is the rail, and both modes lay out inline — which is what
    /// the design asks for; opening the pane as an overlay would cover the tool.
    /// </remarks>
    private void ApplyPaneMode(bool open)
    {
        Nav.PaneDisplayMode = open
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.LeftCompact;

        Nav.IsPaneOpen = open;
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        var open = Nav.PaneDisplayMode != NavigationViewPaneDisplayMode.Left;

        ApplyPaneMode(open);
        _settings.NavigationPaneOpen = open;
    }

    /// <summary>Mouse buttons 4 and 5 drive history, as they do in a browser (FR-S15).</summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsXButton1Pressed)
        {
            e.Handled = _navigation.GoBack();
        }
        else if (properties.IsXButton2Pressed)
        {
            e.Handled = _navigation.GoForward();
        }
    }

    // ------------------------------------------------- the tool's options row (FR-T01)

    /// <summary>The row the active tool handed over, whichever of the two hosts is showing it.</summary>
    private FrameworkElement? _options;

    /// <summary>
    /// The width the row needs without its labels, as the page declared it.
    /// </summary>
    /// <remarks>
    /// Declared rather than measured. A row that has never been in a visual tree has no
    /// templates applied and measures to nearly nothing — and nothing fits anywhere, so the row
    /// lands in the title bar whatever its real size. Measuring it after a layout pass means
    /// measuring it somewhere, which is the decision we were trying to make. One number in the
    /// page beside the row is blunt, but it is right on the first frame, it is where someone
    /// changing that row will see it, and guessing high only costs a row of height.
    /// </remarks>
    private double _optionsWidth;

    private void OnChromeOptionsChanged(object? sender, EventArgs e)
    {
        // Detach from both hosts first. An element has one parent, and the old one still holds
        // it at this point.
        TitleBarOptions.Content = null;
        BandOptions.Content = null;

        _options = _chrome.Options;
        _optionsWidth = _chrome.OptionsWidth;

        PlaceOptions();
    }

    /// <summary>
    /// Puts the options row in the title bar, or in its own band when the title bar is too
    /// narrow for it, and shows the row's labels if either place has room for them.
    /// </summary>
    /// <remarks>
    /// The title bar is the better place — it is a row of height the tool gets back — but a
    /// clipped options row is worse than a second row, which is why there is a band at all.
    /// Re-run on every resize, so dragging the window edge moves the row between the two.
    /// </remarks>
    private void PlaceOptions()
    {
        if (_options is null)
        {
            OptionsBand.Visibility = Visibility.Collapsed;
            return;
        }

        var titleBarRoom = TitleBarDragArea.ActualWidth
            - ToolIdentity.ActualWidth
            - TitleBarCommands.ActualWidth
            - TitleBarCommands.Margin.Right
            - OptionsGutter;

        var inTitleBar = _optionsWidth > 0 && _optionsWidth <= titleBarRoom;

        if (inTitleBar)
        {
            if (!ReferenceEquals(TitleBarOptions.Content, _options))
            {
                BandOptions.Content = null;
                TitleBarOptions.Content = _options;
            }
        }
        else if (!ReferenceEquals(BandOptions.Content, _options))
        {
            TitleBarOptions.Content = null;
            BandOptions.Content = _options;
        }

        OptionsBand.Visibility = inTitleBar ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The margins around the options host, which the sums have to allow for.</summary>
    private const double OptionsGutter = 28;

    // ------------------------------------------------------------- palette

    private void OnPaletteClick(object sender, RoutedEventArgs e) => OpenPalette();

    private void OpenPalette()
    {
        ViewModel.OpenPalette();
        PaletteOverlay.Visibility = Visibility.Visible;
        PaletteInput.Text = string.Empty;
        PaletteInput.Focus(FocusState.Programmatic);

        if (ViewModel.PaletteResults.Count > 0)
        {
            PaletteList.SelectedIndex = 0;
        }
    }

    private void ClosePalette()
    {
        ViewModel.ClosePalette();
        PaletteOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnPaletteBackdropTapped(object sender, TappedRoutedEventArgs e) => ClosePalette();

    /// <summary>Stops a tap inside the palette card from reaching the dismissing backdrop.</summary>
    private void OnPaletteContentTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnPaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                ClosePalette();
                e.Handled = true;
                break;

            case VirtualKey.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                ActivatePaletteSelection();
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        var count = ViewModel.PaletteResults.Count;
        if (count == 0)
        {
            return;
        }

        var index = PaletteList.SelectedIndex + delta;
        index = ((index % count) + count) % count;
        PaletteList.SelectedIndex = index;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void ActivatePaletteSelection()
    {
        var selected = PaletteList.SelectedItem as ToolSearchResult
                       ?? ViewModel.PaletteResults.FirstOrDefault();

        if (selected is not null)
        {
            ClosePalette();
            _navigation.NavigateToTool(selected.Tool.Id);
        }
    }

    private void OnPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolSearchResult result)
        {
            ClosePalette();
            _navigation.NavigateToTool(result.Tool.Id);
        }
    }

    // ------------------------------------------------------------- suggestions

    private void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string toolId })
        {
            ViewModel.DismissSuggestion();
            _navigation.NavigateToTool(toolId);
        }
    }

    private void OnSuggestionDismiss(object sender, RoutedEventArgs e) => ViewModel.DismissSuggestion();

    private void OnSuggestionSettingsClick(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        ViewModel.DismissSuggestion();
        _navigation.NavigateTo(typeof(SettingsPage));
    }

    // ------------------------------------------------------------- shortcuts

    private void RegisterAccelerators()
    {
        // No Ctrl+F here. It belongs to whichever pane has the caret (FR-T12), and the shell
        // claiming it for a search box was the one place the two could disagree.
        Add(VirtualKey.K, VirtualKeyModifiers.Control, OpenPalette);
        Add((VirtualKey)188, VirtualKeyModifiers.Control, () => _navigation.NavigateTo(typeof(SettingsPage)));
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigation.GoBack());
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _navigation.GoForward());
        Add(VirtualKey.F1, VirtualKeyModifiers.None, () => _navigation.NavigateTo(typeof(SettingsPage)));
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, ClosePalette);
        return;

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };

            KeyboardAccelerators.Add(accelerator);
        }
    }
}
