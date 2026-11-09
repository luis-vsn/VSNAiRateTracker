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
    private IAnthropicAuthService? _authService;
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
            _authService = new AnthropicAuthService(_httpClient);
            var calculator = new BurnDownCalculator();
            var iconRenderer = new IconRendererService();
            var trayManager = new TrayIconManager(iconRenderer);
            var telemetryService = new TelemetryService();

            _orchestrator = new SyncOrchestrator(
                _storageService,
                _apiService,
                calculator,
                trayManager,
                _authService,
                openSettingsAction: () => Dispatcher.UIThread.Post(OpenSettingsWindow),
                shutdownAction: () => Dispatcher.UIThread.Post(() => desktop.Shutdown()),
                telemetryService: telemetryService);

            desktop.Exit += OnExit;

            // Start background synchronization loop
            _ = _orchestrator.StartAsync();

            // On first startup without credentials, open settings automatically
            _ = CheckInitialConfigurationAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CheckInitialConfigurationAsync()
    {
        if (_storageService == null || _authService == null) return;

        var config = await _storageService.LoadConfigAsync();
        if (!config.HasCredentials)
        {
            var imported = await _authService.TryImportFromClaudeCliAsync();
            if (imported != null)
            {
                config.AuthTokens = imported;
                await _storageService.SaveConfigAsync(config);
                if (_orchestrator != null)
                {
                    _ = _orchestrator.SyncOnceAsync();
                }
                return;
            }

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

        if (_storageService == null || _apiService == null || _authService == null || _orchestrator == null)
            return;

        var viewModel = new SettingsViewModel(_storageService, _apiService, _authService);
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

        try
        {
            var iconRenderer = new IconRendererService();
            _settingsWindow.Icon = iconRenderer.RenderAppIconWindowIcon(64);
        }
        catch
        {
            // Fallback if platform icon loader is not initialized
        }

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
