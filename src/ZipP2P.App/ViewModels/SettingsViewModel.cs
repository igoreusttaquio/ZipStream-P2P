using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZipP2P.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    [ObservableProperty]
    public partial string ServerUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStopped))]
    public partial bool IsServerRunning { get; set; }

    public bool IsServerStopped => !IsServerRunning;

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        ServerUrl = settings.SignalingUrl;
        IsServerRunning = EmbeddedSignalingServer.Instance.IsRunning;
        if (IsServerRunning)
            ServerStatus = BuildStatus();
    }

    public void Save() => _settings.SignalingUrl = ServerUrl;

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
            await EmbeddedSignalingServer.Instance.StartAsync();
            IsServerRunning = true;
            ServerStatus = BuildStatus();
        }
    }

    private static string BuildStatus()
        => $"http://{NetworkInfo.GetLocalIpAddress()}:5000/signaling";
}
