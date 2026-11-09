using System;
using System.Collections.Generic;
using System.Globalization;

namespace VSNAiRateTracker.Services;

public class LocalizationService : ILocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private string _currentLanguage = "es";

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<string> SupportedLanguages { get; } = new[] { "es", "en" };

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            var normalized = NormalizeLanguage(value);
            if (_currentLanguage != normalized)
            {
                _currentLanguage = normalized;
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["App.Title"] = "Configuración - VSN AI Rate Tracker",
            ["Nav.Auth"] = "Autenticación Anthropic",
            ["Nav.AuthDesc"] = "Gestiona las credenciales de acceso a la API de Anthropic o Claude Code.",
            ["Nav.Schedule"] = "Horario Laboral Semanal",
            ["Nav.ScheduleDesc"] = "Define los días y franjas horarias laborables para el cómputo de cuota.",
            ["Nav.Thresholds"] = "Márgenes y Colores de Ritmo",
            ["Nav.ThresholdsDesc"] = "Ajusta los umbrales de horas y el color del valor en la bandeja del sistema.",
            ["Nav.Telemetry"] = "Telemetría OTLP (Grafana / Collector)",
            ["Nav.TelemetryDesc"] = "Emisión cifrada de métricas, trazas y logs hacia Grafana o Collector OTLP.",
            ["Nav.Language"] = "Idioma de la Aplicación",
            ["Nav.LanguageDesc"] = "Selecciona el idioma de la interfaz (Español o English).",

            ["Auth.WebSection"] = "Autenticación Web de Anthropic (Recomendado)",
            ["Auth.WebHelp"] = "Inicia sesión a través del navegador para vincular tu cuenta con OAuth o importar las credenciales de Claude Code.",
            ["Auth.BtnSignIn"] = "Iniciar sesión con Anthropic",
            ["Auth.BtnImport"] = "Importar desde Claude Code",
            ["Auth.BtnSignOut"] = "Cerrar sesión",
            ["Auth.SessionNotStarted"] = "Sesión no iniciada.",
            ["Auth.SessionExpired"] = "⚠️ Sesión expirada. Vuelve a iniciar sesión.",
            ["Auth.SessionActive"] = "✓ Conectado mediante Web Signing (Válido hasta {0:g})",

            ["Auth.ManualSection"] = "Configuración Manual (Opcional - API Key)",
            ["Auth.ApiKeyLabel"] = "Clave API de Anthropic (sk-ant-...):",
            ["Auth.ApiKeyPlaceholder"] = "Introduce tu clave sk-ant-... si no usas autenticación web",
            ["Auth.SyncInterval"] = "Intervalo de actualización (minutos):",
            ["Auth.BtnTest"] = "Probar conexión",

            ["Schedule.Day"] = "Día",
            ["Schedule.Active"] = "Laborable",
            ["Schedule.Start"] = "Entrada",
            ["Schedule.End"] = "Salida",
            ["Schedule.TimeZone"] = "Zona horaria (ej. Europe/Madrid o Local):",

            ["Day.Monday"] = "Lunes",
            ["Day.Tuesday"] = "Martes",
            ["Day.Wednesday"] = "Miércoles",
            ["Day.Thursday"] = "Jueves",
            ["Day.Friday"] = "Viernes",
            ["Day.Saturday"] = "Sábado",
            ["Day.Sunday"] = "Domingo",

            ["Threshold.OptimalMargin"] = "Margen de ritmo óptimo (± horas):",
            ["Threshold.HighMargin"] = "Umbral de ritmo excesivo (horas de adelanto):",
            ["Threshold.OptimalRate"] = "Ritmo Óptimo (|desfase| ≤ {0:F1}h):",
            ["Threshold.WarningRate"] = "Ritmo Acelerado (+{0:F1}h a +{1:F1}h):",
            ["Threshold.DangerRate"] = "Ritmo Excesivo (> +{0:F1}h):",
            ["Threshold.SlowRate"] = "Ritmo Lento (< -{0:F1}h):",

            ["RateStatus.Optimal"] = "Ritmo óptimo",
            ["RateStatus.WarningFast"] = "Ritmo acelerado",
            ["RateStatus.DangerFast"] = "Ritmo excesivo",
            ["RateStatus.Slow"] = "Ritmo lento",

            ["Threshold.ColorOptimal"] = "Óptimo",
            ["Threshold.ColorWarningFast"] = "Acelerado",
            ["Threshold.ColorDangerFast"] = "Excesivo",
            ["Threshold.ColorSlow"] = "Lento",

            ["Telemetry.Enable"] = "Habilitar exportación de telemetría OpenTelemetry (OTLP HTTP)",
            ["Telemetry.Endpoint"] = "Endpoint OTLP (ej. http://localhost:4318 o https://otlp-gateway-<region>.grafana.net/otlp):",
            ["Telemetry.Headers"] = "Cabecera de Autorización (opcional para autoalojado, o Authorization=Basic <token>):",
            ["Telemetry.ServiceName"] = "Nombre del servicio (service.name):",
            ["Telemetry.MachineInfo"] = "ℹ️ Se incluirá automáticamente en todas las métricas, trazas y logs el nombre de la máquina ({0}).",
            ["Telemetry.MachineNameLabel"] = "Nombre del equipo en métricas y trazas:",

            ["Common.Save"] = "Guardar configuración",
            ["Common.Cancel"] = "Cerrar",
            ["Common.SuccessSave"] = "✓ Configuración guardada correctamente.",
            ["Common.ErrorSave"] = "✗ Error al guardar configuración: {0}",
            ["Common.TestingConn"] = "Probando conexión con Anthropic...",
            ["Common.ConnSuccess"] = "✓ Conexión exitosa. Cuota consumida: {0:F1}%.",
            ["Common.ConnError"] = "✗ Error de conexión: {0}",
            ["Tooltip.Format"] = "Anthropic: {0:F1}% gastado | Desfase: {1} ({2})"
        },
        ["en"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["App.Title"] = "Settings - VSN AI Rate Tracker",
            ["Nav.Auth"] = "Anthropic Authentication",
            ["Nav.AuthDesc"] = "Manage Anthropic API or Claude Code access credentials.",
            ["Nav.Schedule"] = "Weekly Work Schedule",
            ["Nav.ScheduleDesc"] = "Define working days and time slots for quota burndown calculation.",
            ["Nav.Thresholds"] = "Rate Margins & Colors",
            ["Nav.ThresholdsDesc"] = "Configure hourly thresholds and tray icon text colors.",
            ["Nav.Telemetry"] = "OTLP Telemetry (Grafana / Collector)",
            ["Nav.TelemetryDesc"] = "Encrypted export of metrics, traces, and logs to Grafana or OTLP Collector.",
            ["Nav.Language"] = "Application Language",
            ["Nav.LanguageDesc"] = "Select interface language (Español or English).",

            ["Auth.WebSection"] = "Anthropic Web Signing (Recommended)",
            ["Auth.WebHelp"] = "Sign in via your browser using OAuth or import existing Claude Code credentials.",
            ["Auth.BtnSignIn"] = "Sign in with Anthropic",
            ["Auth.BtnImport"] = "Import from Claude Code",
            ["Auth.BtnSignOut"] = "Sign out",
            ["Auth.SessionNotStarted"] = "Not signed in.",
            ["Auth.SessionExpired"] = "⚠️ Session expired. Please sign in again.",
            ["Auth.SessionActive"] = "✓ Connected via Web Signing (Valid until {0:g})",

            ["Auth.ManualSection"] = "Manual Configuration (Optional - API Key)",
            ["Auth.ApiKeyLabel"] = "Anthropic API Key (sk-ant-...):",
            ["Auth.ApiKeyPlaceholder"] = "Enter your sk-ant-... key if not using Web Signing",
            ["Auth.SyncInterval"] = "Refresh interval (minutes):",
            ["Auth.BtnTest"] = "Test connection",

            ["Schedule.Day"] = "Day",
            ["Schedule.Active"] = "Working Day",
            ["Schedule.Start"] = "Start",
            ["Schedule.End"] = "End",
            ["Schedule.TimeZone"] = "Time zone (e.g. Europe/Madrid or Local):",

            ["Day.Monday"] = "Monday",
            ["Day.Tuesday"] = "Tuesday",
            ["Day.Wednesday"] = "Wednesday",
            ["Day.Thursday"] = "Thursday",
            ["Day.Friday"] = "Friday",
            ["Day.Saturday"] = "Saturday",
            ["Day.Sunday"] = "Sunday",

            ["Threshold.OptimalMargin"] = "Optimal rate margin (± hours):",
            ["Threshold.HighMargin"] = "Excessive rate threshold (hours ahead):",
            ["Threshold.OptimalRate"] = "Optimal Pace (|delta| ≤ {0:F1}h):",
            ["Threshold.WarningRate"] = "Accelerated Pace (+{0:F1}h to +{1:F1}h):",
            ["Threshold.DangerRate"] = "Excessive Pace (> +{0:F1}h):",
            ["Threshold.SlowRate"] = "Slow Pace (< -{0:F1}h):",

            ["RateStatus.Optimal"] = "Optimal pace",
            ["RateStatus.WarningFast"] = "Accelerated pace",
            ["RateStatus.DangerFast"] = "Excessive consumption",
            ["RateStatus.Slow"] = "Slow pace",

            ["Threshold.ColorOptimal"] = "Optimal",
            ["Threshold.ColorWarningFast"] = "Fast",
            ["Threshold.ColorDangerFast"] = "Excessive",
            ["Threshold.ColorSlow"] = "Slow",

            ["Telemetry.Enable"] = "Enable OpenTelemetry export (OTLP HTTP)",
            ["Telemetry.Endpoint"] = "OTLP Endpoint (e.g. http://localhost:4318 or https://otlp-gateway-<region>.grafana.net/otlp):",
            ["Telemetry.Headers"] = "Authorization Header (optional for self-hosted, or Authorization=Basic <token>):",
            ["Telemetry.ServiceName"] = "Service name (service.name):",
            ["Telemetry.MachineInfo"] = "ℹ️ Machine name ({0}) will be automatically attached to all metrics, traces, and logs.",
            ["Telemetry.MachineNameLabel"] = "Machine name in metrics and traces:",

            ["Common.Save"] = "Save settings",
            ["Common.Cancel"] = "Close",
            ["Common.SuccessSave"] = "✓ Settings saved successfully.",
            ["Common.ErrorSave"] = "✗ Error saving settings: {0}",
            ["Common.TestingConn"] = "Testing connection with Anthropic...",
            ["Common.ConnSuccess"] = "✓ Connection successful. Consumed quota: {0:F1}%.",
            ["Common.ConnError"] = "✗ Connection error: {0}",
            ["Tooltip.Format"] = "Anthropic: {0:F1}% used | Delta: {1} ({2})"
        }
    };

    public string GetString(string key)
    {
        if (Translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var val))
        {
            return val;
        }

        if (Translations["en"].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    public string GetString(string key, params object[] args)
    {
        var raw = GetString(key);
        try
        {
            return string.Format(raw, args);
        }
        catch
        {
            return raw;
        }
    }

    public static string DetectSystemLanguage()
    {
        try
        {
            var cultureName = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return cultureName == "es" ? "es" : "en";
        }
        catch
        {
            return "es";
        }
    }

    private static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "es";
        var lower = lang.Trim().ToLowerInvariant();
        if (lower.StartsWith("es") || lower == "español" || lower == "spanish") return "es";
        return "en";
    }
}
