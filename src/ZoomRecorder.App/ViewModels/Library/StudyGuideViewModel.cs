using System.Collections.ObjectModel;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class StudyGuideViewModel : LibraryViewModelBase
{
    public ObservableCollection<StudyGuideContribution> Topics { get; } = [];
    public bool IsUpdatePending { get; private set; }

    public void Apply(ClassStudyGuide? guide, bool isUpdatePending)
    {
        Replace(Topics, guide?.Topics ?? []);
        IsUpdatePending = isUpdatePending;
        RaisePropertyChanged(nameof(IsUpdatePending));
    }
}
