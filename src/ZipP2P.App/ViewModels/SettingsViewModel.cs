using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZipP2P.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _back;

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
    }

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
        if (EmbeddedSignalingServer.Instance.IsRunning)
        {
            await EmbeddedSignalingServer.Instance.StopAsync();
            IsServerRunning = false;
            ServerStatus = string.Empty;
        }
        else
        {
            await EmbeddedSignalingServer.Instance.StartAsync($"http://0.0.0.0:{Port}");
            IsServerRunning = true;
            ServerStatus = BuildStatus();
        }
    }

    private string BuildStatus()
        => $"http://{NetworkInfo.GetLocalIpAddress()}:{Port}/signaling";
}
