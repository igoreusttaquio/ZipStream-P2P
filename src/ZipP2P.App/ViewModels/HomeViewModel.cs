using CommunityToolkit.Mvvm.ComponentModel;

namespace ZipP2P.App.ViewModels;

/// <summary>Tela principal: abas Enviar, Receber, Dispositivos e Ferramentas.</summary>
public partial class HomeViewModel : ViewModelBase
{
    public const int SendTab = 0;
    public const int ReceiveTab = 1;
    public const int DevicesTab = 2;
    public const int ToolsTab = 3;

    public HomeViewModel()
    {
        Send.ShowDevices = () => SelectedTab = DevicesTab;
    }

    public SendViewModel Send { get; } = new();

    public ReceiveViewModel Receive { get; } = new();

    public DevicesViewModel Devices { get; } = new();

    public ToolsViewModel Tools { get; } = new();

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>Arquivo/pasta solto na janela: vai para a aba atual (Ferramentas) ou para Enviar.</summary>
    public void OnItemDropped(string path)
    {
        if (SelectedTab == ToolsTab)
        {
            Tools.SetInputPath(path);
            return;
        }

        SelectedTab = SendTab;
        Send.SetPath(path);
    }
}
