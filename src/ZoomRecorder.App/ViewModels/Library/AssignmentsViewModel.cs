using System.Collections.ObjectModel;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class AssignmentsViewModel : LibraryViewModelBase
{
    public ObservableCollection<StoredStudyAssignment> Items { get; } = [];

    public void ReplaceWith(IEnumerable<StoredStudyAssignment> assignments) => Replace(Items,
        assignments.OrderBy(item => item.NormalizedDueDate).ThenBy(item => item.SourceOrder));
}
