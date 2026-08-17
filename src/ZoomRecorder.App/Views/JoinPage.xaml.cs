using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;

namespace ZoomRecorder.App.Views;

public sealed partial class JoinPage : Page
{
    private readonly JoinViewModel _viewModel;

    public JoinPage(JoinViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void PasscodeChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Passcode = ((PasswordBox)sender).Password;

    private async void JoinClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.JoinAndRecordAsync();
}
