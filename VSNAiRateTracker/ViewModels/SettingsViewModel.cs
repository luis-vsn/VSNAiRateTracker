using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;

namespace VSNAiRateTracker.ViewModels;

public class DayScheduleItemViewModel : INotifyPropertyChanged
{
    private DayOfWeek _day;
    private string _dayName = string.Empty;
    private bool _isEnabled;
    private TimeSpan _startTime;
    private TimeSpan _endTime;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DayScheduleItemViewModel(DayOfWeek day, string dayName, bool isEnabled, TimeSpan start, TimeSpan end)
    {
        _day = day;
        _dayName = dayName;
        _isEnabled = isEnabled;
        _startTime = start;
        _endTime = end;
    }

    public DayOfWeek Day
    {
        get => _day;
        set
        {
            if (SetField(ref _day, value))
            {
                OnPropertyChanged(nameof(DayName));
            }
        }
    }

    public string DayName
    {
        get => string.IsNullOrEmpty(_dayName) ? LocalizationService.Instance.GetString($"Day.{_day}") : _dayName;
        set => SetField(ref _dayName, value);
    }

    public string StartLabel => LocalizationService.Instance.GetString("Schedule.Start") + ":";
    public string EndLabel => LocalizationService.Instance.GetString("Schedule.End") + ":";

