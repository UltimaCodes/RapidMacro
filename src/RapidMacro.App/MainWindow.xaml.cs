using System.Windows;
using RapidMacro.App.ViewModels;
using RapidMacro.Core.Input;
using Wpf.Ui.Controls;

namespace RapidMacro.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
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
}
