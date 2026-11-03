using System.Security.Cryptography;
using ZipP2P.Core.Security;

namespace ZipP2P.Core.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void SignAndVerify_RoundTrip()
    {
        using var identity = DeviceIdentity.Create();
        var data = RandomNumberGenerator.GetBytes(91);

        var signature = identity.Sign(data);

        Assert.True(DeviceIdentity.Verify(identity.PublicKey, data, signature));
    }

    [Fact]
    public void Verify_RejectsTamperedDataOrOtherIdentity()
    {
        using var identity = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        var data = RandomNumberGenerator.GetBytes(64);
        var signature = identity.Sign(data);

        var tampered = (byte[])data.Clone();
        tampered[0] ^= 0xFF;

        Assert.False(DeviceIdentity.Verify(identity.PublicKey, tampered, signature));
        Assert.False(DeviceIdentity.Verify(other.PublicKey, data, signature));
        Assert.False(DeviceIdentity.Verify([1, 2, 3], data, signature));
    }

    [Fact]
    public void ExportImport_KeepsTheSameIdentity()
    {
        using var identity = DeviceIdentity.Create();

        using var restored = DeviceIdentity.Import(identity.ExportPrivateKey());

        Assert.Equal(identity.PublicKey, restored.PublicKey);
        Assert.Equal(identity.DeviceId, restored.DeviceId);
        var data = RandomNumberGenerator.GetBytes(32);
        Assert.True(DeviceIdentity.Verify(identity.PublicKey, data, restored.Sign(data)));
    }

    [Fact]
    public void DeviceId_IsDerivedFromThePublicKey()
    {
        using var a = DeviceIdentity.Create();
        using var b = DeviceIdentity.Create();

        Assert.Equal(64, a.DeviceId.Length);
        Assert.Equal(DeviceIdentity.ComputeDeviceId(a.PublicKey), a.DeviceId);
        Assert.NotEqual(a.DeviceId, b.DeviceId);
    }

    [Fact]
    public void ReceiverCode_GeneratesFormattedCodes()
    {
        var code = ReceiverCode.Generate();

        Assert.Matches("^[A-Z2-9]{3}-[A-Z2-9]{3}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.All(code.Replace("-", ""), c => Assert.Contains(c, ReceiverCode.Alphabet));
        Assert.NotEqual(code, ReceiverCode.Generate());
    }

    [Theory]
    [InlineData("K7P-2QX-9MRT-H4WZ")]
    [InlineData("k7p2qx9mrth4wz")]
    [InlineData(" K7P 2QX 9MRT H4WZ ")]
    public void ReceiverCode_NormalizesTypedCodes(string typed)
    {
        Assert.True(ReceiverCode.TryNormalize(typed, out var code));
        Assert.Equal("K7P2QX9MRTH4WZ", code);
        Assert.Equal("K7P-2QX-9MRT-H4WZ", ReceiverCode.Format(code));
    }

    [Fact]
    public void ReceiverCode_PairingKeyDependsOnlyOnTheNormalizedCode()
    {
        var key = ReceiverCode.DerivePairingKey("K7P-2QX-9MRT-H4WZ");

        Assert.Equal(32, key.Length);
        Assert.Equal(key, ReceiverCode.DerivePairingKey("k7p 2qx 9mrt h4wz"));
        Assert.NotEqual(key, ReceiverCode.DerivePairingKey("K7P-2QX-9MRT-H4WY"));
        Assert.Throws<ArgumentException>(() => ReceiverCode.DerivePairingKey("ABC"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7P-2QX-9MRT-H4W")]     // curto
    [InlineData("K7P-2QX-9MRT-H4WZA")]   // longo
    [InlineData("K7P-2QX-9MRT-H4W0")]    // 0 não faz parte do alfabeto
    public void ReceiverCode_RejectsInvalidCodes(string? typed)
    {
        Assert.False(ReceiverCode.TryNormalize(typed, out _));
    }
}
