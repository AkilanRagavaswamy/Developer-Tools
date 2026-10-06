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
        BandScroller.SizeChanged += (_, e) => BandOptions.MinWidth = e.NewSize.Width;

        ApplyPaneMode(_settings.NavigationPaneOpen);

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += OnNavigated;

        RegisterAccelerators();

        PointerPressed += OnPointerPressed;
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>What the keyboard button in the title bar lists.</summary>
    public IReadOnlyList<ShortcutEntry> Shortcuts => ShortcutEntry.All;

    /// <summary>Handed to <c>Window.SetTitleBar</c> so this row behaves as the caption bar.</summary>
    public UIElement TitleBarElement => TitleBarDragArea;

    public async Task InitializeAsync()
    {
        UpdateCaptionInset();

        await ViewModel.InitializeAsync();
        BuildNavigation();

        // A forgekitrk: URI that launched the app opens that tool instead of Home (FR-S18).
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

    /// <summary>The row the active tool handed over.</summary>
    private FrameworkElement? _options;

    private void OnChromeOptionsChanged(object? sender, EventArgs e)
    {
        // Detach first: an element has one parent, and the band still holds the old row.
        BandOptions.Content = null;
        _options = _chrome.Options;

        PlaceOptions();
    }

    /// <summary>
    /// Shows the options row in its band at the top of the page.
    /// </summary>
    /// <remarks>
    /// It used to move up into the title bar when there was room, but the title bar is the
    /// window's drag and double-click area: clicking quickly on an option there could maximise
    /// or restore the window. The title bar now carries only the tool's name and the app commands.
    /// </remarks>
    private void PlaceOptions()
    {
        if (_options is null)
        {
            OptionsBand.Visibility = Visibility.Collapsed;
            return;
        }

        if (!ReferenceEquals(BandOptions.Content, _options))
        {
            BandOptions.Content = _options;
        }

        OptionsBand.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------- jump to tool

    /// <summary>Ctrl+K: puts the caret in Jump to Tool, wherever it was.</summary>
    private void FocusJumpBox() => JumpBox.Focus(FocusState.Keyboard);

    /// <summary>An empty box lists the recent tools first, so the commonest jump is one key away.</summary>
    private void OnJumpBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(JumpBox.Text))
        {
            ViewModel.OpenPalette();
        }

        JumpBox.IsSuggestionListOpen = ViewModel.PaletteResults.Count > 0;
    }

    private void OnJumpBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.PaletteQuery = sender.Text;
            sender.IsSuggestionListOpen = ViewModel.PaletteResults.Count > 0;
        }
    }

    /// <summary>Enter, or a click on a result: opens the chosen tool, or the best match for what was typed.</summary>
    private void OnJumpBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = args.ChosenSuggestion as ToolSearchResult ?? ViewModel.PaletteResults.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        sender.Text = string.Empty;
        sender.IsSuggestionListOpen = false;
        ViewModel.ClosePalette();
        _navigation.NavigateToTool(target.Tool.Id);

        // Hand the keyboard to the tool that just opened rather than leaving it in the box.
        ContentFrame.Focus(FocusState.Programmatic);
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
        Add(VirtualKey.K, VirtualKeyModifiers.Control, FocusJumpBox);
        Add((VirtualKey)188, VirtualKeyModifiers.Control, () => _navigation.NavigateTo(typeof(SettingsPage)));
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigation.GoBack());
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _navigation.GoForward());
        Add(VirtualKey.F1, VirtualKeyModifiers.None, () => _navigation.NavigateTo(typeof(SettingsPage)));

        // App-wide shortcuts on the whole shell: an automatic tooltip would follow the pointer
        // everywhere. They are listed in Settings instead.
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
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
