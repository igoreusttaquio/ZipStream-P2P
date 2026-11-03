using Avalonia;

namespace ZipP2P.App.Views;

/// <summary>
/// Janela ancorada num canto da tela: o app a reposiciona quando o tamanho muda, mas só até o usuário
/// movê-la. Uma posição diferente da última definida pelo app é um arraste.
/// </summary>
public sealed class CornerAnchor
{
    private PixelPoint? _placedAt;

    public bool IsAnchored { get; private set; } = true;

    /// <summary>O app colocou a janela em <paramref name="position"/>.</summary>
    public void Placed(PixelPoint position) => _placedAt = position;

    /// <summary>A janela foi para <paramref name="position"/> (pelo app ou pelo usuário).</summary>
    public void Moved(PixelPoint position)
    {
        if (_placedAt is { } placed && position != placed)
            IsAnchored = false;
    }
}
