using Microsoft.Data.Sqlite;
using ZipP2P.App.Devices;
using ZipP2P.Core.Security;

namespace ZipP2P.App.Tests;

public sealed class DeviceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-devices-").FullName;

    private string DbPath => Path.Combine(_dir, "settings.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Profile_KeepsTheSameIdentityAcrossLoads()
    {
        var profile = new DeviceProfile(new SettingsStore(DbPath));

        using var first = profile.LoadIdentity();
        using var second = new DeviceProfile(new SettingsStore(DbPath)).LoadIdentity();

        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void Profile_PrivateKeyIsNotStoredInPlainTextOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // DPAPI só existe no Windows

        var store = new SettingsStore(DbPath);
        using var identity = new DeviceProfile(store).LoadIdentity();

        var stored = Convert.FromBase64String(store.Get("DeviceIdentity")!);
        Assert.NotEqual(identity.ExportPrivateKey(), stored);
    }

    [Fact]
    public void Profile_ReceiverCodeIsFixedUntilRegenerated()
    {
        var profile = new DeviceProfile(new SettingsStore(DbPath));

        var code = profile.ReceiverCode;
        Assert.True(ReceiverCode.TryNormalize(code, out _));
        Assert.Equal(code, new DeviceProfile(new SettingsStore(DbPath)).ReceiverCode);

        var regenerated = profile.RegenerateReceiverCode();
        Assert.NotEqual(code, regenerated);
        Assert.Equal(regenerated, profile.ReceiverCode);
    }

    [Fact]
    public void Profile_DeviceNameDefaultsToMachineName()
    {
        var profile = new DeviceProfile(new SettingsStore(DbPath));
        Assert.Equal(Environment.MachineName, profile.DeviceName);

        profile.DeviceName = "  Escritório ";
        Assert.Equal("Escritório", profile.DeviceName);
    }

    [Fact]
    public void TrustedDevices_TrustFindRenameRemove()
    {
        var store = new TrustedDeviceStore(DbPath);
        using var notebook = DeviceIdentity.Create();
        using var desktop = DeviceIdentity.Create();

        Assert.Null(store.Find(notebook.DeviceId));

        var trusted = store.Trust(notebook.PublicKey, "Notebook");
        store.Trust(desktop.PublicKey, "Desktop");

        Assert.Equal(notebook.DeviceId, trusted.DeviceId);
        Assert.Equal(notebook.PublicKey, store.Find(notebook.DeviceId)!.PublicKey);
        Assert.Equal(["Desktop", "Notebook"], store.List().Select(d => d.Name));

        store.Rename(notebook.DeviceId, "Notebook Casa");
        Assert.Equal("Notebook Casa", new TrustedDeviceStore(DbPath).Find(notebook.DeviceId)!.Name);

        store.Remove(notebook.DeviceId);
        Assert.Null(store.Find(notebook.DeviceId));
        Assert.Single(store.List());
    }

    [Fact]
    public void TrustedDevices_PairingAgainKeepsTheOriginalDate()
    {
        var store = new TrustedDeviceStore(DbPath);
        using var device = DeviceIdentity.Create();

        var first = store.Trust(device.PublicKey, "PC");
        var again = store.Trust(device.PublicKey, "PC novo");

        Assert.Equal(first.PairedAt, again.PairedAt);
        Assert.Equal("PC novo", again.Name);
        Assert.Single(store.List());
    }
}
