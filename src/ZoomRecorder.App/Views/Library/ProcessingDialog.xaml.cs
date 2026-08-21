using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class ProcessingDialog : ContentDialog
{
    private readonly ProcessingViewModel viewModel;

    public ProcessingDialog(ProcessingViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
    }

    private async void StartClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { await viewModel.StartAsync(CancellationToken.None); }
        finally { deferral.Complete(); }
    }
}
