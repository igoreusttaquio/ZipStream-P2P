using Avalonia;
using ZipP2P.App.Views;

namespace ZipP2P.App.Tests;

public sealed class CornerAnchorTests
{
    [Fact]
    public void StaysAnchored_WhileOnlyTheAppMovesTheWindow()
    {
        var anchor = new CornerAnchor();

        anchor.Moved(new PixelPoint(10, 10)); // posição inicial do sistema, antes de o app posicionar
        anchor.Placed(new PixelPoint(1500, 800));
        anchor.Moved(new PixelPoint(1500, 800));
        anchor.Placed(new PixelPoint(1500, 760)); // o conteúdo cresceu: o app sobe a janela
        anchor.Moved(new PixelPoint(1500, 760));

        Assert.True(anchor.IsAnchored);
    }

    [Fact]
    public void UserDrag_ReleasesTheAnchor_ForGood()
    {
        // Antes, arrastar para outro monitor mudava a escala (e o tamanho) e a janela voltava ao canto.
        var anchor = new CornerAnchor();
        anchor.Placed(new PixelPoint(1500, 800));

        anchor.Moved(new PixelPoint(-1200, 300));

        Assert.False(anchor.IsAnchored);
        anchor.Moved(new PixelPoint(1500, 800));
        Assert.False(anchor.IsAnchored);
    }
}
