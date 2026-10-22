using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Logging;
using ZipP2P.App.ViewModels;
using ZipP2P.App.Views;

namespace ZipP2P.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Instance.CaptureUnhandledExceptions();
        AppLog.Instance.CreateLogger("App").LogInformation(
            "ZipStream {Version} started on {OS} ({Arch}), .NET {Runtime}",
            AppLog.AppVersion, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture, Environment.Version);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var command = StartupCommand.Parse(desktop.Args ?? Array.Empty<string>());
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };

            // Executa depois de abrir: os seletores/área de transferência vêm da janela.
            window.Opened += async (_, _) => await viewModel.RunStartupCommandAsync(command);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}