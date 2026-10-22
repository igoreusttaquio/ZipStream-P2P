using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Localization;
using ZipP2P.App.Logging;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.ViewModels;

public enum OperationState
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Base das abas (Enviar/Receber/Ferramentas): execução cancelável, status localizado,
/// barra de progresso e tratamento de erros (registrados no log técnico).
/// </summary>
public abstract partial class OperationViewModel : ViewModelBase
{
    protected static readonly ILogger Logger = AppLog.Instance.CreateLogger("Transfer");

    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private static readonly SolidColorBrush AccentStatusBrush = new(Color.Parse("#3B82F6"));
    private static readonly SolidColorBrush SuccessBrush = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush WarningBrush = new(Color.Parse("#F59E0B"));
    private static readonly SolidColorBrush ErrorBrush = new(Color.Parse("#EF4444"));

    private CancellationTokenSource? _cts;
    private string _statusKey = "Status.Ready";
    private object[] _statusArgs = Array.Empty<object>();

    protected OperationViewModel()
    {
        Localizer.Instance.PropertyChanged += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    /// <summary>Abre a tela de log técnico (definido pela janela principal).</summary>
    public static Action? ShowLogs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsActiveOrFinished))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial OperationState State { get; set; }

    public bool IsBusy => State == OperationState.Running;

    partial void OnStateChanged(OperationState value) => OnStateUpdated(value);

    protected virtual void OnStateUpdated(OperationState value)
    {
    }

    public bool IsIdle => State == OperationState.Idle;

    public bool IsCompleted => State == OperationState.Completed;

    public bool IsFailed => State == OperationState.Failed;

    /// <summary>Há algo a mostrar no painel de status (rodando ou com resultado).</summary>
    public bool IsActiveOrFinished => State != OperationState.Idle;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MaterialIconKind StatusIcon { get; set; } = MaterialIconKind.CheckCircleOutline;

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSasVisible))]
    public partial string SasCode { get; set; } = string.Empty;

    public bool IsSasVisible => !string.IsNullOrWhiteSpace(SasCode);

    protected static string L(string key) => Localizer.Instance[key];

    protected void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        RefreshStatus();
    }

    /// <summary>Executa a operação tratando cancelamento e erros. Retorna true se concluiu.</summary>
    protected async Task<bool> RunAsync(Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        State = OperationState.Running;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        Details = string.Empty;
        ErrorMessage = string.Empty;
        SasCode = string.Empty;

        try
        {
            await work(_cts.Token).ConfigureAwait(true);
            IsProgressIndeterminate = false;
            ProgressPercent = 100;
            SetStatus("Status.Completed");
            State = OperationState.Completed;
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            SetStatus("Status.Cancelled");
            State = OperationState.Cancelled;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Operation failed");
            SetStatus("Status.Failed");
            ErrorMessage = DescribeError(ex);
            State = OperationState.Failed;
        }
        finally
        {
            IsProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }

        return false;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private static void ViewLogs() => ShowLogs?.Invoke();

    /// <summary>Atualiza o status conforme a etapa da conexão (aguardando, reconectando, transferindo).</summary>
    protected void ApplyTransferStatus(TransferStatus status, string transferringKey)
    {
        switch (status.Stage)
        {
            case TransferStage.WaitingForPeer:
                IsProgressIndeterminate = true;
                SetStatus("Status.WaitingForPeer");
                break;
            case TransferStage.Reconnecting:
                IsProgressIndeterminate = true;
                SetStatus("Status.Reconnecting", status.Attempt);
                break;
            default:
                IsProgressIndeterminate = false;
                SetStatus(transferringKey);
                break;
        }
    }

    protected void ReportTransfer(TransferProgress p)
    {
        IsProgressIndeterminate = false;
        ProgressPercent = p.TotalBytes > 0 ? (double)p.BytesTransferred / p.TotalBytes * 100.0 : 0;

        var text = $"{FormatBytes(p.BytesTransferred)} / {FormatBytes(p.TotalBytes)}";
        if (p.BytesPerSecond > 0)
        {
            text += $" · {FormatBytes((long)p.BytesPerSecond)}/s";
            var remaining = (p.TotalBytes - p.BytesTransferred) / p.BytesPerSecond;
            if (remaining >= 1)
                text += " · " + string.Format(CultureInfo.CurrentCulture, L("Progress.Remaining"), FormatDuration(TimeSpan.FromSeconds(remaining)));
        }

        Details = text;
    }

    protected void ReportCompression(ZipP2P.Core.Compression.CompressionProgress p)
    {
        IsProgressIndeterminate = p.TotalBytes <= 0;
        ProgressPercent = p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes * 100.0 : 0;
        Details = p.TotalBytes > 0
            ? $"{FormatBytes(p.BytesProcessed)} / {FormatBytes(p.TotalBytes)}"
            : FormatBytes(p.BytesProcessed);
    }

    public static string FormatBytes(long bytes)
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

    private static string FormatDuration(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}h {time.Minutes:00}min"
        : time.TotalMinutes >= 1
            ? $"{(int)time.TotalMinutes}min {time.Seconds:00}s"
            : $"{time.Seconds}s";

    /// <summary>Mensagem amigável + detalhe técnico.</summary>
    protected static string DescribeError(Exception ex)
    {
        var hint = ex switch
        {
            SignalingException => L("Error.Signaling"),
            ConnectionLostException => L("Error.ConnectionLost"),
            TransferAbortedException => L("Error.Aborted"),
            InvalidDataException => L("Error.Corrupted"),
            UnauthorizedAccessException => L("Error.Access"),
            IOException => L("Error.Disk"),
            _ => L("Error.Unexpected"),
        };

        return $"{hint}\n{ex.Message}";
    }

    private void RefreshStatus()
    {
        var text = L(_statusKey);
        StatusText = _statusArgs.Length > 0 ? string.Format(CultureInfo.CurrentCulture, text, _statusArgs) : text;

        (StatusIcon, StatusBrush) = _statusKey switch
        {
            "Status.Preparing" or "Status.Compressing" => (MaterialIconKind.Archive, AccentStatusBrush),
            "Status.Decompressing" or "Status.Extracting" => (MaterialIconKind.Unarchive, AccentStatusBrush),
            "Status.WaitingForPeer" or "Status.Connecting" => (MaterialIconKind.HourglassEmpty, AccentStatusBrush),
            "Status.Reconnecting" => (MaterialIconKind.Refresh, WarningBrush),
            "Status.Sending" => (MaterialIconKind.Upload, AccentStatusBrush),
            "Status.Receiving" => (MaterialIconKind.Download, AccentStatusBrush),
            "Status.Completed" => (MaterialIconKind.CheckCircle, SuccessBrush),
            "Status.Cancelled" => (MaterialIconKind.Cancel, WarningBrush),
            "Status.Failed" => (MaterialIconKind.AlertCircleOutline, ErrorBrush),
            _ => (MaterialIconKind.CheckCircleOutline, (IBrush)Brushes.Gray),
        };
    }
}
