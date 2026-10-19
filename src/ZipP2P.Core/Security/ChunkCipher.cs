using System.Security.Cryptography;

namespace ZipP2P.Core.Security;

/// <summary>
/// Cifra/decifra pedaços de dados com AES-256-GCM. Cada pedaço usa um nonce
/// aleatório de 96 bits (único por pedaço), anexado ao texto cifrado.
/// Formato no fio: [nonce (12) | ciphertext | tag (16)].
/// </summary>
public sealed class ChunkCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public ChunkCipher(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("A chave deve ter 32 bytes (AES-256).", nameof(key));

        _key = key;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length < NonceSize + TagSize)
            throw new InvalidDataException("Dados cifrados inválidos (muito curtos).");

        var nonce = encrypted[..NonceSize];
        var ciphertext = encrypted[NonceSize..^TagSize];
        var tag = encrypted[^TagSize..];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
