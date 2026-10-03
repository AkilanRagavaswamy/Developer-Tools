namespace DevTools.App.Services;

/// <summary>
/// The service bundle every tool view model needs. Passing one object keeps each view-model
/// constructor from listing ten dependencies, while still resolving through DI.
/// </summary>
/// <remarks>
/// The HTTP-specific services are deliberately <em>not</em> here: only the two API tools need
/// an executor, a workspace or a credential store, and they take those directly. Putting them
/// in the shared bundle would hand every tool a way to open a socket, which is exactly the
/// thing the project layout exists to prevent.
/// </remarks>
public sealed class ToolServices(
    ToolCatalog catalog,
    ISettingsService settings,
    IToolStateService state,
    IClipboardService clipboard,
    IFileDialogService files,
    IDialogService dialogs,
    IFavoritesService favorites,
    IThemeService theme,
    INavigationService navigation,
    IToolHandoffService handoff)
{
    /// <summary>Cross-tool hand-off, which every tool may both send and receive (FR-S18).</summary>
    public IToolHandoffService Handoff { get; } = handoff;

    public ToolCatalog Catalog { get; } = catalog;

    public ISettingsService Settings { get; } = settings;

    public IToolStateService State { get; } = state;

    public IClipboardService Clipboard { get; } = clipboard;

    public IFileDialogService Files { get; } = files;

    public IDialogService Dialogs { get; } = dialogs;

    public IFavoritesService Favorites { get; } = favorites;

    public IThemeService Theme { get; } = theme;

    public INavigationService Navigation { get; } = navigation;
}
