namespace ZipP2P.App.Tests;

public sealed class IceServerConfigTests
{
    [Fact]
    public void NoHost_MeansNoIceServers()
        => Assert.Empty(new IceServerConfig("  ", "user", "pass").ToUrls());

    [Fact]
    public void HostOnly_IsStunWithDefaultPort()
        => Assert.Equal(new[] { "stun:turn.example.com:3478" }, new IceServerConfig("turn.example.com", "", "").ToUrls());

    [Fact]
    public void WithUsername_AddsTurnKeepingThePort()
        => Assert.Equal(
            new[] { "stun:203.0.113.10:5349", "turn:zipstream:s3cret@203.0.113.10:5349" },
            new IceServerConfig(" turn:203.0.113.10:5349 ", " zipstream ", "s3cret").ToUrls());

    [Fact]
    public void Credentials_AreEscapedAndRoundTrip()
    {
        var config = new IceServerConfig("turn.example.com:3478", "me@home", "p:a@ss/word");

        var urls = config.ToUrls();

        Assert.Equal("turn:me%40home:p%3Aa%40ss%2Fword@turn.example.com:3478", urls[1]);
        Assert.Equal(config, IceServerConfig.FromUrls(urls));
    }

    [Fact]
    public void FromUrls_StunOnly()
        => Assert.Equal(new IceServerConfig("turn.example.com:3478", "", ""), IceServerConfig.FromUrls(new[] { "stun:turn.example.com:3478" }));

    [Fact]
    public void FromUrls_Empty()
        => Assert.Equal(IceServerConfig.Empty, IceServerConfig.FromUrls(Array.Empty<string>()));
}
