using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class RecordingsPage : Page
{
    private readonly RecordingsViewModel? _viewModel;
    private readonly Func<RecordingRecord, CancellationToken, Task<bool>>? _assignment;
    private readonly Func<RecordingRecord, CancellationToken, Task>? _deletion;
    private readonly Action _recordClass;
    private readonly Func<Task> _retry;

    public RecordingsPage(
        RecordingsViewModel? viewModel,
        Func<RecordingRecord, CancellationToken, Task<bool>>? assignment,
        Action recordClass,
        Func<Task> retry,
        bool isLoading = false,
        Func<RecordingRecord, CancellationToken, Task>? deletion = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _assignment = assignment;
        _deletion = deletion;
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
        if (_viewModel is not null && _assignment is not null &&
            ((FrameworkElement)sender).DataContext is RecordingListItem item)
        {
            var assigned = await _viewModel.AssignAsync(item, _assignment, CancellationToken.None);
            if (assigned)
            {
                await _viewModel.SearchAsync(SearchTextBox.Text, CancellationToken.None);
            }

            UpdateListState();
        }
    }

    private async void RetryAssignmentClicked(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null || _assignment is null)
        {
            return;
        }

        var assigned = await _viewModel.RetryAssignmentAsync(_assignment, CancellationToken.None);
        if (assigned)
        {
            await _viewModel.SearchAsync(SearchTextBox.Text, CancellationToken.None);
        }

        UpdateListState();
    }

    private async void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null || _deletion is null ||
            ((FrameworkElement)sender).DataContext is not RecordingListItem item)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete recording?",
            Content = $"Permanently delete {item.FileName} and all associated study materials? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.DeleteAsync(item, _deletion, CancellationToken.None);
        UpdateListState();
    }

    private void UpdateListState()
    {
        var showLibrary = _viewModel is not null && _viewModel.ErrorMessage is null;
        var hasItems = showLibrary && _viewModel!.Recordings.Count > 0;
        RecordingsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = showLibrary && !hasItems ? Visibility.Visible : Visibility.Collapsed;
        AssignmentErrorPanel.Visibility = showLibrary && _viewModel!.CanRetryAssignment
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeletionErrorPanel.Visibility = showLibrary && _viewModel!.DeletionErrorMessage is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RecordClassClicked(object sender, RoutedEventArgs args) => _recordClass();
    private async void RetryClicked(object sender, RoutedEventArgs args) => await _retry();
}
