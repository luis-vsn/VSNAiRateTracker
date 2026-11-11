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
    private NativeMenuItem? _refreshItem;
    private NativeMenuItem? _settingsItem;
    private NativeMenuItem? _exitItem;
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
            var loc = LocalizationService.Instance;
            _trayIcon = new TrayIcon
            {
                ToolTipText = loc.GetString("Tray.Initializing"),
                IsVisible = true,
                Icon = _iconRenderer.RenderTrayBadgeIcon(0.0, "...")
            };

            var menu = new NativeMenu();

            var refreshItem = new NativeMenuItem(loc.GetString("Tray.RefreshNow"));
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

            var settingsItem = new NativeMenuItem(loc.GetString("Tray.Settings"));
            settingsItem.Click += (_, _) => onOpenSettings();
            menu.Add(settingsItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem(loc.GetString("Tray.Exit"));
            exitItem.Click += (_, _) => onExit();
            menu.Add(exitItem);

            _trayIcon.Menu = menu;
            _refreshItem = refreshItem;
            _settingsItem = settingsItem;
            _exitItem = exitItem;
            loc.LanguageChanged += OnLanguageChanged;
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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            var loc = LocalizationService.Instance;
            if (_refreshItem != null) _refreshItem.Header = loc.GetString("Tray.RefreshNow");
            if (_settingsItem != null) _settingsItem.Header = loc.GetString("Tray.Settings");
            if (_exitItem != null) _exitItem.Header = loc.GetString("Tray.Exit");
        });
    }

    public void UpdateState(UsageState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_isDisposed)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null)
                return;

            try
            {
                _trayIcon.Icon = _iconRenderer.RenderTrayBadgeIcon(state.TrayText, state.StatusColorHex);
                _trayIcon.ToolTipText = state.TooltipText;
            }
            catch
            {
                // Silently avoid UI thread crashes if display is being torn down
            }
        });
    }

    public void SetAlert(string tooltip, bool isCritical = false)
    {
        if (_isDisposed)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null)
                return;

            try
            {
                _trayIcon.Icon = _iconRenderer.RenderTrayAlertIcon(isCritical);
                _trayIcon.ToolTipText = tooltip;
            }
            catch
            {
                // Silently avoid UI thread crashes if display is being torn down
            }
        });
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
                _trayIcon.Icon = _iconRenderer.RenderTrayBadgeIcon(deltaHours, shortText);
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
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

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

