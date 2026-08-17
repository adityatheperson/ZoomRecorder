using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels;
using System.Diagnostics;

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
    private void OpenRecording(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Process.Start(new ProcessStartInfo(viewModel.Path) { UseShellExecute = true });
    private void OpenFolder(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Process.Start("explorer.exe", $"/select,\"{viewModel.Path}\"");
    private void Done(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => done();
}
