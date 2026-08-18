using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class AssignRecordingDialog : ContentDialog
{
    private readonly AssignRecordingViewModel _viewModel;

    public AssignRecordingDialog(AssignRecordingViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        RememberMeetingCheckBox.Visibility = viewModel.CanRememberMeeting
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (viewModel.Classes.Count == 0)
        {
            ExistingClassOption.IsChecked = false;
            NewClassOption.IsChecked = true;
        }
    }

    public ClassRecord? AssignedClass { get; private set; }

    private async void AssignRecording(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        ErrorText.Text = string.Empty;

        try
        {
            var rememberMeeting = RememberMeetingCheckBox.IsChecked == true;
            if (NewClassOption.IsChecked == true)
            {
                AssignedClass = await _viewModel.CreateAndAssignAsync(
                    ClassNameBox.Text,
                    TermBox.Text,
                    rememberMeeting,
                    CancellationToken.None);
            }
            else if (ClassPicker.SelectedItem is ClassRecord selectedClass)
            {
                await _viewModel.AssignExistingAsync(
                    selectedClass.Id,
                    rememberMeeting,
                    CancellationToken.None);
                AssignedClass = selectedClass;
            }
            else
            {
                ErrorText.Text = "Choose a class or create a new one.";
                return;
            }

            args.Cancel = false;
        }
        catch (ArgumentException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch
        {
            ErrorText.Text = "The class library is unavailable right now. Try again later.";
        }
        finally
        {
            deferral.Complete();
        }
    }
}
