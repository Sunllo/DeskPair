using System.Collections.Specialized;
using Avalonia.Controls;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChatViewModel vm)
            {
                vm.Lines.CollectionChanged += OnLinesChanged;
            }
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Scroller.ScrollToEnd();
}
