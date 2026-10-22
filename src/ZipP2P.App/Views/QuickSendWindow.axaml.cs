using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZipP2P.App.ViewModels;

namespace ZipP2P.App.Views;

/// <summary>
/// Janela compacta aberta pelo "Enviar com ZipStream": mostra o código (já copiado) e o progresso,
/// sem abrir a janela principal. Fechar durante o envio apenas minimiza.
/// </summary>
public partial class QuickSendWindow : Window
{
    public QuickSendWindow()
    {
        InitializeComponent();

        // A altura muda com o conteúdo (SizeToContent): mantém a janela ancorada no canto.
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                PlaceNearTray();
        };
    }

    /// <summary>Quantas estão abertas (empilha as janelas no canto da tela).</summary>
    private static int _openCount;

    private int _stackIndex;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AppServices.Attach(this, overwrite: false);
        _stackIndex = _openCount++;
        PlaceNearTray();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Não interrompe um envio em andamento pelo X da janela: só tira da frente.
        if (DataContext is SendViewModel { IsBusy: true } && !e.IsProgrammatic)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _openCount--;
        base.OnClosed(e);
    }

    /// <summary>
    /// Canto inferior direito do monitor principal (onde fica a bandeja no Windows). Usa a escala
    /// desse monitor: com monitores de DPI diferentes, a escala da posição inicial não serve.
    /// </summary>
    private void PlaceNearTray()
    {
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var size = FrameSize ?? Bounds.Size;
        var width = (int)(size.Width * scale);
        var height = (int)(size.Height * scale);
        var margin = (int)(16 * scale);
        var offset = _stackIndex * (int)(28 * scale);
        Position = new PixelPoint(
            area.Right - width - margin - offset,
            Math.Max(area.Y, area.Bottom - height - margin - offset));
    }

    private void OnOpenMainClick(object? sender, RoutedEventArgs e) => (Application.Current as App)?.ShowMainWindow();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
