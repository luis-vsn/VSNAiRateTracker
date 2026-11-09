using System;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class SyncOrchestrator : ISyncOrchestrator
{
    private readonly ISecureStorageService _storageService;
    private readonly IAnthropicApiService _apiService;
    private readonly IBurnDownCalculator _calculator;
    private readonly ITrayIconManager _trayManager;
    private readonly IAnthropicAuthService _authService;
    private readonly Action? _openSettingsAction;
    private readonly Action? _shutdownAction;

    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private bool _isDisposed;

    public SyncOrchestrator(
        ISecureStorageService storageService,
        IAnthropicApiService apiService,
        IBurnDownCalculator calculator,
        ITrayIconManager trayManager,
        IAnthropicAuthService authService,
        Action? openSettingsAction = null,
        Action? shutdownAction = null)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        _trayManager = trayManager ?? throw new ArgumentNullException(nameof(trayManager));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _openSettingsAction = openSettingsAction;
        _shutdownAction = shutdownAction;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _trayManager.Initialize(
            onOpenSettings: OpenSettings,
            onRefreshNow: () => SyncOnceAsync(_cts.Token),
            onExit: Shutdown);

        _backgroundTask = Task.Run(async () =>
        {
            // Initial immediate synchronization
            await SyncOnceAsync(_cts.Token);

            while (!_cts.Token.IsCancellationRequested)
            {
                var config = await _storageService.LoadConfigAsync(_cts.Token);
                var minutes = config.SyncIntervalMinutes > 0 ? config.SyncIntervalMinutes : 5;
                var interval = TimeSpan.FromMinutes(minutes);

                using var timer = new PeriodicTimer(interval);
                try
                {
                    if (await timer.WaitForNextTickAsync(_cts.Token))
                    {
                        await SyncOnceAsync(_cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);

        return Task.CompletedTask;
    }

    public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _storageService.LoadConfigAsync(cancellationToken);
            if (!config.HasCredentials)
            {
                _trayManager.SetStatus("LOG", "VSNAiRateTracker - Inicia sesión con Anthropic o configura una API Key", 0.0);
                return;
            }

            // Proactively refresh OAuth token if it's nearing expiration
            if (config.AuthTokens != null && config.AuthTokens.IsExpired(TimeSpan.FromMinutes(5)))
            {
                var refreshedTokens = await _authService.RefreshTokensAsync(config.AuthTokens, cancellationToken);
                if (refreshedTokens != null)
                {
                    config.AuthTokens = refreshedTokens;
                    await _storageService.SaveConfigAsync(config, cancellationToken);
                }
                else
                {
                    _trayManager.SetStatus("LOG", "VSNAiRateTracker - Sesión de Anthropic expirada. Haz clic para volver a iniciar sesión.", -2.0);
                    return;
                }
            }

            var result = await _apiService.GetUsageAsync(config.ApiKey, config.AuthTokens, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.IsSessionExpired)
                {
                    // Attempt token refresh on 401
                    if (config.AuthTokens != null)
                    {
                        var refreshedTokens = await _authService.RefreshTokensAsync(config.AuthTokens, cancellationToken);
                        if (refreshedTokens != null)
                        {
                            config.AuthTokens = refreshedTokens;
                            await _storageService.SaveConfigAsync(config, cancellationToken);
                            result = await _apiService.GetUsageAsync(config.ApiKey, config.AuthTokens, cancellationToken);
                        }
                    }

                    if (!result.IsSuccess)
                    {
                        _trayManager.SetStatus("LOG", "VSNAiRateTracker - Sesión de Anthropic expirada. Haz clic para iniciar sesión de nuevo.", -2.0);
                        return;
                    }
                }
                else
                {
                    _trayManager.SetStatus("ERR", $"Error consultando Anthropic: {result.ErrorMessage}", -3.0);
                    return;
                }
            }

            var usageState = _calculator.Calculate(
                config.Schedule,
                result.PercentConsumed,
                result.CycleStart,
                result.CycleEnd,
                DateTimeOffset.UtcNow);

            _trayManager.UpdateState(usageState);
        }
        catch (OperationCanceledException)
        {
            // Graceful cancellation
        }
        catch (Exception ex)
        {
            _trayManager.SetStatus("ERR", $"Error inesperado: {ex.Message}", -3.0);
        }
    }

    public void OpenSettings()
    {
        _openSettingsAction?.Invoke();
    }

    public void Shutdown()
    {
        if (_isDisposed)
            return;

        _cts.Cancel();
        _trayManager.Dispose();
        _shutdownAction?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _trayManager.Dispose();
    }
}
