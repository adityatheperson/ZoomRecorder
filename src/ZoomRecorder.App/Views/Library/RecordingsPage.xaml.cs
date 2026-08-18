using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class RecordingsPage : Page
{
    private readonly RecordingsViewModel? _viewModel;
    private readonly Func<RecordingRecord, Task>? _assignment;
    private readonly Action _recordClass;
    private readonly Func<Task> _retry;

    public RecordingsPage(
        RecordingsViewModel? viewModel,
        Func<RecordingRecord, Task>? assignment,
        Action recordClass,
        Func<Task> retry,
        bool isLoading = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _assignment = assignment;
        _recordClass = recordClass ?? throw new ArgumentNullException(nameof(recordClass));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        DataContext = viewModel;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = !isLoading && (viewModel is null || viewModel.ErrorMessage is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (viewModel is not null)
        {
            UnavailableText.Text = viewModel.ErrorMessage ?? UnavailableText.Text;
            UpdateListState();
        }
    }

    private async void SearchClicked(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null)
        {
            return;
        }

        await _viewModel.SearchAsync(SearchTextBox.Text, CancellationToken.None);
        UnavailablePanel.Visibility = _viewModel.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        UnavailableText.Text = _viewModel.ErrorMessage ?? UnavailableText.Text;
        UpdateListState();
    }

    private async void AssignClicked(object sender, RoutedEventArgs args)
    {
        if (_assignment is not null && ((FrameworkElement)sender).DataContext is RecordingListItem item)
        {
            await _assignment(item.Recording);
        }
    }

    private void UpdateListState()
    {
        var showLibrary = _viewModel is not null && _viewModel.ErrorMessage is null;
        var hasItems = showLibrary && _viewModel!.Recordings.Count > 0;
        RecordingsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = showLibrary && !hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordClassClicked(object sender, RoutedEventArgs args) => _recordClass();
    private async void RetryClicked(object sender, RoutedEventArgs args) => await _retry();
}
