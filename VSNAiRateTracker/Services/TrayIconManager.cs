using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class TrayIconManager : ITrayIconManager
{
    private readonly IIconRendererService _iconRenderer;
    private TrayIcon? _trayIcon;
    private bool _isDisposed;

    public TrayIconManager(IIconRendererService? iconRenderer = null)
    {
        _iconRenderer = iconRenderer ?? new IconRendererService();
    }

    public void Initialize(Action onOpenSettings, Func<Task> onRefreshNow, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onOpenSettings);
        ArgumentNullException.ThrowIfNull(onRefreshNow);
        ArgumentNullException.ThrowIfNull(onExit);

        Dispatcher.UIThread.Post(() =>
        {
            _trayIcon = new TrayIcon
            {
                ToolTipText = "VSNAiRateTracker - Inicializando...",
                IsVisible = true,
                Icon = _iconRenderer.RenderBadgeWindowIcon(0.0, "...", 64)
            };

            var menu = new NativeMenu();

            var refreshItem = new NativeMenuItem("Actualizar ahora");
            refreshItem.Click += async (_, _) =>
            {
                try
                {
                    await onRefreshNow();
                }
                catch
                {
                    // Handled internally in orchestrator
                }
            };
            menu.Add(refreshItem);

            var settingsItem = new NativeMenuItem("Configuración...");
            settingsItem.Click += (_, _) => onOpenSettings();
            menu.Add(settingsItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Salir");
            exitItem.Click += (_, _) => onExit();
            menu.Add(exitItem);

            _trayIcon.Menu = menu;
            _trayIcon.Clicked += (_, _) => onOpenSettings();

            if (Application.Current != null)
            {
                var trayIcons = TrayIcon.GetIcons(Application.Current);
                if (trayIcons == null)
                {
                    trayIcons = new TrayIcons();
                    TrayIcon.SetIcons(Application.Current, trayIcons);
                }
                trayIcons.Add(_trayIcon);
            }
        });
    }

    public void UpdateState(UsageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SetStatus(state.ShortText, state.TooltipText, state.DeltaHours);
    }

    public void SetStatus(string shortText, string tooltip, double deltaHours)
    {
        if (_isDisposed)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null)
                return;

            try
            {
                _trayIcon.Icon = _iconRenderer.RenderBadgeWindowIcon(deltaHours, shortText, 64);
                _trayIcon.ToolTipText = tooltip;
            }
            catch
            {
                // Silently avoid UI thread crashes if display is being torn down
            }
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = false;
                if (Application.Current != null)
                {
                    var icons = TrayIcon.GetIcons(Application.Current);
                    icons?.Remove(_trayIcon);
                }
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        });
    }
}
