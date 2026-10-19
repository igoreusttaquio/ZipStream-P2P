using System.Security.Cryptography;
using ZipP2P.Core.Security;

namespace ZipP2P.Core.Tests;

public class SecurityTests
{
    [Fact]
    public void KeyAgreement_DerivesSameSharedKey()
    {
        using var alice = new KeyAgreement();
        using var bob = new KeyAgreement();

        var keyAlice = alice.DeriveKey(bob.PublicKey);
        var keyBob = bob.DeriveKey(alice.PublicKey);

        Assert.Equal(32, keyAlice.Length);
        Assert.Equal(keyAlice, keyBob);
    }

    [Fact]
    public void ChunkCipher_RoundTrip()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var cipher = new ChunkCipher(key);

        var plaintext = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void ChunkCipher_TamperDetected()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var cipher = new ChunkCipher(key);

        var encrypted = cipher.Encrypt(new byte[] { 1, 2, 3, 4, 5 });
        encrypted[^1] ^= 0xFF; // corrompe o tag de autenticação

        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(encrypted));
    }

    [Fact]
    public void ChunkCipher_EmptyPlaintext()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var cipher = new ChunkCipher(key);

        var encrypted = cipher.Encrypt(ReadOnlySpan<byte>.Empty);
        Assert.Empty(cipher.Decrypt(encrypted));
    }
}
