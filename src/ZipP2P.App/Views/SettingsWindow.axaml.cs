using Avalonia.Controls;
using Avalonia.Interactivity;
using ZipP2P.App.ViewModels;

namespace ZipP2P.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Save();

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
