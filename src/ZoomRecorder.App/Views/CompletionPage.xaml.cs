using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Services;
using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Views;

public sealed partial class CompletionPage : Page
{
    private readonly CompletionViewModel viewModel;
    private readonly Action done;
    private readonly Func<Task>? assign;
    private readonly string recordingDirectory;

    public CompletionPage(CompletionViewModel viewModel, Action done)
        : this(viewModel, done, assign: null, LibraryPaths.DefaultRecordingsRoot())
    {
    }

    public CompletionPage(CompletionViewModel viewModel, Action done, Func<Task>? assign, string recordingDirectory)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.done = done ?? throw new ArgumentNullException(nameof(done));
        this.assign = assign;
        this.recordingDirectory = Path.GetFullPath(recordingDirectory);
        DataContext = viewModel;
    }

    private void OpenRecording(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.Open(viewModel.Path, recordingDirectory);
    private void OpenFolder(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LocalFileActions.SelectInFolder(viewModel.Path, recordingDirectory);
    private async void AssignToClass(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (assign is not null)
        {
            await assign();
        }
    }
    private void Done(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => done();
}
