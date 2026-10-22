using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Localization;
using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

/// <summary>Aba "Enviar": escolher arquivo/pasta → gerar código → aguardar → enviar.</summary>
public partial class SendViewModel : OperationViewModel
{
    private int _sizeVersion;

    public SendViewModel()
    {
        RefreshPending();
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            if (!IsBusy)
                RefreshPending();
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowPicker))]
    [NotifyPropertyChangedFor(nameof(SelectedName))]
    [NotifyPropertyChangedFor(nameof(SelectedIsFolder))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string SelectedPath { get; set; } = string.Empty;

    public bool HasSelection => !string.IsNullOrEmpty(SelectedPath);

    public bool ShowPicker => !HasSelection && !IsActiveOrFinished;

    public string SelectedName => Path.GetFileName(SelectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool SelectedIsFolder => Directory.Exists(SelectedPath);

    [ObservableProperty]
    public partial string SelectedSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomCode))]
    public partial string RoomCode { get; set; } = string.Empty;

    public bool HasRoomCode => !string.IsNullOrEmpty(RoomCode);

    [ObservableProperty]
    public partial string CopyFeedback { get; set; } = string.Empty;

    public ObservableCollection<PendingTransferItem> Pending { get; } = new();

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    protected override void OnStateUpdated(OperationState value)
    {
        OnPropertyChanged(nameof(ShowPicker));
        SendCommand.NotifyCanExecuteChanged();
        if (value != OperationState.Running)
            RefreshPending();
    }

    partial void OnSelectedPathChanged(string value) => _ = ComputeSizeAsync(value);

    /// <summary>Seleciona o item (arrastar e soltar, linha de comando, menu de contexto).</summary>
    public void SetPath(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
            return;

        State = OperationState.Idle;
        RoomCode = string.Empty;
        SelectedPath = Path.GetFullPath(path);
    }

    /// <summary>Usado pelo menu de contexto do sistema: seleciona e já começa a enviar.</summary>
    public async Task SendFromShellAsync(string path)
    {
        SetPath(path);
        if (SendCommand.CanExecute(null))
            await SendAsync();
    }

    [RelayCommand]
    private async Task PickFileAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = L("Send.PickFile"),
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetPath(path);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = L("Send.PickFolder"),
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetPath(path);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (IsBusy)
            return;

        SelectedPath = string.Empty;
        RoomCode = string.Empty;
        State = OperationState.Idle;
    }

    private bool CanSend() => HasSelection && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var source = SelectedPath;
        RoomCode = TransferServices.GenerateRoomCode();

        await RunAsync(async ct =>
        {
            SetStatus("Status.Connecting");
            await TransferServices.CheckServerAsync(ct).ConfigureAwait(true);

            Logger.LogInformation("Preparing {Source} for sending (code {Room})", source, RoomCode);
            SetStatus("Status.Preparing");

            var store = TransferServices.Store;
            var id = Guid.NewGuid();
            var contentType = await TransferPackage.CreateAsync(
                source,
                store.PackagePath(id),
                new CompressionOptions { Level = 3 },
                new Progress<CompressionProgress>(ReportCompression),
                ct).ConfigureAwait(true);

            var transfer = new OutgoingTransfer(
                id, source, SelectedName, contentType, new FileInfo(store.PackagePath(id)).Length, RoomCode, DateTimeOffset.Now);
            store.Save(transfer);
            Details = string.Empty;

            await TransferAsync(transfer, ct).ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (AppServices.Clipboard is null || !HasRoomCode)
            return;

        await AppServices.Clipboard.SetTextAsync(RoomCode);
        CopyFeedback = L("Send.CodeCopied");
        await Task.Delay(2000);
        CopyFeedback = string.Empty;
    }

    [RelayCommand]
    private void SendAnother() => ClearSelection();

    private async Task ResumeAsync(PendingTransferItem item)
    {
        if (IsBusy || item.Transfer is not OutgoingTransfer transfer)
            return;

        SelectedPath = transfer.SourcePath;
        RoomCode = transfer.RoomCode;
        await RunAsync(ct => TransferAsync(transfer, ct));
    }

    private void Discard(PendingTransferItem item)
    {
        if (item.Transfer is OutgoingTransfer transfer)
        {
            Logger.LogInformation("Discarding pending send {Id} ({Name})", transfer.Id, transfer.FileName);
            TransferServices.Store.Delete(transfer);
        }

        RefreshPending();
    }

    private async Task TransferAsync(OutgoingTransfer transfer, CancellationToken ct)
    {
        var runner = TransferServices.CreateRunner();
        runner.SasReady += sas => Dispatcher.UIThread.Post(() => SasCode = sas);
        runner.StatusChanged += status => Dispatcher.UIThread.Post(() => ApplyTransferStatus(status, "Status.Sending"));
        await runner.SendAsync(transfer, new Progress<TransferProgress>(ReportTransfer), ct).ConfigureAwait(true);
    }

    private void RefreshPending()
    {
        Pending.Clear();
        foreach (var t in TransferServices.Store.ListOutgoing().OrderByDescending(t => t.CreatedAt))
        {
            var detail = string.Format(
                CultureInfo.CurrentCulture,
                L("Pending.SendDetail"),
                FormatBytes(t.PackageSize),
                t.RoomCode,
                t.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
            Pending.Add(new PendingTransferItem(
                t, t.FileName, detail, t.ContentType == Zpp2Format.ContentTypeDirectory, ResumeAsync, Discard));
        }

        HasPending = Pending.Count > 0;
    }

    private async Task ComputeSizeAsync(string path)
    {
        var version = ++_sizeVersion;
        SelectedSizeText = string.Empty;
        if (string.IsNullOrEmpty(path))
            return;

        var size = await Task.Run(() =>
        {
            try
            {
                if (File.Exists(path))
                    return new FileInfo(path).Length;

                return new DirectoryInfo(path)
                    .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Sum(f => f.Length);
            }
            catch
            {
                return -1L;
            }
        }).ConfigureAwait(true);

        if (version == _sizeVersion)
            SelectedSizeText = FormatBytes(size);
    }
}
