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
using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

/// <summary>Aba "Enviar": escolher arquivo/pasta → gerar código → aguardar → enviar.</summary>
public partial class SendViewModel : OperationViewModel
{
    private int _sizeVersion;
    private int _sendVersion;

    /// <summary>A transferência já começou: o progresso da compressão deixa de aparecer.</summary>
    private bool _sending;

    public SendViewModel()
    {
        RefreshPending();
        LoadDevices();
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            if (!IsBusy)
                RefreshPending();
        };
        DeviceServices.TrustedChanged += () => Dispatcher.UIThread.Post(LoadDevices);
    }

    // ---- Enviar para dispositivo pareado (sem código) ----

    /// <summary>Abre a aba Dispositivos (definido pela Home).</summary>
    public Action? ShowDevices { get; set; }

    public ObservableCollection<TrustedDevice> Devices { get; } = new();

    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToDeviceCommand))]
    public partial TrustedDevice? SelectedDevice { get; set; }

    /// <summary>Dispositivo de destino do envio atual (no lugar do código).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetDevice))]
    public partial string TargetDeviceName { get; set; } = string.Empty;

    public bool HasTargetDevice => !string.IsNullOrEmpty(TargetDeviceName);

    private void LoadDevices()
    {
        var selectedId = SelectedDevice?.DeviceId;
        Devices.Clear();
        foreach (var device in DeviceServices.Trusted.List())
            Devices.Add(device);
        HasDevices = Devices.Count > 0;
        SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == selectedId) ?? Devices.FirstOrDefault();
    }

    private bool CanSendToDevice() => CanSend() && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanSendToDevice))]
    private Task SendToDeviceAsync()
        => StartSendAsync(SelectedPath, TransferServices.GenerateInviteRoom(), SelectedDevice);

    [RelayCommand]
    private void OpenDevices() => ShowDevices?.Invoke();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowPicker))]
    [NotifyPropertyChangedFor(nameof(SelectedName))]
    [NotifyPropertyChangedFor(nameof(SelectedIsFolder))]
    [NotifyPropertyChangedFor(nameof(ShowDestinationChoice))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToDeviceCommand))]
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

    /// <summary>Copia o código para a área de transferência assim que é gerado (envio pelo menu de contexto).</summary>
    public bool CopyCodeAutomatically { get; init; }

    partial void OnRoomCodeChanged(string value)
    {
        if (CopyCodeAutomatically && !string.IsNullOrEmpty(value))
            _ = CopyCodeAsync();
    }

    [ObservableProperty]
    public partial string CopyFeedback { get; set; } = string.Empty;

    public ObservableCollection<PendingTransferItem> Pending { get; } = new();

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    protected override void OnStateUpdated(OperationState value)
    {
        OnPropertyChanged(nameof(ShowPicker));
        OnPropertyChanged(nameof(ShowDestinationChoice));
        SendCommand.NotifyCanExecuteChanged();
        SendToDeviceCommand.NotifyCanExecuteChanged();
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
        TargetDeviceName = string.Empty;
        SelectedPath = Path.GetFullPath(path);
    }

    /// <summary>
    /// Usado pelo menu de contexto do sistema: seleciona e já começa a enviar com código. Com
    /// dispositivos pareados, espera a escolha (dispositivo ou código) na janela compacta.
    /// </summary>
    public async Task SendFromShellAsync(string path)
    {
        SetPath(path);
        if (!HasDevices && SendCommand.CanExecute(null))
            await SendAsync();
    }

    /// <summary>Janela compacta: ainda não começou e há dispositivos pareados para escolher.</summary>
    public bool ShowDestinationChoice => IsIdle && HasSelection && HasDevices;

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(ShowDestinationChoice));

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
        TargetDeviceName = string.Empty;
        State = OperationState.Idle;
    }

    private bool CanSend() => HasSelection && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => StartSendAsync(SelectedPath, TransferServices.GenerateRoomCode());

    /// <summary>
    /// Entra na sala logo e compacta ao mesmo tempo: o envio começa assim que o outro computador
    /// conecta e acompanha a compressão, em vez de esperar ela terminar. Com <paramref name="target"/>,
    /// convida o dispositivo pareado (em vez de mostrar um código) e a sessão é autenticada.
    /// </summary>
    private async Task StartSendAsync(string source, string roomCode, TrustedDevice? target = null)
    {
        // O código só aparece no envio por código; para um dispositivo, aparece o nome dele.
        RoomCode = target is null ? roomCode : string.Empty;
        TargetDeviceName = target?.Name ?? string.Empty;
        var version = ++_sendVersion;

        await RunAsync(async ct =>
        {
            SetStatus("Status.Connecting");
            await TransferServices.CheckServerAsync(ct).ConfigureAwait(true);

            // Convida antes de compactar: se o dispositivo estiver offline, falha na hora.
            if (target is not null)
                await DeviceServices.Connector.InviteAsync(target, roomCode, ct).ConfigureAwait(true);

            Logger.LogInformation("Preparing {Source} for sending ({Target})", source, target?.Name ?? "code " + roomCode);
            SetStatus("Status.Preparing");

            var transfer = new OutgoingTransfer(
                Guid.NewGuid(), source, SelectedName, TransferPackage.GetContentType(source), -1, roomCode, DateTimeOffset.Now,
                target?.DeviceId);
            TransferServices.Store.Save(transfer);
            var package = TransferServices.StartPackage(
                transfer,
                // A compressão pode continuar depois desta tentativa: só este envio atualiza a tela.
                new Progress<CompressionProgress>(p =>
                {
                    if (version == _sendVersion)
                        ReportPreparation(p);
                }));

            await TransferAsync(transfer, package, ct).ConfigureAwait(true);
        });
    }

    /// <summary>Progresso da compressão até o envio começar; depois, a barra mostra o envio.</summary>
    private void ReportPreparation(CompressionProgress p)
    {
        if (_sending || !IsBusy)
            return;

        ReportCompression(p);
        Details = p.TotalBytes > 0 && p.BytesProcessed >= p.TotalBytes
            ? string.Format(CultureInfo.CurrentCulture, L("Send.CompressedDetail"), FormatBytes(p.TotalBytes))
            : string.Format(CultureInfo.CurrentCulture, L("Send.CompressingDetail"), Details);
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
        if (IsBusy || item.Transfer is not OutgoingTransfer pending)
            return;

        SelectedPath = pending.SourcePath;
        ++_sendVersion;

        // Envio para um dispositivo pareado: ele precisa ser convidado de novo para a sala.
        TrustedDevice? target = null;
        if (pending.TargetDeviceId is { } targetId && (target = DeviceServices.Trusted.Find(targetId)) is null)
        {
            SetStatus("Status.Failed");
            ErrorMessage = L("Error.DeviceRemoved");
            State = OperationState.Failed;
            return;
        }

        // Ainda compactando neste processo (só a conexão caiu): retoma acompanhando a compressão.
        if (TransferServices.FindPreparing(pending.Id) is { } preparing)
        {
            ShowTarget(pending.RoomCode, target);
            await RunAsync(ct => InviteAndTransferAsync(pending, preparing, target, ct));
            return;
        }

        var transfer = TransferServices.Store.FindOutgoing(pending.Id) ?? pending;
        if (transfer.PackageSize < 0)
        {
            // O app fechou no meio da compressão: o pacote parcial não serve. Compacta de novo, com o mesmo código.
            Logger.LogInformation("The package of {Id} was not fully prepared; preparing it again", transfer.Id);
            TransferServices.Store.Delete(transfer);
            await StartSendAsync(transfer.SourcePath, transfer.RoomCode, target);
            return;
        }

        ShowTarget(transfer.RoomCode, target);
        var package = PackageSource.FromFile(TransferServices.Store.PackagePath(transfer.Id));
        await RunAsync(ct => InviteAndTransferAsync(transfer, package, target, ct));
    }

    private void ShowTarget(string roomCode, TrustedDevice? target)
    {
        RoomCode = target is null ? roomCode : string.Empty;
        TargetDeviceName = target?.Name ?? string.Empty;
    }

    private async Task InviteAndTransferAsync(OutgoingTransfer transfer, PackageSource package, TrustedDevice? target, CancellationToken ct)
    {
        if (target is not null)
        {
            SetStatus("Status.Connecting");
            await DeviceServices.Connector.InviteAsync(target, transfer.RoomCode, ct).ConfigureAwait(true);
        }

        await TransferAsync(transfer, package, ct).ConfigureAwait(true);
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

    private async Task TransferAsync(OutgoingTransfer transfer, PackageSource package, CancellationToken ct)
    {
        _sending = false;

        // Cancelar o envio cancela também a compressão; se só a conexão cair, ela continua e o envio fica retomável.
        await using var cancelCompression = ct.Register(package.Cancel);

        // Para um dispositivo pareado, a sessão só aceita aquele dispositivo (e dispensa a conferência do SAS).
        var target = transfer.TargetDeviceId is { } targetId ? DeviceServices.Trusted.Find(targetId) : null;
        using var identity = target is null ? null : DeviceServices.Profile.LoadIdentity();
        var runner = TransferServices.CreateRunner(target is null ? null : DeviceTrust.OnlyDevice(identity!, target));
        if (target is null)
            runner.SasReady += sas => Dispatcher.UIThread.Post(() => SasCode = sas);
        runner.StatusChanged += status => Dispatcher.UIThread.Post(() => ApplyTransferStatus(status, "Status.Sending"));
        var progress = new Progress<TransferProgress>(p =>
        {
            _sending = true;
            ReportTransfer(p);
        });
        await runner.SendAsync(transfer, package, progress, ct).ConfigureAwait(true);
    }

    private void RefreshPending()
    {
        Pending.Clear();
        foreach (var t in TransferServices.Store.ListOutgoing().OrderByDescending(t => t.CreatedAt))
        {
            // Envio para dispositivo: mostra o nome dele no lugar do código.
            var target = t.TargetDeviceId is { } id ? DeviceServices.Trusted.Find(id)?.Name ?? "?" : t.RoomCode;
            var detail = string.Format(
                CultureInfo.CurrentCulture,
                L("Pending.SendDetail"),
                FormatBytes(t.PackageSize),
                target,
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
