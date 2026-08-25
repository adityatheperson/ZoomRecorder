using System.Globalization;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed record RecordingListItem(RecordingRecord Recording, string ProcessingStatus = "Not transcribed")
{
    public Guid Id => Recording.Id;
    public Guid? ClassId => Recording.ClassId;
    public string FileName => Recording.FileName;
    public DateTimeOffset RecordedAt => Recording.RecordedAt;
    public string RecordedAtText => Recording.RecordedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string DurationText => Recording.Duration.ToString(
        Recording.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
    public bool IsUnassigned => Recording.ClassId is null;
    public string AssignmentStatus => IsUnassigned ? "Unassigned" : "Assigned";
}

public sealed record ClassCardViewModel(ClassRecord Class, IReadOnlyList<RecordingRecord> Lectures)
{
    public Guid Id => Class.Id;
    public string Name => Class.Name;
    public string Term => string.IsNullOrWhiteSpace(Class.Term) ? "No term" : Class.Term;
    public int LectureCount => Lectures.Count;
    public RecordingRecord? MostRecentLecture => Lectures
        .OrderByDescending(item => item.RecordedAt)
        .FirstOrDefault();
    public string MostRecentLectureText => MostRecentLecture is null
        ? "No lectures yet"
        : $"Latest {MostRecentLecture.RecordedAt.ToLocalTime():g}";
    public string StudyPackageStatus => "Study package pending";
}
