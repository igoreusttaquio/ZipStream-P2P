using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace ZipP2P.App.Views;

public partial class PendingList : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PendingList, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<PendingList, string>(nameof(Hint), string.Empty);

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<PendingList, IEnumerable?>(nameof(Items));

    public PendingList()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }
}
