using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class ClassesPage : Page
{
    private readonly Action<ClassCardViewModel> _openClass;
    private readonly Action _recordClass;
    private readonly Action _reviewRecordings;
    private readonly Func<Task> _retry;

    public ClassesPage(
        ClassesViewModel? viewModel,
        Action<ClassCardViewModel> openClass,
        Action recordClass,
        Action reviewRecordings,
        Func<Task> retry,
        bool isLoading = false)
    {
        InitializeComponent();
        _openClass = openClass ?? throw new ArgumentNullException(nameof(openClass));
        _recordClass = recordClass ?? throw new ArgumentNullException(nameof(recordClass));
        _reviewRecordings = reviewRecordings ?? throw new ArgumentNullException(nameof(reviewRecordings));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        DataContext = viewModel;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = !isLoading && (viewModel is null || viewModel.ErrorMessage is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryContent.Visibility = !isLoading && viewModel is not null && viewModel.ErrorMessage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (viewModel is not null)
        {
            UnavailableText.Text = viewModel.ErrorMessage ?? UnavailableText.Text;
            EmptyClassesPanel.Visibility = viewModel.Classes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClassesList.Visibility = viewModel.Classes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ClassClicked(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is ClassCardViewModel classCard)
        {
            _openClass(classCard);
        }
    }

    private void RecordClassClicked(object sender, RoutedEventArgs args) => _recordClass();
    private void ReviewRecordingsClicked(object sender, RoutedEventArgs args) => _reviewRecordings();
    private async void RetryClicked(object sender, RoutedEventArgs args) => await _retry();
}
