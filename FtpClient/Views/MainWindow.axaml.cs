using Avalonia.Controls;
using Avalonia.Input;
using FtpClient.ViewModels;

namespace FtpClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLocalItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedLocalItem is { } item)
        {
            if (item.IsDirectory)
            {
                vm.NavigateLocalToCommand.Execute(item);
            }
        }
    }

    private void OnRemoteItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedRemoteItem is { } item)
        {
            if (item.IsDirectory)
            {
                vm.NavigateRemoteToCommand.Execute(item);
            }
        }
    }
}