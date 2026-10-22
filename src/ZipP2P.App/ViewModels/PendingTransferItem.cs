using CommunityToolkit.Mvvm.Input;

namespace ZipP2P.App.ViewModels;

/// <summary>Transferência interrompida exibida na lista "Retomar".</summary>
public sealed partial class PendingTransferItem
{
    private readonly Func<PendingTransferItem, Task> _resume;
    private readonly Action<PendingTransferItem> _discard;

    public PendingTransferItem(
        object transfer,
        string name,
        string detail,
        bool isFolder,
        Func<PendingTransferItem, Task> resume,
        Action<PendingTransferItem> discard)
    {
        Transfer = transfer;
        Name = name;
        Detail = detail;
        IsFolder = isFolder;
        _resume = resume;
        _discard = discard;
    }

    /// <summary><see cref="ZipP2P.P2P.Transfer.OutgoingTransfer"/> ou <see cref="ZipP2P.P2P.Transfer.IncomingTransfer"/>.</summary>
    public object Transfer { get; }

    public string Name { get; }

    public string Detail { get; }

    public bool IsFolder { get; }

    public bool IsFile => !IsFolder;

    [RelayCommand]
    private Task ResumeAsync() => _resume(this);

    [RelayCommand]
    private void Discard() => _discard(this);
}
