using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevTools.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string closeText = "OK");

    Task<bool> ConfirmAsync(string title, string message, string primaryText = "Continue", string closeText = "Cancel");

    Task<ContentDialogResult> ShowAsync(ContentDialog dialog);
}

/// <summary>
/// Hosts <see cref="ContentDialog"/> against the live XAML root. WinUI allows only one
/// dialog at a time, so a second request while one is open is serialised rather than
/// throwing the <c>COMException</c> WinUI would otherwise raise.
/// </summary>
public sealed class DialogService(IShellContext shell, ISettingsService settings) : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ShowMessageAsync(string title, string message, string closeText = "OK")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };

        await ShowAsync(dialog);
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryText = "Continue",
        string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
        };

        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var root = shell.XamlRoot;
        if (root is null)
        {
            return ContentDialogResult.None;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            dialog.XamlRoot = root;
            dialog.RequestedTheme = settings.Theme;
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            _gate.Release();
        }
    }
}
