using System.Collections.Concurrent;
using ZipP2P.App.Logging;
using ZipP2P.Core.Compression;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App;

/// <summary>Monta as peças de transferência com as configurações atuais.</summary>
public static class TransferServices
{
    /// <summary>Pacotes ainda sendo compactados: um envio interrompido pode ser retomado enquanto a compressão continua.</summary>
    private static readonly ConcurrentDictionary<Guid, PackageSource> Preparing = new();

    public static TransferStore Store { get; } = new(Path.Combine(AppPaths.DataDirectory, "transfers"));

    /// <summary>
    /// Começa a compactar o envio em segundo plano. Ao terminar, grava o tamanho no <see cref="Store"/>
    /// (o envio fica retomável); se falhar ou for cancelada, descarta o envio, pois um pacote
    /// incompleto não serve para retomar. Não depende do envio: se só a conexão cair, a compressão
    /// continua (interrompa com <see cref="PackageSource.Cancel"/>).
    /// </summary>
    public static PackageSource StartPackage(OutgoingTransfer transfer, IProgress<CompressionProgress>? progress)
    {
        var package = PackageSource.Create(
            transfer.SourcePath,
            Store.PackagePath(transfer.Id),
            new CompressionOptions { Level = 3 },
            progress,
            length => Store.Save(transfer with { PackageSize = length }));

        Preparing[transfer.Id] = package;
        _ = package.Completion.ContinueWith(
            t =>
            {
                Preparing.TryRemove(transfer.Id, out _);
                if (!t.IsCompletedSuccessfully)
                    Store.Delete(transfer);
            },
            TaskScheduler.Default);

        return package;
    }

    /// <summary>Pacote do envio <paramref name="id"/> ainda em compressão neste processo, se houver.</summary>
    public static PackageSource? FindPreparing(Guid id) => Preparing.TryGetValue(id, out var package) ? package : null;

    /// <param name="authentication">Exige um dispositivo pareado do outro lado (envio/recebimento sem código).</param>
    public static TransferRunner CreateRunner(SessionAuthentication? authentication = null)
        => new(CreateFactory(), Store, AppLog.Instance.CreateLogger("Transfer"), authentication: authentication);

    /// <summary>Falha cedo (antes de comprimir) se o servidor de sinalização não responder.</summary>
    public static Task CheckServerAsync(CancellationToken cancellationToken) => CreateFactory().CheckServerAsync(cancellationToken);

    public static SignalingPeerLinkFactory CreateFactory()
    {
        var settings = new AppSettings(new SettingsStore());
        return new SignalingPeerLinkFactory(settings.SignalingUrl, settings.IceServers, AppLog.Instance.Factory);
    }

    /// <summary>Conexão de presença com o servidor configurado (convites do receive mode).</summary>
    public static PresenceClient CreatePresence()
        => new(new AppSettings(new SettingsStore()).SignalingUrl, AppLog.Instance.CreateLogger("Presence"));

    /// <summary>
    /// Sala de um convite: só os dois dispositivos a conhecem (o servidor a entrega apenas ao convidado),
    /// então pode ser longa e aleatória; não precisa ser digitada.
    /// </summary>
    public static string GenerateInviteRoom() => "D" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    /// <summary>Código de 6 caracteres sem ambiguidades (sem 0/O, 1/I).</summary>
    public static string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    /// <summary>Normaliza o código digitado (maiúsculas, sem espaços/hífens).</summary>
    public static string NormalizeRoomCode(string code)
        => new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
