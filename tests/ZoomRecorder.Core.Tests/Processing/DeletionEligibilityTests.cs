using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class DeletionEligibilityTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void Delete_requires_package_assignments_and_accepted_guide_outcome(
        bool package, bool assignments, bool guideOutcome, bool expected)
    {
        Assert.Equal(expected, DeletionEligibility.Evaluate(package, assignments, guideOutcome).CanDelete);
    }

    [Fact]
    public void Ineligible_result_identifies_missing_commit()
    {
        var result = DeletionEligibility.Evaluate(packageCommitted: true, assignmentsCommitted: false, guideOutcomeAccepted: true);

        Assert.False(result.CanDelete);
        Assert.Equal("Assignments are not committed.", result.Reason);
    }
}