    public void RefreshLocalization()
    {
        _dayName = LocalizationService.Instance.GetString($"Day.{_day}");
        OnPropertyChanged(nameof(DayName));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(EndLabel));
    }

    public void RefreshDayName() => RefreshLocalization();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
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

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISecureStorageService _storageService;
    private readonly IAnthropicApiService _apiService;
    private readonly IAnthropicAuthService _authService;

    private string _apiKey = string.Empty;
    private OAuthTokens? _authTokens;
    private int _syncIntervalMinutes = 5;
    private string _timeZoneId = "Local";

    private double _optimalMarginHours = 0.5;
    private double _highRateMarginHours = 5.0;
    private string _colorOptimalHex = "#2ECC71";
    private string _colorWarningFastHex = "#F39C12";
    private string _colorDangerFastHex = "#E74C3C";
    private string _colorSlowHex = "#3498DB";

    private Color _colorOptimal = Color.Parse("#2ECC71");
    private Color _colorWarningFast = Color.Parse("#F39C12");
    private Color _colorDangerFast = Color.Parse("#E74C3C");
    private Color _colorSlow = Color.Parse("#3498DB");

    private bool _telemetryEnabled;
    private string _telemetryEndpoint = string.Empty;
    private string _telemetryHeaders = string.Empty;
    private string _telemetryServiceName = "vsn-ai-rate-tracker";

    private string _statusMessage = string.Empty;
    private bool _isStatusSuccess;
    private bool _isTestingConnection;
    private bool _isLoggingIn;
    private string _sessionStatusText = "Sesión no iniciada.";
    private bool _isLoggedInWithWeb;

    private string _selectedLanguage = "Español";
    public ObservableCollection<string> AvailableLanguages { get; } = new() { "Español", "English" };

    public ObservableCollection<DayScheduleItemViewModel> DaySchedules { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => LocalizationService.Instance.GetString(key);
    public string MachineName => Environment.MachineName;

    public SettingsViewModel(
        ISecureStorageService storageService,
        IAnthropicApiService apiService,
        IAnthropicAuthService authService)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            RefreshDayNames();
            UpdateSessionStatus();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        };

        InitializeDefaultDays();
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetField(ref _selectedLanguage, value))
            {
                var code = value == "English" ? "en" : "es";
                LocalizationService.Instance.CurrentLanguage = code;
                RefreshDayNames();
                UpdateSessionStatus();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }
    }

    public void RefreshDayNames()
    {
        foreach (var item in DaySchedules)
        {
            item.RefreshDayName();
        }
    }

    private void InitializeDefaultDays()
    {
        DaySchedules.Clear();
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Monday, LocalizationService.Instance.GetString("Day.Monday"), true, new TimeSpan(9, 0, 0), new TimeSpan(18, 30, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Tuesday, LocalizationService.Instance.GetString("Day.Tuesday"), true, new TimeSpan(9, 0, 0), new TimeSpan(18, 30, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Wednesday, LocalizationService.Instance.GetString("Day.Wednesday"), true, new TimeSpan(9, 0, 0), new TimeSpan(18, 30, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Thursday, LocalizationService.Instance.GetString("Day.Thursday"), true, new TimeSpan(9, 0, 0), new TimeSpan(18, 30, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Friday, LocalizationService.Instance.GetString("Day.Friday"), true, new TimeSpan(9, 0, 0), new TimeSpan(18, 30, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Saturday, LocalizationService.Instance.GetString("Day.Saturday"), false, new TimeSpan(9, 0, 0), new TimeSpan(14, 0, 0)));
        DaySchedules.Add(new DayScheduleItemViewModel(DayOfWeek.Sunday, LocalizationService.Instance.GetString("Day.Sunday"), false, new TimeSpan(9, 0, 0), new TimeSpan(14, 0, 0)));
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

    public double OptimalMarginHours
    {
        get => _optimalMarginHours;
        set => SetField(ref _optimalMarginHours, value);
    }

    public double HighRateMarginHours
    {
        get => _highRateMarginHours;
        set => SetField(ref _highRateMarginHours, value);
    }

    public Color ColorOptimal
    {
        get => _colorOptimal;
        set
        {
            if (SetField(ref _colorOptimal, value))
            {
                _colorOptimalHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorOptimalHex)));
            }
        }
    }

    public Color ColorWarningFast
    {
        get => _colorWarningFast;
        set
        {
            if (SetField(ref _colorWarningFast, value))
            {
                _colorWarningFastHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorWarningFastHex)));
            }
        }
    }

    public Color ColorDangerFast
    {
        get => _colorDangerFast;
        set
        {
            if (SetField(ref _colorDangerFast, value))
            {
                _colorDangerFastHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorDangerFastHex)));
            }
        }
    }

    public Color ColorSlow
    {
        get => _colorSlow;
        set
        {
            if (SetField(ref _colorSlow, value))
            {
                _colorSlowHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorSlowHex)));
            }
        }
    }

    public string ColorOptimalHex
    {
        get => _colorOptimalHex;
        set
        {
            if (SetField(ref _colorOptimalHex, value))
            {
                if (Color.TryParse(value, out var c))
                {
                    _colorOptimal = c;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorOptimal)));
                }
            }
        }
    }

    public string ColorWarningFastHex
    {
        get => _colorWarningFastHex;
        set
        {
            if (SetField(ref _colorWarningFastHex, value))
            {
                if (Color.TryParse(value, out var c))
                {
                    _colorWarningFast = c;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorWarningFast)));
                }
            }
        }
    }

    public string ColorDangerFastHex
    {
        get => _colorDangerFastHex;
        set
        {
            if (SetField(ref _colorDangerFastHex, value))
            {
                if (Color.TryParse(value, out var c))
                {
                    _colorDangerFast = c;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorDangerFast)));
                }
            }
        }
    }

    public string ColorSlowHex
    {
        get => _colorSlowHex;
        set
        {
            if (SetField(ref _colorSlowHex, value))
            {
                if (Color.TryParse(value, out var c))
                {
                    _colorSlow = c;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorSlow)));
                }
            }
        }
    }

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set => SetField(ref _telemetryEnabled, value);
    }

    public string TelemetryEndpoint
    {
        get => _telemetryEndpoint;
        set => SetField(ref _telemetryEndpoint, value);
    }

    public string TelemetryHeaders
    {
        get => _telemetryHeaders;
        set => SetField(ref _telemetryHeaders, value);
    }

    public string TelemetryServiceName
    {
        get => _telemetryServiceName;
        set => SetField(ref _telemetryServiceName, value);
    }

    // Compatibilidad hacia atrás para días individuales
    public bool IsMonday
    {
        get => GetDaySchedule(DayOfWeek.Monday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Monday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsTuesday
    {
        get => GetDaySchedule(DayOfWeek.Tuesday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Tuesday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsWednesday
    {
        get => GetDaySchedule(DayOfWeek.Wednesday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Wednesday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsThursday
    {
        get => GetDaySchedule(DayOfWeek.Thursday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Thursday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsFriday
    {
        get => GetDaySchedule(DayOfWeek.Friday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Friday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsSaturday
    {
        get => GetDaySchedule(DayOfWeek.Saturday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Saturday); if (d != null) d.IsEnabled = value; }
    }

    public bool IsSunday
    {
        get => GetDaySchedule(DayOfWeek.Sunday)?.IsEnabled ?? false;
        set { var d = GetDaySchedule(DayOfWeek.Sunday); if (d != null) d.IsEnabled = value; }
    }

    public TimeSpan StartTime
    {
        get => GetDaySchedule(DayOfWeek.Monday)?.StartTime ?? new TimeSpan(9, 0, 0);
        set
        {
            foreach (var d in DaySchedules) d.StartTime = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan EndTime
    {
        get => GetDaySchedule(DayOfWeek.Monday)?.EndTime ?? new TimeSpan(18, 30, 0);
        set
        {
            foreach (var d in DaySchedules) d.EndTime = value;
            OnPropertyChanged();
        }
    }

    private DayScheduleItemViewModel? GetDaySchedule(DayOfWeek day) =>
        DaySchedules.FirstOrDefault(d => d.Day == day);

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

        if (!string.IsNullOrWhiteSpace(config.Language))
        {
            var isEn = config.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            SelectedLanguage = isEn ? "English" : "Español";
        }
        else
        {
            var detected = LocalizationService.DetectSystemLanguage();
            SelectedLanguage = detected == "en" ? "English" : "Español";
        }

        if (config.Schedule?.Slots != null && config.Schedule.Slots.Count > 0)
        {
            foreach (var item in DaySchedules)
            {
                var matching = config.Schedule.Slots.FirstOrDefault(s => s.DayOfWeek == item.Day);
                if (matching != null)
                {
                    item.IsEnabled = matching.IsEnabled;
                    item.StartTime = matching.StartTime.ToTimeSpan();
                    item.EndTime = matching.EndTime.ToTimeSpan();
                }
                else
                {
                    item.IsEnabled = false;
                }
            }
        }

        if (config.Thresholds != null)
        {
            OptimalMarginHours = config.Thresholds.OptimalMarginHours;
            HighRateMarginHours = config.Thresholds.HighRateMarginHours;
            ColorOptimalHex = config.Thresholds.ColorOptimalHex;
            ColorWarningFastHex = config.Thresholds.ColorWarningFastHex;
            ColorDangerFastHex = config.Thresholds.ColorDangerFastHex;
            ColorSlowHex = config.Thresholds.ColorSlowHex;
        }

        if (config.Telemetry != null)
        {
            TelemetryEnabled = config.Telemetry.Enabled;
            TelemetryEndpoint = config.Telemetry.Endpoint ?? string.Empty;
            TelemetryHeaders = config.Telemetry.Headers ?? string.Empty;
            TelemetryServiceName = config.Telemetry.ServiceName ?? "vsn-ai-rate-tracker";
        }

        UpdateSessionStatus();
    }

    public AppConfig ToConfig()
    {
        var config = new AppConfig
        {
            ApiKey = ApiKey.Trim(),
            AuthTokens = AuthTokens,
            SyncIntervalMinutes = SyncIntervalMinutes,
            Language = SelectedLanguage == "English" ? "en" : "es",
            Schedule = new WorkSchedule
            {
                TimeZoneId = TimeZoneId
            },
            Thresholds = new RateThresholdConfig
            {
                OptimalMarginHours = OptimalMarginHours,
                HighRateMarginHours = HighRateMarginHours,
                ColorOptimalHex = ColorOptimalHex,
                ColorWarningFastHex = ColorWarningFastHex,
                ColorDangerFastHex = ColorDangerFastHex,
                ColorSlowHex = ColorSlowHex
            },
            Telemetry = new TelemetryConfig
            {
                Enabled = TelemetryEnabled,
                Endpoint = TelemetryEndpoint.Trim(),
                Protocol = "HttpProtobuf",
                Headers = TelemetryHeaders.Trim(),
                ServiceName = TelemetryServiceName.Trim()
            }
        };

        foreach (var item in DaySchedules)
        {
            config.Schedule.Slots.Add(new WorkTimeSlot(
                item.Day,
                TimeOnly.FromTimeSpan(item.StartTime),
                TimeOnly.FromTimeSpan(item.EndTime),
                item.IsEnabled));
        }

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
            StatusMessage = LocalizationService.Instance.GetString("Common.SuccessSave");
            IsStatusSuccess = true;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.GetString("Common.ErrorSave", ex.Message);
            IsStatusSuccess = false;
            return false;
        }
    }

    private void UpdateSessionStatus()
    {
        var loc = LocalizationService.Instance;
        if (_authTokens != null && !string.IsNullOrWhiteSpace(_authTokens.AccessToken))
        {
            if (_authTokens.IsExpired())
            {
                IsLoggedInWithWeb = false;
                SessionStatusText = loc.GetString("Auth.SessionExpired");
            }
            else
            {
                IsLoggedInWithWeb = true;
                SessionStatusText = loc.GetString("Auth.SessionActive", _authTokens.ExpiresAt.ToLocalTime());
            }
        }
        else
        {
            IsLoggedInWithWeb = false;
            SessionStatusText = loc.GetString("Auth.SessionNotStarted");
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
