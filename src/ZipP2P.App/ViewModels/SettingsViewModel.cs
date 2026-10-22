using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;
using ZipP2P.App.ShellIntegration;

namespace ZipP2P.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = AppLog.Instance.CreateLogger("Settings");

    private readonly AppSettings _settings;
    private readonly Action _back;
    private readonly IShellIntegration? _shell = ShellIntegrationFactory.ForCurrentOs();

    [ObservableProperty]
    public partial string ServerUrl { get; set; }

    [ObservableProperty]
    public partial int Port { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStopped))]
    public partial bool IsServerRunning { get; set; }

    public bool IsServerStopped => !IsServerRunning;

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    public SettingsViewModel(AppSettings settings, Action back)
    {
        _settings = settings;
        _back = back;
        ServerUrl = settings.SignalingUrl;
        Port = settings.Port;
        IsServerRunning = EmbeddedSignalingServer.Instance.IsRunning;
        if (IsServerRunning)
            ServerStatus = BuildStatus();

        Localizer.Instance.PropertyChanged += (_, _) => RefreshShell();
        RefreshShell();
    }

    public bool IsShellSupported => _shell is not null;

    [ObservableProperty]
    public partial bool IsShellInstalled { get; set; }

    [ObservableProperty]
    public partial string ShellStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShellHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShellNote { get; set; } = string.Empty;

    [RelayCommand]
    private void Save()
    {
        _settings.SignalingUrl = ServerUrl;
        _settings.Port = Port;
        Back();
    }

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private async Task ToggleServerAsync()
    {
        try
        {
            if (EmbeddedSignalingServer.Instance.IsRunning)
            {
                await EmbeddedSignalingServer.Instance.StopAsync();
                IsServerRunning = false;
                ServerStatus = string.Empty;
                Logger.LogInformation("Local signaling server stopped");
            }
            else
            {
                await EmbeddedSignalingServer.Instance.StartAsync($"http://0.0.0.0:{Port}");
                IsServerRunning = true;
                ServerStatus = BuildStatus();
                Logger.LogInformation("Local signaling server listening at {Url}", ServerStatus);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not start/stop the local signaling server on port {Port}", Port);
            ServerStatus = Localizer.Instance["Status.Failed"] + ": " + ex.Message;
        }
    }

    [RelayCommand]
    private void InstallShell() => ChangeShell(install: true);

    [RelayCommand]
    private void UninstallShell() => ChangeShell(install: false);

    private void ChangeShell(bool install)
    {
        if (_shell is null)
            return;

        try
        {
            if (install)
            {
                var command = LaunchCommand.ForCurrentProcess();
                _shell.Install(command, Localizer.Instance["Shell.MenuLabel"]);
                Logger.LogInformation("Context menu installed for {Target} ({Executable})", _shell.TargetDescription, command.Executable);
            }
            else
            {
                _shell.Uninstall();
                Logger.LogInformation("Context menu removed from {Target}", _shell.TargetDescription);
            }

            RefreshShell();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not change the context menu integration");
            ShellStatusText = Localizer.Instance["Status.Failed"] + ": " + ex.Message;
        }
    }

    private void RefreshShell()
    {
        if (_shell is null)
            return;

        var status = _shell.GetStatus(LaunchCommand.ForCurrentProcess());
        IsShellInstalled = status != ShellIntegrationStatus.NotInstalled;
        ShellStatusText = Localizer.Instance[status switch
        {
            ShellIntegrationStatus.Installed => "Settings.ShellInstalled",
            ShellIntegrationStatus.Outdated => "Settings.ShellOutdated",
            _ => "Settings.ShellNotInstalled",
        }];
        ShellHint = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.ShellHint"], _shell.TargetDescription);
        ShellNote = Localizer.Instance[
            OperatingSystem.IsWindows() ? "Settings.ShellNoteWindows"
            : OperatingSystem.IsMacOS() ? "Settings.ShellNoteMac"
            : "Settings.ShellNoteLinux"];
    }

    private string BuildStatus()
        => $"http://{NetworkInfo.GetLocalIpAddress()}:{Port}/signaling";
}
