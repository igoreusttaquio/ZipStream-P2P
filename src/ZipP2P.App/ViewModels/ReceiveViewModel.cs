using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Localization;
using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

/// <summary>Aba "Receber": digitar o código → escolher onde salvar → receber.</summary>
public partial class ReceiveViewModel : OperationViewModel
{
    private readonly AppSettings _settings = new(new SettingsStore());

    public ReceiveViewModel()
    {
        DestinationDir = _settings.DownloadDirectory;
        RefreshPending();
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            if (!IsBusy)
                RefreshPending();
        };
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReceiveCommand))]
    public partial string RoomCode { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReceiveCommand))]
    public partial string DestinationDir { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultPath { get; set; } = string.Empty;

    public bool HasResult => !string.IsNullOrEmpty(ResultPath);

    /// <summary>Formulário visível enquanto não há transferência em andamento nem concluída.</summary>
    public bool ShowForm => !IsBusy && !IsCompleted;

    public ObservableCollection<PendingTransferItem> Pending { get; } = new();

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    partial void OnRoomCodeChanged(string value)
    {
        var normalized = TransferServices.NormalizeRoomCode(value);
        if (normalized != value)
            RoomCode = normalized;
    }

    protected override void OnStateUpdated(OperationState value)
    {
        OnPropertyChanged(nameof(ShowForm));
        ReceiveCommand.NotifyCanExecuteChanged();
        if (value != OperationState.Running)
            RefreshPending();
    }

    [RelayCommand]
    private async Task ChangeDestinationAsync()
    {
        if (AppServices.Storage is not { } storage)
            return;

        var start = Directory.Exists(DestinationDir) ? await storage.TryGetFolderFromPathAsync(DestinationDir) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = L("Receive.SaveTo"),
            SuggestedStartLocation = start,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            DestinationDir = path;
            _settings.DownloadDirectory = path;
        }
    }

    private bool CanReceive() => !IsBusy && RoomCode.Length >= 4 && !string.IsNullOrWhiteSpace(DestinationDir);

    [RelayCommand(CanExecute = nameof(CanReceive))]
    private async Task ReceiveAsync()
    {
        var code = RoomCode;
        var destination = DestinationDir;
        ResultPath = string.Empty;

        await RunAsync(async ct =>
        {
            Logger.LogInformation("Receiving with code {Room} into {Destination}", code, destination);
            var incoming = await TransferAsync(code, destination, ct).ConfigureAwait(true);
            await ExtractAsync(incoming, ct).ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private async Task OpenResultFolderAsync()
    {
        if (AppServices.Launcher is not { } launcher || !HasResult)
            return;

        var folder = Directory.Exists(ResultPath) ? ResultPath : Path.GetDirectoryName(ResultPath);
        if (folder is not null)
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    [RelayCommand]
    private void ReceiveAnother()
    {
        RoomCode = string.Empty;
        ResultPath = string.Empty;
        State = OperationState.Idle;
    }

    private async Task ResumeAsync(PendingTransferItem item)
    {
        if (IsBusy || item.Transfer is not IncomingTransfer transfer)
            return;

        RoomCode = transfer.RoomCode;
        ResultPath = string.Empty;
        await RunAsync(async ct =>
        {
            // Já chegou tudo (ex.: falhou só a extração): conclui sem precisar do remetente.
            var incoming = TransferServices.Store.BytesReceived(transfer) >= transfer.Size
                ? transfer
                : await TransferAsync(transfer.RoomCode, transfer.DestinationDir, ct).ConfigureAwait(true);
            await ExtractAsync(incoming, ct).ConfigureAwait(true);
        });
    }

    private void Discard(PendingTransferItem item)
    {
        if (item.Transfer is IncomingTransfer transfer)
        {
            Logger.LogInformation("Discarding pending download {Id} ({Name})", transfer.Id, transfer.FileName);
            TransferServices.Store.Delete(transfer);
        }

        RefreshPending();
    }

    private async Task<IncomingTransfer> TransferAsync(string code, string destination, CancellationToken ct)
    {
        var runner = TransferServices.CreateRunner();
        runner.SasReady += sas => Dispatcher.UIThread.Post(() => SasCode = sas);
        runner.StatusChanged += status => Dispatcher.UIThread.Post(() => ApplyTransferStatus(status, "Status.Receiving"));
        return await runner.ReceiveAsync(code, destination, new Progress<TransferProgress>(ReportTransfer), ct).ConfigureAwait(true);
    }

    private async Task ExtractAsync(IncomingTransfer incoming, CancellationToken ct)
    {
        SetStatus("Status.Extracting");
        Details = string.Empty;
        var store = TransferServices.Store;

        try
        {
            ResultPath = await TransferPackage.ExtractAsync(
                store.PartialPath(incoming.Id),
                incoming.FileName,
                incoming.ContentType,
                incoming.DestinationDir,
                new Progress<CompressionProgress>(ReportCompression),
                ct).ConfigureAwait(true);
        }
        catch (InvalidDataException)
        {
            // Pacote corrompido: não adianta retomar, descarta para não ficar preso na lista.
            store.Delete(incoming);
            throw;
        }

        store.Delete(incoming);
        Logger.LogInformation("Saved to {Path}", ResultPath);
    }

    private void RefreshPending()
    {
        Pending.Clear();
        var store = TransferServices.Store;
        foreach (var t in store.ListIncoming().OrderByDescending(t => t.CreatedAt))
        {
            var percent = t.Size > 0 ? store.BytesReceived(t) * 100 / t.Size : 0;
            var detail = string.Format(
                CultureInfo.CurrentCulture,
                L("Pending.ReceiveDetail"),
                percent,
                FormatBytes(t.Size),
                t.RoomCode,
                t.DestinationDir);
            Pending.Add(new PendingTransferItem(
                t, t.FileName, detail, t.ContentType == Zpp2Format.ContentTypeDirectory, ResumeAsync, Discard));
        }

        HasPending = Pending.Count > 0;
    }
}
