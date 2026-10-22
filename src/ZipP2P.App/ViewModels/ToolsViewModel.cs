using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

/// <summary>Aba "Ferramentas": compactar/descompactar ".zpp2" localmente, sem rede.</summary>
public partial class ToolsViewModel : OperationViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompressMode))]
    [NotifyPropertyChangedFor(nameof(IsDecompressMode))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial int ModeIndex { get; set; }

    public bool IsCompressMode => ModeIndex == 0;

    public bool IsDecompressMode => ModeIndex == 1;

    [ObservableProperty]
    public partial int CompressionPresetIndex { get; set; } = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string InputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string OutputDir { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultPath { get; set; } = string.Empty;

    private int SelectedLevel => CompressionPresetIndex switch
    {
        0 => 1,
        1 => 3,
        _ => 19,
    };

    partial void OnModeIndexChanged(int value)
    {
        InputPath = string.Empty;
        OutputDir = string.Empty;
        State = OperationState.Idle;
    }

    public void SetInputPath(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
            return;

        InputPath = path;
        OutputDir = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        State = OperationState.Idle;
    }

    protected override void OnStateUpdated(OperationState value) => StartCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task PickFileAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var options = new FilePickerOpenOptions { AllowMultiple = false, Title = L("Send.PickFile") };
        if (IsDecompressMode)
            options.FileTypeFilter = new[] { new FilePickerFileType("ZipStream (.zpp2)") { Patterns = new[] { "*.zpp2" } } };

        var files = await storage.OpenFilePickerAsync(options);
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetInputPath(path);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = L("Send.PickFolder") });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetInputPath(path);
    }

    [RelayCommand]
    private async Task PickOutputAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = L("Receive.SaveTo") });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            OutputDir = path;
    }

    private bool CanStart() => !IsBusy && !string.IsNullOrWhiteSpace(InputPath) && !string.IsNullOrWhiteSpace(OutputDir);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ResultPath = string.Empty;
        await RunAsync(async ct =>
        {
            var progress = new Progress<CompressionProgress>(ReportCompression);
            if (IsCompressMode)
            {
                SetStatus("Status.Compressing");
                var name = Path.GetFileName(InputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".zpp2";
                var output = TransferPackage.GetUniquePath(OutputDir, name, isDirectory: false);
                await TransferPackage.CreateAsync(InputPath, output, new CompressionOptions { Level = SelectedLevel }, progress, ct).ConfigureAwait(true);
                ResultPath = output;
            }
            else
            {
                SetStatus("Status.Decompressing");
                byte contentType;
                await using (var input = File.OpenRead(InputPath))
                    contentType = FolderCodec.PeekContentType(input);

                var name = Path.GetFileNameWithoutExtension(InputPath);
                ResultPath = await TransferPackage.ExtractAsync(InputPath, name, contentType, OutputDir, progress, ct).ConfigureAwait(true);
            }

            Details = ResultPath;
        });
    }
}
