using ZipP2P.App.Logging;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App;

/// <summary>Monta as peças de transferência com as configurações atuais.</summary>
public static class TransferServices
{
    public static TransferStore Store { get; } = new(TransferStore.DefaultRoot);

    public static TransferRunner CreateRunner() => new(CreateFactory(), Store, AppLog.Instance.CreateLogger("Transfer"));

    /// <summary>Falha cedo (antes de comprimir) se o servidor de sinalização não responder.</summary>
    public static Task CheckServerAsync(CancellationToken cancellationToken) => CreateFactory().CheckServerAsync(cancellationToken);

    private static SignalingPeerLinkFactory CreateFactory()
    {
        var settings = new AppSettings(new SettingsStore());
        return new SignalingPeerLinkFactory(settings.SignalingUrl, settings.IceServers, AppLog.Instance.Factory);
    }

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
