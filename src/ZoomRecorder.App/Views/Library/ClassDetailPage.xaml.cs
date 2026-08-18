using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class ClassDetailPage : Page
{
    private readonly ClassDetailViewModel _viewModel;
    private readonly Action _back;
    private readonly Action _recordClass;

    public ClassDetailPage(ClassDetailViewModel viewModel, Action back, Action recordClass)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _recordClass = recordClass ?? throw new ArgumentNullException(nameof(recordClass));
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
    }

    private async void RetryClicked(object sender, RoutedEventArgs args)
    {
        await _viewModel.LoadAsync(CancellationToken.None);
        UpdateLectureState();
    }

    private void BackClicked(object sender, RoutedEventArgs args) => _back();
    private void RecordClassClicked(object sender, RoutedEventArgs args) => _recordClass();
    private void OpenStudyGuideClicked(object sender, RoutedEventArgs args) => DetailsTabs.SelectedIndex = 1;
}
