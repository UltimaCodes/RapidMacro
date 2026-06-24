using System.Windows;
using Macrofy.App.ViewModels;
using Macrofy.Core.Input;
using Wpf.Ui.Controls;

namespace Macrofy.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new RawInputHookBackend());
        DataContext = _viewModel;

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RefreshDevices();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ClearLog();

    private void RenameButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RenameSelected();
}
