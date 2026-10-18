using Avalonia.Controls;
using ZipP2P.App.ViewModels;

namespace ZipP2P.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainViewModel vm)
            vm.SetStorageProvider(StorageProvider);
    }
}