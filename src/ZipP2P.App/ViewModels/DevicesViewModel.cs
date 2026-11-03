using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Devices;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;
using ZipP2P.App.ShellIntegration;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

/// <summary>Dispositivo pareado na lista da aba Dispositivos.</summary>
public partial class TrustedDeviceItem : ObservableObject
{
    public TrustedDeviceItem(TrustedDevice device, Func<TrustedDeviceItem, Task> remove)
    {
        Device = device;
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public TrustedDevice Device { get; }

    public string Name => Device.Name;

    public string PairedText => string.Format(
        CultureInfo.CurrentCulture, Localizer.Instance["Devices.PairedOn"], Device.PairedAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineText))]
    public partial bool IsOnline { get; set; }

    public string OnlineText => Localizer.Instance[IsOnline ? "Devices.Online" : "Devices.Offline"];

    public IAsyncRelayCommand RemoveCommand { get; }
}

/// <summary>
/// Aba "Dispositivos": o receive mode deste computador (código, nome, pasta, iniciar com o sistema) e a
/// lista de dispositivos confiáveis, com o pareamento pelo código do outro computador.
/// </summary>
public partial class DevicesViewModel : ViewModelBase
{
    private const int MaxActivity = 8;

    private static readonly ILogger Logger = AppLog.Instance.CreateLogger("Devices");
    private static readonly TimeSpan OnlineRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings = new(new SettingsStore());
    private readonly ReceiveModeService _receiveMode = DeviceServices.ReceiveMode;
    private readonly Autostart _autostart = Autostart.ForCurrentUser();
    private bool _loading = true;

    public DevicesViewModel()
    {
        ReceiverCode = DeviceServices.Profile.ReceiverCode;
        DeviceName = DeviceServices.Profile.DeviceName;
        DownloadDirectory = _settings.DownloadDirectory;
        ReceiveModeEnabled = _receiveMode.IsRunning;
        StartWithSystem = _autostart.IsEnabled;
        _loading = false;

        _receiveMode.StateChanged += () => Dispatcher.UIThread.Post(RefreshReceiveState);
        _receiveMode.Activity += e => Dispatcher.UIThread.Post(() => OnActivity(e));
        _receiveMode.Progress += p => Dispatcher.UIThread.Post(() => ReceiveProgress = p.TotalBytes > 0 ? 100.0 * p.BytesTransferred / p.TotalBytes : 0);
        DeviceServices.TrustedChanged += () => Dispatcher.UIThread.Post(LoadDevices);
        Localizer.Instance.PropertyChanged += (_, _) => RefreshReceiveState();

        LoadDevices();
        RefreshReceiveState();

        // Indicador online/offline dos pareados.
        var timer = new DispatcherTimer { Interval = OnlineRefreshInterval };
        timer.Tick += (_, _) => _ = RefreshOnlineAsync();
        timer.Start();
    }

    // ---- Este computador ----

    [ObservableProperty]
    public partial bool ReceiveModeEnabled { get; set; }

    [ObservableProperty]
    public partial string ReceiveStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    public partial string ReceiverCode { get; set; }

    [ObservableProperty]
    public partial string CopyFeedback { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial string DownloadDirectory { get; set; }

    [ObservableProperty]
    public partial bool StartWithSystem { get; set; }

    /// <summary>Progresso do recebimento automático em andamento (0 = nenhum).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReceiving))]
    public partial double ReceiveProgress { get; set; }

    public bool IsReceiving => ReceiveProgress > 0 && ReceiveProgress < 100;

    public ObservableCollection<string> Activity { get; } = new();

    [ObservableProperty]
    public partial bool HasActivity { get; set; }

    partial void OnReceiveModeEnabledChanged(bool value)
    {
        if (_loading)
            return;

        _settings.ReceiveModeEnabled = value;
        if (value)
            _receiveMode.Start();
        else
            _ = _receiveMode.StopAsync();
    }

    partial void OnDeviceNameChanged(string value)
    {
        if (!_loading && !string.IsNullOrWhiteSpace(value))
            DeviceServices.Profile.DeviceName = value;
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_loading)
            return;

        try
        {
            if (value)
                _autostart.Enable(LaunchCommand.ForCurrentProcess());
            else
                _autostart.Disable();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not change the start-with-system setting");
        }
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (AppServices.Clipboard is null)
            return;

        await AppServices.Clipboard.SetTextAsync(ReceiverCode);
        CopyFeedback = Localizer.Instance["Send.CodeCopied"];
        await Task.Delay(2000);
        CopyFeedback = string.Empty;
    }

    [RelayCommand]
    private async Task RegenerateCodeAsync()
    {
        try
        {
            ReceiverCode = await _receiveMode.RegenerateCodeAsync();
            Logger.LogInformation("Receiver code regenerated");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not register the new receiver code");
            ReceiverCode = DeviceServices.Profile.ReceiverCode;
        }
    }

