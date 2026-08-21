namespace ZoomRecorder.Core.Processing;

public sealed record DeletionEligibilityResult(bool CanDelete, string? Reason);

public static class DeletionEligibility
{
    public static DeletionEligibilityResult Evaluate(
        bool packageCommitted,
        bool assignmentsCommitted,
        bool guideOutcomeAccepted)
    {
        if (!packageCommitted) return new(false, "The study package is not committed.");
        if (!assignmentsCommitted) return new(false, "Assignments are not committed.");
        if (!guideOutcomeAccepted) return new(false, "The class guide outcome is not accepted.");
        return new(true, null);
    }

    public static DeletionEligibilityResult Evaluate(ProcessingJobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Evaluate(
            job.LecturePackageCommitted,
            job.AssignmentsCommitted,
            job.GuideOutcome is ClassGuideOutcome.Succeeded or ClassGuideOutcome.Pending);
    }
}
