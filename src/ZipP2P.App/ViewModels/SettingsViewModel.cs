using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;
using ZipP2P.App.ShellIntegration;
using ZipP2P.App.Updates;

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

    /// <summary>STUN/TURN (host:porta); vazio = só conexão direta.</summary>
    [ObservableProperty]
    public partial string IceHost { get; set; }

    [ObservableProperty]
    public partial string IceUsername { get; set; }

    [ObservableProperty]
    public partial string IcePassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStopped))]
    public partial bool IsServerRunning { get; set; }

    public bool IsServerStopped => !IsServerRunning;

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    /// <summary>Fechar a janela mantém o ZipStream na bandeja (salvo na hora).</summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    partial void OnCloseToTrayChanged(bool value) => _settings.CloseToTray = value;

    public SettingsViewModel(AppSettings settings, Action back, UpdateChecker? updates = null, Action? exitForUpdate = null)
    {
        _settings = settings;
        _back = back;
        _updates = updates ?? UpdateChecker.ForCurrentApp();
        _exitForUpdate = exitForUpdate ?? (() => (Avalonia.Application.Current as App)?.Exit());
        AutoCheckUpdates = settings.AutoCheckUpdates;
        UpdateStatus = _updates.IsDevelopmentBuild ? Localizer.Instance["Update.DevBuild"] : string.Empty;
        ServerUrl = settings.SignalingUrl;
        Port = settings.Port;
        var ice = IceServerConfig.FromUrls(settings.IceServers);
        IceHost = ice.Host;
        IceUsername = ice.Username;
        IcePassword = ice.Password;
        CloseToTray = settings.CloseToTray;
        IsServerRunning = EmbeddedSignalingServer.Instance.IsRunning;
        if (IsServerRunning)
            ServerStatus = BuildStatus();

        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            RefreshShell();
            OnPropertyChanged(nameof(CurrentVersion));
        };
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
        _settings.IceServers = new IceServerConfig(IceHost, IceUsername, IcePassword).ToUrls();
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

    // ---- Atualizações ----

    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromDays(1);

    private readonly UpdateChecker _updates;
    private readonly Action _exitForUpdate;

    public string CurrentVersion => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.CurrentVersion"], _updates.CurrentVersion);

    public bool CanCheckForUpdates => !_updates.IsDevelopmentBuild;

    /// <summary>Procurar versões novas ao abrir o app (salvo na hora).</summary>
    [ObservableProperty]
    public partial bool AutoCheckUpdates { get; set; }

    partial void OnAutoCheckUpdatesChanged(bool value) => _settings.AutoCheckUpdates = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(CanInstallUpdate))]
    public partial AvailableUpdate? AvailableUpdate { get; set; }

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>Só no Windows o app baixa e roda o instalador; nos outros sistemas abre a página da release.</summary>
    public bool CanInstallUpdate => AvailableUpdate?.Installer is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdleForUpdates))]
    public partial bool IsWorkingOnUpdate { get; set; }

    public bool IsIdleForUpdates => !IsWorkingOnUpdate;

    [ObservableProperty]
    public partial string UpdateStatus { get; set; }

    /// <summary>Ao abrir o app: verifica em segundo plano se a opção estiver ligada e a última verificação tiver mais de um dia.</summary>
    public Task CheckForUpdatesOnStartupAsync()
    {
        var last = _settings.LastUpdateCheck;
        if (!AutoCheckUpdates || (last is { } at && DateTimeOffset.Now - at < AutoCheckInterval))
            return Task.CompletedTask;

        return CheckAsync(reportErrors: false);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckAsync(reportErrors: true);

    private async Task CheckAsync(bool reportErrors)
    {
        if (_updates.IsDevelopmentBuild || IsWorkingOnUpdate)
            return;

        IsWorkingOnUpdate = true;
        UpdateStatus = Localizer.Instance["Update.Checking"];
        try
        {
            AvailableUpdate = await _updates.CheckAsync();
            _settings.LastUpdateCheck = DateTimeOffset.Now;
            UpdateStatus = AvailableUpdate is { } update
                ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Available"], update.Version.ToString(3))
                : Localizer.Instance["Update.UpToDate"];
            Logger.LogInformation(
                "Update check: {Result}", AvailableUpdate is { } found ? $"version {found.Tag} available" : "up to date");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            Logger.LogWarning("Update check failed: {Message}", ex.Message);
            UpdateStatus = reportErrors
                ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Failed"], ex.Message)
                : string.Empty;
        }
        finally
        {
            IsWorkingOnUpdate = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate?.Installer is not { } installer || IsWorkingOnUpdate)
            return;

        IsWorkingOnUpdate = true;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus = string.Format(
                CultureInfo.CurrentCulture, Localizer.Instance["Update.Downloading"], p));
            var directory = Path.Combine(Path.GetTempPath(), "ZipStream-Update");
            var path = await _updates.DownloadAsync(installer, directory, progress);

            Logger.LogInformation("Starting installer {Installer}; closing for update", path);
            UpdateInstaller.RunInstaller(path);
            _exitForUpdate();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Update download/installation failed");
            UpdateStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Failed"], ex.Message);
        }
        finally
        {
            IsWorkingOnUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (AvailableUpdate is { } update)
            UpdateInstaller.OpenInBrowser(update.ReleasePage);
    }

    private string BuildStatus()
        => $"http://{NetworkInfo.GetLocalIpAddress()}:{Port}/signaling";
}