    [RelayCommand]
    private async Task ChangeDestinationAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var start = Directory.Exists(DownloadDirectory) ? await storage.TryGetFolderFromPathAsync(DownloadDirectory) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = Localizer.Instance["Receive.SaveTo"],
            SuggestedStartLocation = start,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            DownloadDirectory = path;
            _settings.DownloadDirectory = path;
        }
    }

    private void RefreshReceiveState()
    {
        // Pode ter sido ligado/desligado pela bandeja.
        _loading = true;
        ReceiveModeEnabled = _receiveMode.IsRunning;
        _loading = false;

        IsOnline = _receiveMode.IsOnline;
        ReceiveStatusText = Localizer.Instance[
            !_receiveMode.IsRunning ? "Devices.StatusOff"
            : _receiveMode.IsOnline ? "Devices.StatusOnline"
            : "Devices.StatusConnecting"];
    }

    private void OnActivity(ReceiveModeEvent e)
    {
        var device = e.DeviceName ?? Localizer.Instance["Devices.UnknownDevice"];
        var text = e.Kind switch
        {
            ReceiveModeEventKind.Paired => Format("Devices.EventPaired", device),
            ReceiveModeEventKind.Receiving => Format("Devices.EventReceiving", device),
            ReceiveModeEventKind.Received => Format("Devices.EventReceived", e.Name, device),
            _ => Format("Devices.EventFailed", e.Error ?? string.Empty),
        };

        Activity.Insert(0, $"{DateTime.Now:HH:mm}  {text}");
        while (Activity.Count > MaxActivity)
            Activity.RemoveAt(Activity.Count - 1);
        HasActivity = true;

        if (e.Kind is ReceiveModeEventKind.Received or ReceiveModeEventKind.Failed)
            ReceiveProgress = 0;
        if (e.Kind == ReceiveModeEventKind.Paired)
            DeviceServices.NotifyTrustedChanged();
    }

    // ---- Dispositivos confiáveis ----

    public ObservableCollection<TrustedDeviceItem> Devices { get; } = new();

    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial string PairCode { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial bool IsPairing { get; set; }

    [ObservableProperty]
    public partial string PairStatus { get; set; } = string.Empty;

    private bool CanPair() => !IsPairing && !string.IsNullOrWhiteSpace(PairCode);

    [RelayCommand(CanExecute = nameof(CanPair))]
    private async Task PairAsync()
    {
        if (!Core.Security.ReceiverCode.TryNormalize(PairCode, out var code))
        {
            PairStatus = Localizer.Instance["Devices.InvalidCode"];
            return;
        }

        if (Core.Security.ReceiverCode.TryNormalize(ReceiverCode, out var own) && code == own)
        {
            PairStatus = Localizer.Instance["Devices.OwnCode"];
            return;
        }

        IsPairing = true;
        PairStatus = Localizer.Instance["Devices.Pairing"];
        try
        {
            var device = await DeviceServices.Connector.PairAsync(code);
            PairStatus = Format("Devices.PairedWith", device.Name);
            PairCode = string.Empty;
            DeviceServices.NotifyTrustedChanged();
        }
        catch (DeviceOfflineException)
        {
            PairStatus = Localizer.Instance["Devices.PairOffline"];
        }
        catch (PeerNotTrustedException)
        {
            PairStatus = Localizer.Instance["Devices.PairWrongCode"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Pairing failed");
            PairStatus = Format("Devices.EventFailed", ex.Message);
        }
        finally
        {
            IsPairing = false;
        }
    }

    [RelayCommand]
    private Task RefreshOnlineAsync() => UpdateOnlineAsync();

    private void LoadDevices()
    {
        Devices.Clear();
        foreach (var device in DeviceServices.Trusted.List())
            Devices.Add(new TrustedDeviceItem(device, RemoveAsync));
        HasDevices = Devices.Count > 0;
        _ = UpdateOnlineAsync();
    }

    private Task RemoveAsync(TrustedDeviceItem item)
    {
        DeviceServices.Trusted.Remove(item.Device.DeviceId);
        Logger.LogInformation("Removed trusted device {Name} ({DeviceId})", item.Name, item.Device.DeviceId);
        DeviceServices.NotifyTrustedChanged();
        return Task.CompletedTask;
    }

    private async Task UpdateOnlineAsync()
    {
        if (Devices.Count == 0)
            return;

        try
        {
            var online = await DeviceServices.Connector.GetOnlineAsync();
            foreach (var item in Devices)
                item.IsOnline = online.Contains(item.Device.DeviceId);
        }
        catch (Exception ex)
        {
            // Sem servidor: mostra todos como offline.
            Logger.LogDebug("Could not check which devices are online: {Message}", ex.Message);
            foreach (var item in Devices)
                item.IsOnline = false;
        }
    }

    private static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Localizer.Instance[key], args);
}
