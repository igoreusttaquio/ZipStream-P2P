namespace ZipP2P.App.Tests;

public sealed class StartupCommandTests
{
    [Fact]
    public void Send_TakesTheNextArgumentAsPath()
    {
        var command = StartupCommand.Parse(new[] { "--send", @"C:\Users\me\Documents\Relatório final.pdf" });

        Assert.Equal(@"C:\Users\me\Documents\Relatório final.pdf", command.SendPath);
        Assert.Null(command.ReceiveCode);
    }

    [Fact]
    public void Receive_NormalizesTheCode()
        => Assert.Equal("ABC123", StartupCommand.Parse(new[] { "--receive", "abc-123" }).ReceiveCode);

    [Fact]
    public void BarePath_OfAnExistingItem_MeansSend()
    {
        var path = Path.GetTempPath();
        Assert.Equal(path, StartupCommand.Parse(new[] { path }).SendPath);
    }

    [Theory]
    [InlineData]
    [InlineData("--send")]
    [InlineData("--unknown", "x")]
    [InlineData("does-not-exist.txt")]
    public void Otherwise_DoesNothing(params string[] args)
        => Assert.Equal(StartupCommand.None, StartupCommand.Parse(args));

    [Fact]
    public void RoomCodes_AreSixUnambiguousCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = TransferServices.GenerateRoomCode();
            Assert.Equal(6, code.Length);
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I');
            Assert.Equal(code, TransferServices.NormalizeRoomCode(code));
        }
    }

    [Theory]
    [InlineData(" ab c-12 3 ", "ABC123")]
    [InlineData("xyz", "XYZ")]
    public void NormalizeRoomCode_StripsSeparators(string input, string expected)
        => Assert.Equal(expected, TransferServices.NormalizeRoomCode(input));
}
