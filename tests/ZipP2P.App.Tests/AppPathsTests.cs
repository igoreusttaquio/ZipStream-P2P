namespace ZipP2P.App.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Default_IsLocalAppData_WithoutInstanceSuffix()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipStream");

        Assert.Equal(expected, AppPaths.ResolveDataDirectory(null));
        Assert.Equal(expected, AppPaths.ResolveDataDirectory(""));
        Assert.Equal(string.Empty, AppPaths.InstanceSuffixFor(null));
    }

    [Fact]
    public void CustomDirectory_HasItsOwnInstance()
    {
        var a = Path.Combine(Path.GetTempPath(), "perfil-a");
        var b = Path.Combine(Path.GetTempPath(), "perfil-b");

        Assert.Equal(Path.GetFullPath(a), AppPaths.ResolveDataDirectory(a));
        Assert.Matches("^-[0-9a-f]{12}$", AppPaths.InstanceSuffixFor(a));
        Assert.NotEqual(AppPaths.InstanceSuffixFor(a), AppPaths.InstanceSuffixFor(b));
        Assert.Equal(AppPaths.InstanceSuffixFor(a), AppPaths.InstanceSuffixFor(a.ToUpperInvariant()));
    }
}
