using System.Globalization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipP2P.App.Localization;
using ZipP2P.Core.Compression;

namespace ZipP2P.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private IStorageProvider? _storage;
    private CancellationTokenSource? _cts;
    private string _statusKey = "Status.Ready";

    public MainViewModel()
    {
        Localizer.Instance.PropertyChanged += (_, _) => RefreshStatus();
        LanguageIndex = DetectLanguageIndex();
        RefreshStatus();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string InputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompressMode))]
    [NotifyPropertyChangedFor(nameof(IsDecompressMode))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial int CompressionPresetIndex { get; set; } = 1;

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    public bool IsCompressMode => ModeIndex == 0;

    public bool IsDecompressMode => ModeIndex == 1;

    private int SelectedLevel => CompressionPresetIndex switch
    {
        0 => 1,
        1 => 3,
        _ => 19,
    };

    public void SetStorageProvider(IStorageProvider storage) => _storage = storage;

    partial void OnLanguageIndexChanged(int value)
    {
        if (value >= 0 && value < Localizer.SupportedCultures.Count)
            Localizer.Instance.Culture = Localizer.SupportedCultures[value];
    }

    private void RefreshStatus() => StatusText = Localizer.Instance[_statusKey];

    private void SetStatus(string key)
    {
        _statusKey = key;
        RefreshStatus();
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        if (_storage is null)
            return;

        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Localizer.Instance["Home.DropHint"],
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        InputPath = file.TryGetLocalPath() ?? file.Name;
        SuggestOutputPath();
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (_storage is null)
            return;

        var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = SuggestedOutputName(),
            Title = Localizer.Instance["Action.Browse"],
        });

        if (file is not null)
            OutputPath = file.TryGetLocalPath() ?? file.Name;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || string.IsNullOrWhiteSpace(OutputPath))
            return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        Details = string.Empty;

        try
        {
            var progress = new Progress<CompressionProgress>(OnProgress);

            if (IsCompressMode)
            {
                SetStatus("Status.Compressing");
                await using var input = File.OpenRead(InputPath);
                await using var output = File.Create(OutputPath);
                await ChunkedCompressor.CompressAsync(
                    input,
                    output,
                    new CompressionOptions { ChunkSize = 4 * 1024 * 1024, Level = SelectedLevel },
                    progress,
                    _cts.Token).ConfigureAwait(true);
            }
            else
            {
                SetStatus("Status.Decompressing");
                await using var input = File.OpenRead(InputPath);
                await using var output = File.Create(OutputPath);
                await ChunkedDecompressor.DecompressAsync(
                    input,
                    output,
                    progress,
                    _cts.Token).ConfigureAwait(true);
            }

            ProgressPercent = 100;
            SetStatus("Status.Completed");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Status.Cancelled");
        }
        catch (Exception)
        {
            SetStatus("Status.Failed");
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() =>
        !IsBusy && !string.IsNullOrWhiteSpace(InputPath) && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanCancel() => IsBusy;

    private void OnProgress(CompressionProgress p)
    {
        ProgressPercent = p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes * 100.0 : 0;
        Details = $"{FormatBytes(p.BytesProcessed)} / {FormatBytes(p.TotalBytes)}";
    }

    private void SuggestOutputPath()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return;

        var dir = Path.GetDirectoryName(InputPath) ?? string.Empty;
        OutputPath = Path.Combine(dir, SuggestedOutputName());
    }

    private string SuggestedOutputName()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return string.Empty;

        if (IsCompressMode)
            return Path.GetFileNameWithoutExtension(InputPath) + ".zpp2";

        var name = Path.GetFileName(InputPath);
        return name.EndsWith(".zpp2", StringComparison.OrdinalIgnoreCase)
            ? name[..^5]
            : name + ".out";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "?";

        var culture = CultureInfo.CurrentCulture;
        return bytes switch
        {
            >= GB => (bytes / (double)GB).ToString("0.0", culture) + " GB",
            >= MB => (bytes / (double)MB).ToString("0.0", culture) + " MB",
            >= KB => (bytes / (double)KB).ToString("0.0", culture) + " KB",
            _ => bytes.ToString(culture) + " B",
        };
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
