using Avalonia.Controls;
using Avalonia.Input;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

public partial class FileTransferWindow : Window
{
    private readonly FileTransferViewModel _vm;
    private bool _closing;

    public FileTransferWindow(FileTransferViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += () => Close();
        LocalList.DoubleTapped += (_, _) => vm.OpenLocalCommand.Execute(vm.SelectedLocal);
        RemoteList.DoubleTapped += (_, _) => vm.OpenRemoteCommand.Execute(vm.SelectedRemote);
        LocalList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { vm.OpenLocalCommand.Execute(vm.SelectedLocal); } };
        RemoteList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { vm.OpenRemoteCommand.Execute(vm.SelectedRemote); } };
        Opened += async (_, _) => await vm.StartAsync();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        e.Cancel = true;
        await _vm.DisposeAsync();
        Close();
    }
}
