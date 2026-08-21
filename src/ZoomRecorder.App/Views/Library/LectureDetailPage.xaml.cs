using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class LectureDetailPage : Page
{
    private readonly LectureDetailViewModel viewModel;
    private readonly Action back;
    private readonly Func<Task> process;

    public LectureDetailPage(LectureDetailViewModel viewModel, Action back, Func<Task> process)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.back = back ?? throw new ArgumentNullException(nameof(back));
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        DataContext = viewModel;
    }

    private void BackClicked(object sender, RoutedEventArgs args) => back();
    private async void SaveTranscriptClicked(object sender, RoutedEventArgs args) =>
        await viewModel.SaveTranscriptAsync(CancellationToken.None);
    private async void RefreshClicked(object sender, RoutedEventArgs args) =>
        await viewModel.RefreshStudyMaterialsAsync(CancellationToken.None);
    private async void ProcessClicked(object sender, RoutedEventArgs args) => await process();
}
