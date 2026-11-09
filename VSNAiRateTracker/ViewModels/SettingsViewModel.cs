using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;

namespace VSNAiRateTracker.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISecureStorageService _storageService;
    private readonly IAnthropicApiService _apiService;
    private readonly IAnthropicAuthService _authService;

    private string _apiKey = string.Empty;
    private OAuthTokens? _authTokens;
    private int _syncIntervalMinutes = 5;
    private bool _isMonday = true;
    private bool _isTuesday = true;
    private bool _isWednesday = true;
    private bool _isThursday = true;
    private bool _isFriday = true;
    private bool _isSaturday = false;
    private bool _isSunday = false;
    private TimeSpan _startTime = new(9, 0, 0);
    private TimeSpan _endTime = new(18, 30, 0);
    private string _timeZoneId = "Local";

    private string _statusMessage = string.Empty;
    private bool _isStatusSuccess;
    private bool _isTestingConnection;
    private bool _isLoggingIn;
    private string _sessionStatusText = "Sesión no iniciada.";
    private bool _isLoggedInWithWeb;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        ISecureStorageService storageService,
        IAnthropicApiService apiService,
        IAnthropicAuthService authService)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetField(ref _apiKey, value);
    }

    public OAuthTokens? AuthTokens
    {
        get => _authTokens;
        set
        {
            if (SetField(ref _authTokens, value))
            {
                UpdateSessionStatus();
            }
        }
    }

    public bool IsLoggedInWithWeb
    {
        get => _isLoggedInWithWeb;
        private set => SetField(ref _isLoggedInWithWeb, value);
    }

    public string SessionStatusText
    {
        get => _sessionStatusText;
        private set => SetField(ref _sessionStatusText, value);
    }

    public bool IsLoggingIn
    {
        get => _isLoggingIn;
        set => SetField(ref _isLoggingIn, value);
    }

    public int SyncIntervalMinutes
    {
        get => _syncIntervalMinutes;
        set => SetField(ref _syncIntervalMinutes, value);
    }

    public bool IsMonday
    {
        get => _isMonday;
        set => SetField(ref _isMonday, value);
    }

    public bool IsTuesday
    {
        get => _isTuesday;
        set => SetField(ref _isTuesday, value);
    }

    public bool IsWednesday
    {
        get => _isWednesday;
        set => SetField(ref _isWednesday, value);
    }

    public bool IsThursday
    {
        get => _isThursday;
        set => SetField(ref _isThursday, value);
    }

    public bool IsFriday
    {
        get => _isFriday;
        set => SetField(ref _isFriday, value);
    }

    public bool IsSaturday
    {
        get => _isSaturday;
        set => SetField(ref _isSaturday, value);
    }

    public bool IsSunday
    {
        get => _isSunday;
        set => SetField(ref _isSunday, value);
    }

    public TimeSpan StartTime
    {
        get => _startTime;
        set => SetField(ref _startTime, value);
    }

    public TimeSpan EndTime
    {
        get => _endTime;
        set => SetField(ref _endTime, value);
    }

    public string TimeZoneId
    {
        get => _timeZoneId;
        set => SetField(ref _timeZoneId, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsStatusSuccess
    {
        get => _isStatusSuccess;
        set => SetField(ref _isStatusSuccess, value);
    }

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        set => SetField(ref _isTestingConnection, value);
    }

    public void LoadFromConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ApiKey = config.ApiKey ?? string.Empty;
        AuthTokens = config.AuthTokens;
        SyncIntervalMinutes = config.SyncIntervalMinutes > 0 ? config.SyncIntervalMinutes : 5;
        TimeZoneId = config.Schedule?.TimeZoneId ?? "Local";

        var days = new HashSet<DayOfWeek>();
        TimeSpan? detectedStart = null;
        TimeSpan? detectedEnd = null;

        if (config.Schedule?.Slots != null)
        {
            foreach (var slot in config.Schedule.Slots)
            {
                days.Add(slot.DayOfWeek);
                detectedStart ??= slot.StartTime.ToTimeSpan();
                detectedEnd ??= slot.EndTime.ToTimeSpan();
            }
        }

        IsMonday = days.Contains(DayOfWeek.Monday);
        IsTuesday = days.Contains(DayOfWeek.Tuesday);
        IsWednesday = days.Contains(DayOfWeek.Wednesday);
        IsThursday = days.Contains(DayOfWeek.Thursday);
        IsFriday = days.Contains(DayOfWeek.Friday);
        IsSaturday = days.Contains(DayOfWeek.Saturday);
        IsSunday = days.Contains(DayOfWeek.Sunday);

        if (detectedStart.HasValue) StartTime = detectedStart.Value;
        if (detectedEnd.HasValue) EndTime = detectedEnd.Value;

        UpdateSessionStatus();
    }

    public AppConfig ToConfig()
    {
        var config = new AppConfig
        {
            ApiKey = ApiKey.Trim(),
            AuthTokens = AuthTokens,
            SyncIntervalMinutes = SyncIntervalMinutes,
            Schedule = new WorkSchedule
            {
                TimeZoneId = TimeZoneId
            }
        };

        var start = TimeOnly.FromTimeSpan(StartTime);
        var end = TimeOnly.FromTimeSpan(EndTime);

        if (IsMonday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, start, end));
        if (IsTuesday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Tuesday, start, end));
        if (IsWednesday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Wednesday, start, end));
        if (IsThursday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Thursday, start, end));
        if (IsFriday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Friday, start, end));
        if (IsSaturday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Saturday, start, end));
        if (IsSunday) config.Schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Sunday, start, end));

        return config;
    }

    public async Task<bool> LoginWithWebAsync()
    {
        IsLoggingIn = true;
        StatusMessage = "Abriendo el navegador para autenticarte con Anthropic...";
        IsStatusSuccess = true;

        try
        {
            var tokens = await _authService.StartInteractiveLoginAsync();
            AuthTokens = tokens;

            var config = ToConfig();
            config.AuthTokens = tokens;
            await _storageService.SaveConfigAsync(config);

            StatusMessage = "✓ Autenticación Web completada con éxito.";
            IsStatusSuccess = true;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error durante la autenticación web: {ex.Message}";
            IsStatusSuccess = false;
            return false;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    public async Task<bool> ImportFromClaudeCliAsync()
    {
        IsLoggingIn = true;
        StatusMessage = "Buscando credenciales activas de Claude Code en el equipo...";
        IsStatusSuccess = true;

        try
        {
            var tokens = await _authService.TryImportFromClaudeCliAsync();
            if (tokens != null)
            {
                AuthTokens = tokens;
                var config = ToConfig();
                config.AuthTokens = tokens;
                await _storageService.SaveConfigAsync(config);

                StatusMessage = "✓ Credenciales de Claude Code importadas y verificadas con éxito.";
                IsStatusSuccess = true;
                return true;
            }
            else
            {
                StatusMessage = "No se encontraron credenciales en ~/.claude/.credentials.json. Inicia sesión con el botón Web.";
                IsStatusSuccess = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error al importar sesión: {ex.Message}";
            IsStatusSuccess = false;
            return false;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    public async Task LogoutWebAsync()
    {
        AuthTokens = null;
        var config = ToConfig();
        config.AuthTokens = null;
        await _storageService.SaveConfigAsync(config);
        StatusMessage = "Sesión cerrada.";
        IsStatusSuccess = true;
    }

    public async Task<bool> TestConnectionAsync()
    {
        bool hasToken = AuthTokens != null && !string.IsNullOrWhiteSpace(AuthTokens.AccessToken);
        bool hasKey = !string.IsNullOrWhiteSpace(ApiKey);

        if (!hasToken && !hasKey)
        {
            StatusMessage = "Inicia sesión con Anthropic (Web) o introduce una API Key.";
            IsStatusSuccess = false;
            return false;
        }

        IsTestingConnection = true;
        StatusMessage = "Validando credenciales con Anthropic...";

        try
        {
            var isValid = await _apiService.ValidateCredentialsAsync(ApiKey.Trim(), AuthTokens);
            if (isValid)
            {
                StatusMessage = "✓ Conexión establecida correctamente con Anthropic.";
                IsStatusSuccess = true;
                return true;
            }
            else
            {
                StatusMessage = "✗ Error de conexión: credenciales inválidas o expiradas.";
                IsStatusSuccess = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error al conectar: {ex.Message}";
            IsStatusSuccess = false;
            return false;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        try
        {
            var config = ToConfig();
            await _storageService.SaveConfigAsync(config);
            StatusMessage = "✓ Configuración guardada correctamente.";
            IsStatusSuccess = true;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error al guardar configuración: {ex.Message}";
            IsStatusSuccess = false;
            return false;
        }
    }

    private void UpdateSessionStatus()
    {
        if (_authTokens != null && !string.IsNullOrWhiteSpace(_authTokens.AccessToken))
        {
            if (_authTokens.IsExpired())
            {
                IsLoggedInWithWeb = false;
                SessionStatusText = "⚠️ Sesión expirada. Vuelve a iniciar sesión.";
            }
            else
            {
                IsLoggedInWithWeb = true;
                SessionStatusText = $"✓ Conectado mediante Web Signing (Válido hasta {_authTokens.ExpiresAt.ToLocalTime():g})";
            }
        }
        else
        {
            IsLoggedInWithWeb = false;
            SessionStatusText = "Sesión no iniciada.";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
