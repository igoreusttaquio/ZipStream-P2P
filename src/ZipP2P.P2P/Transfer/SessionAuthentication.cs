using System.Buffers.Binary;
using System.Text;
using ZipP2P.Core.Security;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Autenticação entre dispositivos pareados (receive mode): cada lado assina as chaves efêmeras da
/// sessão com a identidade permanente e só aceita o par se a identidade dele for confiável. Substitui
/// a conferência manual do código SAS, que ninguém faz quando o recebimento é automático.
/// </summary>
public sealed class SessionAuthentication
{
    private static readonly byte[] Label = Encoding.UTF8.GetBytes("ZipStream-Identity-v1");

    private readonly Func<byte[], bool> _isTrusted;

    /// <param name="identity">Identidade deste dispositivo (o chamador continua dono do objeto).</param>
    /// <param name="isTrusted">Decide se a chave pública da identidade do par é confiável.</param>
    public SessionAuthentication(DeviceIdentity identity, Func<byte[], bool> isTrusted)
    {
        Identity = identity;
        _isTrusted = isTrusted;
    }

    public DeviceIdentity Identity { get; }

    public bool IsTrusted(byte[] peerPublicKey) => _isTrusted(peerPublicKey);

    /// <summary>
    /// O que cada lado assina: a própria chave efêmera e a do par, nessa ordem. Quem se colocar no meio
    /// troca as chaves efêmeras, e a assinatura deixa de conferir do outro lado.
    /// </summary>
    public static byte[] Transcript(ReadOnlySpan<byte> signerEphemeralKey, ReadOnlySpan<byte> verifierEphemeralKey)
    {
        var buffer = new byte[Label.Length + 4 + signerEphemeralKey.Length + verifierEphemeralKey.Length];
        var span = buffer.AsSpan();
        Label.CopyTo(span);
        span = span[Label.Length..];
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)signerEphemeralKey.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)verifierEphemeralKey.Length);
        signerEphemeralKey.CopyTo(span[4..]);
        verifierEphemeralKey.CopyTo(span[(4 + signerEphemeralKey.Length)..]);
        return buffer;
    }
}
