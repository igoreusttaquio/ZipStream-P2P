using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ZipP2P.App.ViewModels;

namespace ZipP2P.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && DataContext is HomeViewModel vm)
            vm.OnItemDropped(path);
    }
}
