using System.Text.Json;

namespace ZipP2P.P2P.Transfer;

/// <summary>Envio pendente: o pacote já comprimido fica em cache até o destinatário confirmar o recebimento.</summary>
public sealed record OutgoingTransfer(
    Guid Id,
    string SourcePath,
    string FileName,
    byte ContentType,
    long PackageSize,
    string RoomCode,
    DateTimeOffset CreatedAt);

/// <summary>Recebimento pendente: o pacote parcial fica em disco para ser retomado.</summary>
public sealed record IncomingTransfer(
    Guid Id,
    string FileName,
    byte ContentType,
    long Size,
    string DestinationDir,
    string RoomCode,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persiste o estado das transferências em andamento (JSON + dados) para que possam ser
/// retomadas depois de uma queda de conexão ou de o app ser fechado e reaberto.
/// </summary>
public sealed class TransferStore
{
    private readonly string _outgoingDir;
    private readonly string _incomingDir;

    public TransferStore(string rootDirectory)
    {
        _outgoingDir = Path.Combine(rootDirectory, "outgoing");
        _incomingDir = Path.Combine(rootDirectory, "incoming");
        Directory.CreateDirectory(_outgoingDir);
        Directory.CreateDirectory(_incomingDir);
    }

    /// <summary>Pasta padrão: <c>%LOCALAPPDATA%/ZipStream/transfers</c> (ou equivalente no Linux/macOS).</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipStream", "transfers");

    public string PackagePath(Guid id) => Path.Combine(_outgoingDir, $"{id:N}.zpp2");

    public string PartialPath(Guid id) => Path.Combine(_incomingDir, $"{id:N}.part");

    public void Save(OutgoingTransfer transfer) => Write(Path.Combine(_outgoingDir, $"{transfer.Id:N}.json"), transfer);

    public void Save(IncomingTransfer transfer) => Write(Path.Combine(_incomingDir, $"{transfer.Id:N}.json"), transfer);

    public IReadOnlyList<OutgoingTransfer> ListOutgoing()
        => ReadAll<OutgoingTransfer>(_outgoingDir).Where(t => File.Exists(PackagePath(t.Id))).ToList();

    public IReadOnlyList<IncomingTransfer> ListIncoming() => ReadAll<IncomingTransfer>(_incomingDir).ToList();

    public IncomingTransfer? FindIncoming(Guid id)
        => Read<IncomingTransfer>(Path.Combine(_incomingDir, $"{id:N}.json"));

    /// <summary>Bytes do pacote já recebidos (tamanho do arquivo parcial).</summary>
    public long BytesReceived(IncomingTransfer transfer)
    {
        var info = new FileInfo(PartialPath(transfer.Id));
        return info.Exists ? info.Length : 0;
    }

    public void Delete(OutgoingTransfer transfer)
    {
        TryDelete(Path.Combine(_outgoingDir, $"{transfer.Id:N}.json"));
        TryDelete(PackagePath(transfer.Id));
    }

    public void Delete(IncomingTransfer transfer)
    {
        TryDelete(Path.Combine(_incomingDir, $"{transfer.Id:N}.json"));
        TryDelete(PartialPath(transfer.Id));
    }

    private static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value));
        File.Move(temp, path, overwrite: true);
    }

    private static T? Read<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<T> ReadAll<T>(string directory)
        where T : class
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (Read<T>(file) is { } value)
                yield return value;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignora falha de limpeza
        }
    }
}
