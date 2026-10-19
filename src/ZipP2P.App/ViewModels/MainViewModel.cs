using System.Globalization;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipP2P.App.Localization;

namespace ZipP2P.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings = new(new SettingsStore());

    public HomeViewModel Home { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightMode))]
    public partial bool IsDarkMode { get; set; } = true;

    public bool IsLightMode => !IsDarkMode;

    [ObservableProperty]
    public partial bool IsServerRunning { get; set; }

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    public MainViewModel()
    {
        Home = new HomeViewModel();
        Settings = new SettingsViewModel(new AppSettings(new SettingsStore()), GoHome);
        CurrentPage = Home;
        LanguageIndex = DetectLanguageIndex();

        EmbeddedSignalingServer.Instance.StateChanged += () => Dispatcher.UIThread.Post(RefreshServerStatus);
        RefreshServerStatus();
    }

    public void SetStorageProvider(IStorageProvider storage) => Home.SetStorageProvider(storage);

    private void RefreshServerStatus()
    {
        IsServerRunning = EmbeddedSignalingServer.Instance.IsRunning;
        ServerStatus = IsServerRunning
            ? $"http://{NetworkInfo.GetLocalIpAddress()}:{_settings.Port}/signaling"
            : string.Empty;
    }

    [RelayCommand]
    private void GoHome() => CurrentPage = Home;

    [RelayCommand]
    private void GoSettings() => CurrentPage = Settings;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;

        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (value >= 0 && value < Localizer.SupportedCultures.Count)
            Localizer.Instance.Culture = Localizer.SupportedCultures[value];
    }

    private static int DetectLanguageIndex()
    {
        var current = CultureInfo.CurrentUICulture;

        for (var i = 0; i < Localizer.SupportedCultures.Count; i++)
        {
            if (string.Equals(Localizer.SupportedCultures[i].Name, current.Name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < Localizer.SupportedCultures.Count; i++)
        {
            if (string.Equals(
                    Localizer.SupportedCultures[i].TwoLetterISOLanguageName,
                    current.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
