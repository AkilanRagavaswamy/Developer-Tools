using System.ComponentModel;
using DevTools.App.Services;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace DevTools.App.Views.Tools;

/// <summary>
/// The Scratchpad page: the note list, the editor, and a side panel that is either the note's
/// history or, for a Math note, a result for every line.
/// </summary>
public sealed partial class ScratchpadPage : ToolPageBase
{
    private static readonly Color[] TagColours =
    [
        Colors.Transparent,
        Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D),
        Color.FromArgb(0xFF, 0xF7, 0x6B, 0x15),
        Color.FromArgb(0xFF, 0xF5, 0xB7, 0x00),
        Color.FromArgb(0xFF, 0x30, 0xA4, 0x6C),
        Color.FromArgb(0xFF, 0x00, 0x90, 0xFF),
        Color.FromArgb(0xFF, 0x8E, 0x4E, 0xC6),
    ];

    private static readonly Dictionary<int, SolidColorBrush> TagBrushes = [];

    public ScratchpadPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<ScratchpadViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.NoteLeaving += OnNoteLeaving;
        ViewModel.NoteShown += OnNoteShown;

        Loaded += (_, _) => UpdateColumns();
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.NoteLeaving -= OnNoteLeaving;
            ViewModel.NoteShown -= OnNoteShown;
        };

        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control, () => ViewModel.NewNoteCommand.Execute(null));
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
        {
            ViewModel.IsNotesPaneOpen = true;
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
        });
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu, () => ViewModel.FormatCommand.Execute(null));
        AddShortcut(VirtualKey.K, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => SendButton.Flyout?.ShowAt(SendButton));

        // Ctrl+Shift+; — the semicolon key is OEM 1 on a US layout.
        AddShortcut((VirtualKey)186, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
            Editor.InsertAtCaret(DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
    }

    public ScratchpadViewModel ViewModel { get; }

    // ---------------------------------------------------------------- binding helpers

    public static Brush ColourBrush(int index)
    {
        index = Math.Clamp(index, 0, TagColours.Length - 1);
        if (!TagBrushes.TryGetValue(index, out var brush))
        {
            brush = new SolidColorBrush(TagColours[index]);
            TagBrushes[index] = brush;
        }

        return brush;
    }

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string ArchiveText(bool archived) => archived ? "Move back to Notes" : "Archive";

    public static object? RowTip(string? error) => error;

    public static Brush ResultBrush(bool isError) =>
        (Brush)Application.Current.Resources[isError ? "DevToolsWarnForegroundBrush" : "TextFillColorPrimaryBrush"];

    public static string RetentionText(bool _)
    {
        var days = App.GetService<ISettingsService>().ScratchpadRetentionDays;
        return $"Versions are kept for {days} days, up to 100 per note. Change this in Settings.";
    }

    // ---------------------------------------------------------------- layout

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScratchpadViewModel.IsNotesPaneOpen) or nameof(ScratchpadViewModel.ShowSidePanel))
        {
            UpdateColumns();
        }
    }

    /// <summary>A hidden pane gives its width back to the editor rather than leaving a gap.</summary>
    private void UpdateColumns()
    {
        NotesColumn.Width = ViewModel.IsNotesPaneOpen ? new GridLength(280) : new GridLength(0);
        SideColumn.Width = ViewModel.ShowSidePanel ? new GridLength(300) : new GridLength(0);
    }

    // ---------------------------------------------------------------- caret

    private void OnNoteLeaving(object? sender, EventArgs e) => ViewModel.CaretToRemember = Editor.CaretIndex;

    private void OnNoteShown(object? sender, int caret)
    {
        // After the text has reached the editor, which happens on this same pass.
        DispatcherQueue.TryEnqueue(() =>
        {
            Editor.CaretIndex = caret;
            if (ViewModel.IsEditable)
            {
                Editor.FocusEditor();
            }
        });
    }

    // ---------------------------------------------------------------- send to

    private void OnSendMenuOpening(object? sender, object e)
    {
        SendMenu.Items.Clear();

        var selection = Editor.SelectedText;
        SendMenu.Items.Add(new MenuFlyoutItem
        {
            Text = string.IsNullOrEmpty(selection) ? "Sends the whole note" : "Sends the selected text",
            IsEnabled = false,
        });
        SendMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var target in ViewModel.SendTargets)
        {
            var item = new MenuFlyoutItem { Text = target.Name };
            var id = target.ToolId;
            item.Click += (_, _) => ViewModel.SendTo(id, selection);
            SendMenu.Items.Add(item);
        }
    }

    // ---------------------------------------------------------------- math

    private void OnMathRowClick(object sender, ItemClickEventArgs e) =>
        ViewModel.CopyMathResultCommand.Execute(e.ClickedItem as ScratchMathRow);

    // ---------------------------------------------------------------- shortcuts

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
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
