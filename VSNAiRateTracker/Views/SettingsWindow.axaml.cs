using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VSNAiRateTracker.ViewModels;

namespace VSNAiRateTracker.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? _viewModel;
    private readonly Action? _onSavedCallback;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel, Action? onSavedCallback = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _onSavedCallback = onSavedCallback;
        DataContext = _viewModel;
    }

    private async void OnTestConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.TestConnectionAsync();
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            var saved = await _viewModel.SaveAsync();
            if (saved)
            {
                _onSavedCallback?.Invoke();
                Close();
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
