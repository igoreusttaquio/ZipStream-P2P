using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;

namespace ZipP2P.App.ViewModels;

public partial class LogsViewModel : ViewModelBase
{
    private readonly Action _back;

    public LogsViewModel(Action back)
    {
        _back = back;
        Reload();
        AppLog.Instance.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            if (ShowDebug || !entry.IsDebug)
                Entries.Add(entry);
            while (Entries.Count > AppLog.Capacity)
                Entries.RemoveAt(0);
        });
        AppLog.Instance.Cleared += () => Dispatcher.UIThread.Post(Entries.Clear);
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Mostra também as mensagens de depuração (ICE, SDP, candidatos...).</summary>
    [ObservableProperty]
    public partial bool ShowDebug { get; set; }

    [ObservableProperty]
    public partial string Feedback { get; set; } = string.Empty;

    partial void OnShowDebugChanged(bool value) => Reload();

    private void Reload()
    {
        Entries.Clear();
        foreach (var entry in AppLog.Instance.Snapshot())
        {
            if (ShowDebug || !entry.IsDebug)
                Entries.Add(entry);
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (AppServices.Clipboard is null)
            return;

        await AppServices.Clipboard.SetTextAsync(AppLog.Instance.Export(ShowDebug));
        await ShowFeedbackAsync(Localizer.Instance["Logs.Copied"]);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (AppServices.Storage is null)
            return;

        var file = await AppServices.Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["Logs.Export"],
            SuggestedFileName = $"zipstream-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } } },
        });
        if (file is null)
            return;

        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(AppLog.Instance.Export(includeDebug: true));

        await ShowFeedbackAsync(Localizer.Instance["Logs.Exported"]);
    }

    [RelayCommand]
    private void Clear() => AppLog.Instance.Clear();

    [RelayCommand]
    private void Back() => _back();

    private async Task ShowFeedbackAsync(string text)
    {
        Feedback = text;
        await Task.Delay(2500);
        if (Feedback == text)
            Feedback = string.Empty;
    }
}
