using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (item is null)
            return;

        var path = item.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (DataContext is MainViewModel vm)
            vm.SetInputPath(path);
    }
}
