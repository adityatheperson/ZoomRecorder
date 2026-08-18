using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Services;

namespace ZoomRecorder.App.Views;

public sealed partial class CompletionPage : Page
{
    private readonly CompletionViewModel viewModel;
    private readonly Action done;
    private readonly Func<Task>? assign;

    public CompletionPage(CompletionViewModel viewModel, Action done)
        : this(viewModel, done, assign: null)
    {
    }

    public CompletionPage(CompletionViewModel viewModel, Action done, Func<Task>? assign)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.done = done ?? throw new ArgumentNullException(nameof(done));
        this.assign = assign;
        DataContext = viewModel;
    }

    private string RecordingDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Meeting Recordings");
    private void OpenRecording(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.Open(viewModel.Path, RecordingDirectory);
    private void OpenFolder(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.SelectInFolder(viewModel.Path, RecordingDirectory);
    private async void AssignToClass(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (assign is not null)
        {
            await assign();
        }
    }
    private void Done(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => done();
}
