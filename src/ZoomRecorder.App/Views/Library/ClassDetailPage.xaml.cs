using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class ClassDetailPage : Page
{
    private readonly ClassDetailViewModel _viewModel;
    private readonly Action _back;
    private readonly Action _recordClass;
    private readonly Action<RecordingListItem> _openLecture;
    private readonly Func<RecordingRecord, CancellationToken, Task> _deletion;

    public ClassDetailPage(
        ClassDetailViewModel viewModel,
        Action back,
        Action recordClass,
        Action<RecordingListItem> openLecture,
        Func<RecordingRecord, CancellationToken, Task> deletion)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _recordClass = recordClass ?? throw new ArgumentNullException(nameof(recordClass));
        _openLecture = openLecture ?? throw new ArgumentNullException(nameof(openLecture));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        DataContext = viewModel;
        UpdateLectureState();
    }

    private async void SearchClicked(object sender, RoutedEventArgs args)
    {
        await _viewModel.SearchAsync(SearchTextBox.Text, CancellationToken.None);
        UpdateLectureState();
    }

    private void UpdateLectureState()
    {
        NoLecturesPanel.Visibility = _viewModel.Lectures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LecturesList.Visibility = _viewModel.Lectures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ErrorPanel.Visibility = _viewModel.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        DeletionErrorPanel.IsOpen = _viewModel.DeletionErrorMessage is not null;
    }

    private async void RetryClicked(object sender, RoutedEventArgs args)
    {
        await _viewModel.LoadAsync(CancellationToken.None);
        UpdateLectureState();
    }

    private void BackClicked(object sender, RoutedEventArgs args) => _back();
    private void RecordClassClicked(object sender, RoutedEventArgs args) => _recordClass();
    private void OpenStudyGuideClicked(object sender, RoutedEventArgs args) => DetailsTabs.SelectedIndex = 1;
    private void LectureClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is RecordingListItem lecture && !_viewModel.IsDeleting(lecture.Id))
        {
            _openLecture(lecture);
        }
    }

    private async void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button deleteButton || !deleteButton.IsEnabled ||
            deleteButton.DataContext is not RecordingListItem item)
        {
            return;
        }

        deleteButton.IsEnabled = false;
        try
        {
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
            UpdateLectureState();
        }
        finally
        {
            deleteButton.IsEnabled = true;
        }
    }
}
