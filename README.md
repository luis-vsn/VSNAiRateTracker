# VSNAiRateTracker

Aplicación de escritorio ligera para la bandeja del sistema (*System Tray* / *Menu Bar*) construida con **.NET 10 (LTS)** y **Avalonia UI**, diseñada para monitorizar el consumo de cuota de la API de Anthropic y calcular el desfase horario (*burndown rate*) respecto a la jornada laboral configurada por el usuario.

---

## Características Principales

* **Icono dinámico en la bandeja del sistema (System Tray):**
  * Renderizado en memoria a 64x64 píxeles mediante **SkiaSharp**.
  * Código de colores de semáforo intuitivo:
    * 🟢 **Verde (`#2ECC71`):** `DeltaHours >= 0h` (Dentro de margen o con colchón de cuota).
    * 🟠 **Ámbar (`#F39C12`):** `0 > DeltaHours >= -2h` (Retraso leve de consumo).
    * 🔴 **Rojo (`#E74C3C`):** `DeltaHours < -2h` (Sobredetracción severa de cuota).
  * Texto corto centrado indicando el desfase exacto (ej. `+4h`, `0h`, `-2h`).
  * Tooltip reactivo: `Anthropic: [X]% gastado | Desfase: [Y] horas`.

* **Motor matemático de Burndown (`BurnDownCalculator`):**
  * Cálculo de horas laborables totales ($T_{\text{total}}$) y transcurridas ($T_{\text{elapsed}}$) en el ciclo de reset.
  * Considera días de la semana activos, horarios de entrada y salida, fines de semana y franjas fuera de jornada (las horas no laborables no acumulan tiempo indebidamente).
  * Soporte de zonas horarias locales o personalizadas.

* **Almacenamiento seguro autenticado (`SecureStorageService`):**
  * Cifrado simétrico autenticado **AES-GCM (256 bits)**.
  * Protección de la clave maestra en disco mediante **DPAPI** de Windows (`ProtectedData`), asegurando que ningún token sensible se guarde en texto claro en `%APPDATA%/VSNAiRateTracker/config.enc`.

* **Cliente API Anthropic Resiliente (`AnthropicApiService`):**
  * Consulta periódica cada 5 minutos (configurable).
  * Resiliencia ante errores transitorios (`429 Too Many Requests`, `5xx`) con reintentos y retroceso exponencial (*Exponential Backoff*).
  * Sin bucles de reintento ante errores de autenticación (`401 Unauthorized`).
  * Captura controlada de excepciones de red para garantizar la estabilidad del temporizador.

* **Ventana de configuración Fluent UI (`SettingsWindow`):**
  * Configuración de la clave API de Anthropic con opción de validación en tiempo real.
  * Selección de días laborables y franja horaria.
  * Intervalo de actualización configurable.
  * La aplicación opera en modo residente (`ShutdownMode.OnExplicitShutdown`), por lo que cerrar la ventana no termina el proceso de fondo.

---

## Estructura del Proyecto

```text
VSNAiRateTracker/
├── VSNAiRateTracker.slnx
├── README.md
├── VSNAiRateTracker/
│   ├── Program.cs                 # Punto de entrada de la aplicación
│   ├── App.axaml                  # Estilos globales de Avalonia
│   ├── App.axaml.cs               # Ciclo de vida residente y orquestación
│   ├── Models/
│   │   ├── AppConfig.cs           # Horarios, zonas horarias, claves e intervalos
│   │   ├── AnthropicUsageResult.cs# DTO de respuesta y métricas de la API
│   │   └── UsageState.cs          # Estado de consumo y semáforo
│   ├── Services/
│   │   ├── AnthropicApiService.cs # Cliente HTTP resiliente con reintentos
│   │   ├── BurnDownCalculator.cs  # Motor matemático de cálculo de desfase
│   │   ├── IconRendererService.cs # Generación de bitmaps dinámicos con SkiaSharp
│   │   ├── SecureStorageService.cs# Cifrado AES-GCM y gestión de claves
│   │   ├── SyncOrchestrator.cs    # Bucle periódico con PeriodicTimer
│   │   └── TrayIconManager.cs     # Integración con el System Tray de Windows
│   └── Views/
│       ├── SettingsWindow.axaml   # Ventana de configuración de credenciales y horario
│       └── SettingsWindow.axaml.cs
└── VSNAiRateTracker.Tests/        # Suite completa de tests unitarios (xUnit, FluentAssertions, Moq)
```

---

## Compilación y Ejecución

### Requisitos
* SDK de [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) o superior.
* Windows 10 / 11 (plataforma principal actual).

### Compilar la solución
```bash
dotnet build
```

### Ejecutar la suite de tests (TDD)
```bash
dotnet test
```

### Ejecutar la aplicación
```bash
dotnet run --project VSNAiRateTracker/VSNAiRateTracker.csproj
```

---

## Configuración de Telemetría OpenTelemetry (Grafana / Collector)

La aplicación puede emitir trazas, métricas y logs estructurados vía **OTLP HTTP (Protobuf)** para su visualización centralizada en Grafana o cualquier backend compatible con OpenTelemetry.

### 1. ¿Qué poner en el Endpoint?

* **Instancia autoalojada (OpenTelemetry Collector o Grafana Alloy):**
  - Si tienes un Collector corriendo en tu equipo o en la red local de la empresa:
    - `http://localhost:4318` o `http://<ip-o-servidor>:4318`
  - Si tu Collector o Reverse Proxy tiene TLS configurado:
    - `https://collector.miempresa.com:4318`
* **Grafana Cloud:**
  - En la consola de Grafana Cloud, ve a **Connections** > **OpenTelemetry** y copia el *OTLP Endpoint*.
  - Formato típico: `https://otlp-gateway-<region>.grafana.net/otlp` (por ejemplo `https://otlp-gateway-prod-eu-west-0.grafana.net/otlp`).

### 2. ¿Qué poner en las Cabeceras de Autorización (Authorization Headers)?

* **Instancia autoalojada:**
  - **Déjalo en blanco** si tu Collector local no requiere autenticación.
  - Si configuraste autenticación Bearer: `Authorization=Bearer <tu-token>`
* **Grafana Cloud:**
  - `Authorization=Basic <token_base64>`
  - Donde `<token_base64>` es la cadena Base64 generada a partir de `<Instance_ID>:<API_Token>` (Grafana Cloud te proporciona este valor directamente en la pantalla de configuración OTLP).

### 3. Identificación del equipo emisor (`instance`)

Cada métrica, log y traza enviada por la aplicación incluye de forma automática:
* `instance`: Nombre de la máquina donde se ejecuta la aplicación (`Environment.MachineName`), permitiendo filtrar métricas y dashboards de Grafana/Prometheus directamente por equipo.
* `service.name`: Nombre del servicio configurado (por defecto `vsn-ai-rate-tracker`).
* `service.version`: Versión de la aplicación (`0.0.1`).

---

## Estado Multiplataforma

* **Windows:** Completado e implementado al 100% (Issues #1 a #7 cerrados).
* **macOS:** Documentado en el **[Issue #8](https://github.com/luis-vsn/VSNAiRateTracker/issues/8)** para ser retomado por un compañero con hardware Mac (validación de Menu Bar, empaquetado DMG y soporte multilingüe).
