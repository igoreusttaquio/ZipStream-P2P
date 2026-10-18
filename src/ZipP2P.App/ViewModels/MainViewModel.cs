using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using ZipP2P.App.Localization;
using ZipP2P.Core.Compression;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private const string SignalingServerUrl = "http://localhost:5000/signaling";

    private IStorageProvider? _storage;
    private CancellationTokenSource? _cts;
    private string _statusKey = "Status.Ready";

    public MainViewModel()
    {
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            RefreshStatus();
            OnPropertyChanged(nameof(StartButtonLabel));
        };
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
    [NotifyPropertyChangedFor(nameof(IsSendMode))]
    [NotifyPropertyChangedFor(nameof(IsReceiveMode))]
    [NotifyPropertyChangedFor(nameof(IsInputVisible))]
    [NotifyPropertyChangedFor(nameof(IsOutputVisible))]
    [NotifyPropertyChangedFor(nameof(IsRoomCodeVisible))]
    [NotifyPropertyChangedFor(nameof(StartButtonLabel))]
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
    public partial MaterialIconKind StatusIcon { get; set; } = MaterialIconKind.CheckCircleOutline;

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightMode))]
    public partial bool IsDarkMode { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsFolderArchive { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string RoomCode { get; set; } = string.Empty;

    public bool IsCompressMode => ModeIndex == 0;

    public bool IsDecompressMode => ModeIndex == 1;

    public bool IsSendMode => ModeIndex == 2;

    public bool IsReceiveMode => ModeIndex == 3;

    public bool IsLightMode => !IsDarkMode;

    public bool IsInputVisible => !IsReceiveMode;

    public bool IsOutputVisible => IsCompressMode || IsDecompressMode;

    public bool IsRoomCodeVisible => IsSendMode || IsReceiveMode;

    public string StartButtonLabel => Localizer.Instance[
        IsSendMode ? "Action.Send" : IsReceiveMode ? "Action.Receive" : "Action.Start"];

    private int SelectedLevel => CompressionPresetIndex switch
    {
        0 => 1,
        1 => 3,
        _ => 19,
    };

    public void SetStorageProvider(IStorageProvider storage) => _storage = storage;

    public void SetInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        InputPath = path;
        UpdateFolderArchive();
        SuggestOutputPath();
    }

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

    partial void OnModeIndexChanged(int value)
    {
        IsFolderArchive = false;
    }

    private void RefreshStatus()
    {
        StatusText = Localizer.Instance[_statusKey];

        switch (_statusKey)
        {
            case "Status.Compressing":
                StatusIcon = MaterialIconKind.Archive;
                StatusBrush = AccentStatusBrush;
                break;
            case "Status.Decompressing":
                StatusIcon = MaterialIconKind.Unarchive;
                StatusBrush = AccentStatusBrush;
                break;
            case "Status.WaitingForPeer":
                StatusIcon = MaterialIconKind.HourglassEmpty;
                StatusBrush = AccentStatusBrush;
                break;
            case "Status.Sending":
                StatusIcon = MaterialIconKind.Upload;
                StatusBrush = AccentStatusBrush;
                break;
            case "Status.Receiving":
                StatusIcon = MaterialIconKind.Download;
                StatusBrush = AccentStatusBrush;
                break;
            case "Status.Completed":
                StatusIcon = MaterialIconKind.CheckCircle;
                StatusBrush = SuccessBrush;
                break;
            case "Status.Cancelled":
                StatusIcon = MaterialIconKind.Cancel;
                StatusBrush = WarningBrush;
                break;
            case "Status.Failed":
                StatusIcon = MaterialIconKind.ErrorOutline;
                StatusBrush = ErrorBrush;
                break;
            default:
                StatusIcon = MaterialIconKind.CheckCircleOutline;
                StatusBrush = Brushes.Gray;
                break;
        }
    }

    private static readonly SolidColorBrush AccentStatusBrush = new(Color.Parse("#3B82F6"));
    private static readonly SolidColorBrush SuccessBrush = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush WarningBrush = new(Color.Parse("#F59E0B"));
    private static readonly SolidColorBrush ErrorBrush = new(Color.Parse("#EF4444"));

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

        if (IsCompressMode || IsSendMode)
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = Localizer.Instance["Home.DropHint"],
            });

            var folder = folders.FirstOrDefault();
            if (folder is not null)
                SetInputPath(folder.TryGetLocalPath() ?? folder.Name);
        }
        else
        {
            var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = Localizer.Instance["Home.DropHint"],
                FileTypeFilter = new FilePickerFileType[]
                {
                    new("ZipStream (.zpp2)") { Patterns = new[] { "*.zpp2" } },
                },
            });

            var file = files.FirstOrDefault();
            if (file is not null)
                SetInputPath(file.TryGetLocalPath() ?? file.Name);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (_storage is null)
            return;

        if (IsDecompressMode && IsFolderArchive)
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = Localizer.Instance["Action.Browse"],
            });

            var folder = folders.FirstOrDefault();
            if (folder is not null)
                OutputPath = folder.TryGetLocalPath() ?? folder.Name;
        }
        else
        {
            var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = SuggestedOutputName(),
                Title = Localizer.Instance["Action.Browse"],
            });

            if (file is not null)
                OutputPath = file.TryGetLocalPath() ?? file.Name;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        Details = string.Empty;

        try
        {
            if (IsSendMode)
                await SendTransferAsync(_cts.Token).ConfigureAwait(true);
            else if (IsReceiveMode)
                await ReceiveTransferAsync(_cts.Token).ConfigureAwait(true);
            else if (IsCompressMode)
                await CompressLocalAsync(_cts.Token).ConfigureAwait(true);
            else
                await DecompressLocalAsync(_cts.Token).ConfigureAwait(true);

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

    private bool CanStart()
    {
        if (IsBusy)
            return false;
        if (IsSendMode)
            return !string.IsNullOrWhiteSpace(InputPath);
        if (IsReceiveMode)
            return !string.IsNullOrWhiteSpace(RoomCode);
        return !string.IsNullOrWhiteSpace(InputPath) && !string.IsNullOrWhiteSpace(OutputPath);
    }

    private async Task CompressLocalAsync(CancellationToken cancellationToken)
    {
        SetStatus("Status.Compressing");
        var progress = new Progress<CompressionProgress>(OnProgress);
        var options = new CompressionOptions { ChunkSize = 4 * 1024 * 1024, Level = SelectedLevel };

        await using var output = File.Create(OutputPath);
        if (Directory.Exists(InputPath))
        {
            await FolderCodec.CompressAsync(InputPath, output, options, progress, cancellationToken)
                .ConfigureAwait(true);
        }
        else
        {
            await using var input = File.OpenRead(InputPath);
            await ChunkedCompressor.CompressAsync(input, output, options, progress, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task DecompressLocalAsync(CancellationToken cancellationToken)
    {
        SetStatus("Status.Decompressing");
        var progress = new Progress<CompressionProgress>(OnProgress);

        await using var input = File.OpenRead(InputPath);
        if (IsFolderArchive)
        {
            await FolderCodec.ExtractAsync(input, OutputPath, progress, cancellationToken)
                .ConfigureAwait(true);
        }
        else
        {
            await using var output = File.Create(OutputPath);
            await ChunkedDecompressor.DecompressAsync(input, output, progress, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task SendTransferAsync(CancellationToken cancellationToken)
    {
        RoomCode = GenerateRoomCode();

        var transport = new LibDataChannelTransport();
        var signaling = new SignalRSignalingClient(SignalingServerUrl);
        await using var session = new TransferSession(transport);
        var peer = new PeerConnection(transport, signaling, RoomCode);

        try
        {
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Connected += () => connected.TrySetResult();

            SetStatus("Status.WaitingForPeer");
            await peer.ConnectAsync(asOfferer: true, cancellationToken).ConfigureAwait(true);
            await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

            SetStatus("Status.Sending");
            var progress = new Progress<CompressionProgress>(OnProgress);
            await session.SendAsync(InputPath, progress, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await peer.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async Task ReceiveTransferAsync(CancellationToken cancellationToken)
    {
        var transport = new LibDataChannelTransport();
        var signaling = new SignalRSignalingClient(SignalingServerUrl);
        await using var session = new TransferSession(transport);
        var peer = new PeerConnection(transport, signaling, RoomCode);

        try
        {
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Connected += () => connected.TrySetResult();

            await peer.ConnectAsync(asOfferer: false, cancellationToken).ConfigureAwait(true);
            await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

            SetStatus("Status.Receiving");
            var progress = new Progress<CompressionProgress>(OnProgress);
            var destinationDir = Path.Combine(Path.GetTempPath(), "ZipStream-Recebidos");
            await session.ReceiveAsync(destinationDir, progress, cancellationToken).ConfigureAwait(true);

            Details = destinationDir;
        }
        finally
        {
            await peer.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanCancel() => IsBusy;

    private void OnProgress(CompressionProgress p)
    {
        ProgressPercent = p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes * 100.0 : 0;
        Details = $"{FormatBytes(p.BytesProcessed)} / {FormatBytes(p.TotalBytes)}";
    }

    private void UpdateFolderArchive()
    {
        if (IsDecompressMode && File.Exists(InputPath))
        {
            using var stream = File.OpenRead(InputPath);
            IsFolderArchive = FolderCodec.PeekContentType(stream) == Zpp2Format.ContentTypeDirectory;
        }
        else
        {
            IsFolderArchive = false;
        }
    }

    private void SuggestOutputPath()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return;

        if (IsDecompressMode && IsFolderArchive)
        {
            var parent = Path.GetDirectoryName(InputPath) ?? string.Empty;
            OutputPath = Path.Combine(parent, Path.GetFileNameWithoutExtension(InputPath) + "_extracted");
        }
        else
        {
            var dir = Path.GetDirectoryName(InputPath) ?? string.Empty;
            OutputPath = Path.Combine(dir, SuggestedOutputName());
        }
    }

    private string SuggestedOutputName()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return string.Empty;

        if (IsCompressMode)
            return Path.GetFileName(InputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".zpp2";

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
