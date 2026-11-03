using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Devices;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;
using ZipP2P.App.ViewModels;
using ZipP2P.App.Views;

namespace ZipP2P.App;

/// <summary>
/// O app vive na bandeja do sistema (área de notificação no Windows/Linux, barra de menus no macOS):
/// fechar a janela principal só a esconde. "Enviar com ZipStream" abre uma janela compacta com o
/// código, sem a janela principal; pedidos de outras instâncias chegam por <see cref="SingleInstance"/>.
/// </summary>
public partial class App : Application
{
    private static readonly ILogger Logger = AppLog.Instance.CreateLogger("App");

    private readonly SingleInstance _singleInstance = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private TrayIcon? _trayIcon;
    private bool _exiting;

    /// <summary>Comando da linha de comando desta instância (definido em <see cref="Program"/>).</summary>
    public static StartupCommand StartupCommand { get; set; } = StartupCommand.None;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Instance.CaptureUnhandledExceptions();
        Logger.LogInformation(
            "ZipStream {Version} started on {OS} ({Arch}), .NET {Runtime}",
            AppLog.AppVersion, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture, Environment.Version);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _mainViewModel = new MainViewModel();
            _mainWindow = new MainWindow { DataContext = _mainViewModel };
            _mainWindow.Closing += OnMainWindowClosing;
            // Não vira desktop.MainWindow aqui: o Avalonia mostraria a janela ao iniciar, inclusive no
            // "Enviar com ZipStream" (só a janela compacta) e no --background (só a bandeja).
            // ShowMainWindow a atribui quando ela deve aparecer.

            CreateTrayIcon();

            // Receive mode ligado da última vez: volta a atender convites (também ao iniciar com o sistema).
            if (new AppSettings(new SettingsStore()).ReceiveModeEnabled)
                DeviceServices.ReceiveMode.Start();

            _singleInstance.Listen(
                command => Dispatcher.UIThread.Post(() => Handle(command)),
                ex => Logger.LogDebug(ex, "Single-instance channel error"));

            Handle(StartupCommand);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        if (_desktop is not null)
            _desktop.MainWindow ??= _mainWindow;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void Exit()
    {
        _exiting = true;
        Logger.LogInformation("Exiting");
        _ = DeviceServices.ReceiveMode.StopAsync();
        _singleInstance.Dispose();
        _trayIcon?.Dispose();
        _desktop?.Shutdown();
    }

    private void Handle(StartupCommand command)
    {
        if (command.CloseForUpdate)
        {
            // Pedido do instalador: sai de verdade. Transferências em andamento ficam pendentes e
            // podem ser retomadas quando o app abrir de novo.
            Logger.LogInformation("Closing for update");
            Exit();
            return;
        }

        // Iniciado com o sistema: fica só na bandeja (o receive mode, se ligado, já está atendendo).
        if (command.Background)
            return;

        if (command.SendPath is { } path)
        {
            Logger.LogInformation("Quick send requested for {Path}", path);
            var viewModel = new SendViewModel { CopyCodeAutomatically = true };
            new QuickSendWindow { DataContext = viewModel }.Show();
            _ = viewModel.SendFromShellAsync(path);
            return;
        }

        ShowMainWindow();
        if (command.ReceiveCode is { } code)
            _mainViewModel?.ShowReceive(code);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || !new AppSettings(new SettingsStore()).CloseToTray)
        {
            if (!_exiting)
                Exit();
            return;
        }

        // Continua rodando na bandeja (transferências e "Enviar com ZipStream" seguem funcionando).
        e.Cancel = true;
        _mainWindow?.Hide();
        Logger.LogInformation("Main window hidden; ZipStream keeps running in the tray");
    }

    private void CreateTrayIcon()
    {
        var open = new NativeMenuItem();
        open.Click += (_, _) => ShowMainWindow();
        var exit = new NativeMenuItem();
        exit.Click += (_, _) => Exit();

        // Liga/desliga o receive mode sem abrir a janela.
        var receiveMode = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox };
        receiveMode.Click += (_, _) =>
        {
            var enable = !DeviceServices.ReceiveMode.IsRunning;
            new AppSettings(new SettingsStore()).ReceiveModeEnabled = enable;
            if (enable)
                DeviceServices.ReceiveMode.Start();
            else
                _ = DeviceServices.ReceiveMode.StopAsync();
        };
        DeviceServices.ReceiveMode.StateChanged += () =>
            Dispatcher.UIThread.Post(() => receiveMode.IsChecked = DeviceServices.ReceiveMode.IsRunning);
        receiveMode.IsChecked = DeviceServices.ReceiveMode.IsRunning;

        // Recebido em segundo plano: aparece no tooltip do ícone.
        DeviceServices.ReceiveMode.Activity += e =>
        {
            if (e.Kind == ReceiveModeEventKind.Received)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon is not null)
                        _trayIcon.ToolTipText = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Tray.Received"], e.Name);
                });
        };

        void Localize()
        {
            open.Header = Localizer.Instance["Tray.Open"];
            exit.Header = Localizer.Instance["Tray.Exit"];
            receiveMode.Header = Localizer.Instance["Tray.ReceiveMode"];
        }

        Localize();
        Localizer.Instance.PropertyChanged += (_, _) => Localize();

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ZipP2P.App/Assets/zipstream.ico"))),
            ToolTipText = "ZipStream",
            Menu = new NativeMenu { Items = { open, receiveMode, new NativeMenuItemSeparator(), exit } },
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }
}
