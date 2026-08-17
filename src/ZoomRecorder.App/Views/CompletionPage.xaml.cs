using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Services;

namespace ZoomRecorder.App.Views;

public sealed partial class CompletionPage : Page
{
    private readonly CompletionViewModel viewModel;
    private readonly Action done;
    public CompletionPage(CompletionViewModel viewModel, Action done)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.done = done;
        DataContext = viewModel;
    }
    private string RecordingDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Meeting Recordings");
    private void OpenRecording(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.Open(viewModel.Path, RecordingDirectory);
    private void OpenFolder(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.SelectInFolder(viewModel.Path, RecordingDirectory);
    private void Done(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => done();
}
