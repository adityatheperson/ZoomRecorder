using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;
using System.ComponentModel;

namespace ZoomRecorder.App.Views;

public sealed partial class JoinPage : Page
{
    private readonly JoinViewModel _viewModel;

    public JoinPage(JoinViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        Unloaded += (_, _) => _viewModel.PropertyChanged -= ViewModelPropertyChanged;
    }

    private void PasscodeChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Passcode = ((PasswordBox)sender).Password;

    private async void JoinClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.JoinAndRecordAsync();

    private void CancelClicked(object sender, RoutedEventArgs e) => _viewModel.CancelJoin();

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JoinViewModel.CanCancel))
        {
            CancelButton.Visibility = _viewModel.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
