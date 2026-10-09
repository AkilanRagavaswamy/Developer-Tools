using System.Reflection;
using DevTools.App.Services;
using DevTools.App.ViewModels;
using DevTools.Http.Execution;
using DevTools.Http.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DevTools.App;

public partial class App : Application
{
    private static IServiceProvider? _services;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>The composition root. Available to pages so they can resolve their view model.</summary>
    public static IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The service provider is not ready yet.");

    public static MainWindow? MainWindow { get; private set; }

    public static T GetService<T>()
        where T : class => Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Captured here, on the UI thread, before anything can raise an event from a background
        // one — which is what every view model marshals back through.
        UiDispatcher.Initialize();

        _services = ConfigureServices();

        // Read any forgekitrk: URI before the window exists, so the shell can open that tool
        // instead of Home on its first navigation (FR-S18).
        _services.GetRequiredService<IProtocolActivationService>().Initialize();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IShellContext, ShellContext>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFavoritesService, FavoritesService>();
        services.AddSingleton<IRecentToolsService, RecentToolsService>();
        services.AddSingleton<IToolStateService, ToolStateService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IProtocolActivationService, ProtocolActivationService>();
        services.AddSingleton<IToolHandoffService, ToolHandoffService>();
        services.AddSingleton<IScratchpadStore, ScratchpadStore>();
        services.AddSingleton<IToolChrome, ToolChromeService>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ToolServices>();

        // The HTTP side. Only the API Builder takes these, so nothing else in the app has a
        // way to open a socket even by accident (NFR-05).
        services.AddSingleton<ICredentialStore, CredentialVaultStore>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();

        services.AddSingleton<HttpExecutor>(provider =>
            new HttpExecutor(provider.GetRequiredService<ICredentialStore>()));

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SettingsViewModel>();

        RegisterToolViewModels(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Every <see cref="ToolViewModelBase"/> in this assembly is registered automatically, so
    /// adding a tool never means remembering to edit the container.
    /// </summary>
    private static void RegisterToolViewModels(IServiceCollection services)
    {
        var baseType = typeof(ToolViewModelBase);

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!type.IsAbstract && type.IsClass && baseType.IsAssignableFrom(type))
            {
                services.AddTransient(type);
            }
        }
    }

    /// <summary>
    /// Writes a failure to <c>logs\crash-&lt;date&gt;.log</c>. Used by the unhandled-exception
    /// handler and by any <c>async void</c> or fire-and-forget path, whose exceptions never
    /// reach that handler and would otherwise vanish silently.
    /// </summary>
    public static void LogError(string context, Exception exception)
    {
        try
        {
            var directory = Path.Combine(JsonStore.RootPath, "logs");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(
                file,
                $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // If even logging fails there is nothing further to try.
        }
    }

    /// <summary>
    /// Last line of defence: record what went wrong and keep the window alive rather than
    /// vanishing without explanation.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        LogError(e.Message, e.Exception);

        try
        {
            if (_services is not null)
            {
                var dialogs = _services.GetService<IDialogService>();
                _ = dialogs?.ShowMessageAsync(
                    "Something went wrong",
                    $"{e.Message}\n\nThe details were written to the logs folder. The app is still running.");
            }
        }
        catch (Exception)
        {
            // Showing the dialog is best effort.
        }
    }
}
