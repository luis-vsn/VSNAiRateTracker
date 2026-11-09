using System;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VSNAiRateTracker.Services;
using VSNAiRateTracker.ViewModels;
using VSNAiRateTracker.Views;

namespace VSNAiRateTracker;

public partial class App : Application
{
    private ISyncOrchestrator? _orchestrator;
    private ISecureStorageService? _storageService;
    private IAnthropicApiService? _apiService;
    private SettingsWindow? _settingsWindow;
    private HttpClient? _httpClient;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Explicit shutdown ensures background tray execution without a main window
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _httpClient = new HttpClient();
            _storageService = new SecureStorageService();
            _apiService = new AnthropicApiService(_httpClient);
            var calculator = new BurnDownCalculator();
            var iconRenderer = new IconRendererService();
            var trayManager = new TrayIconManager(iconRenderer);

            _orchestrator = new SyncOrchestrator(
                _storageService,
                _apiService,
                calculator,
                trayManager,
                openSettingsAction: () => Dispatcher.UIThread.Post(OpenSettingsWindow),
                shutdownAction: () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));

            desktop.Exit += OnExit;

            // Start background synchronization loop
            _ = _orchestrator.StartAsync();

            // On first startup without API Key, open settings automatically
            _ = CheckInitialConfigurationAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CheckInitialConfigurationAsync()
    {
        if (_storageService == null) return;

        var config = await _storageService.LoadConfigAsync();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            await Dispatcher.UIThread.InvokeAsync(OpenSettingsWindow);
        }
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_storageService == null || _apiService == null || _orchestrator == null)
            return;

        var viewModel = new SettingsViewModel(_storageService, _apiService);
        _ = _storageService.LoadConfigAsync().ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                Dispatcher.UIThread.Post(() => viewModel.LoadFromConfig(task.Result));
            }
        });

        _settingsWindow = new SettingsWindow(viewModel, onSavedCallback: () =>
        {
            _ = _orchestrator.SyncOnceAsync();
        });

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _orchestrator?.Dispose();
        _httpClient?.Dispose();
    }
}