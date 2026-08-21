using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class StudyMaterialMergeServiceTests
{
    [Fact]
    public void Regeneration_preserves_confirmed_assignment_identity_and_edits()
    {
        var id = Guid.NewGuid();
        var existing = new[] { Stored("Read chapter 4", "Monday", true, id, 0) };
        var generated = new[] { Generated("Read chapter 5", "Friday") };

        var result = StudyMaterialMergeService.Merge(generated, existing);

        var assignment = Assert.Single(result);
        Assert.Equal(id, assignment.Id);
        Assert.Equal("Read chapter 4", assignment.Description);
        Assert.Equal("Monday", assignment.DueDateText);
        Assert.True(assignment.IsUserConfirmed);
    }

    [Fact]
    public void Regeneration_updates_unconfirmed_assignment_but_keeps_its_identity()
    {
        var id = Guid.NewGuid();
        var existing = new[] { Stored("Old suggestion", "Tuesday", false, id, 0) };

        var result = StudyMaterialMergeService.Merge(
            new[] { Generated("New suggestion", "Thursday") }, existing);

        var assignment = Assert.Single(result);
        Assert.Equal(id, assignment.Id);
        Assert.Equal("New suggestion", assignment.Description);
        Assert.Equal("Thursday", assignment.DueDateText);
        Assert.False(assignment.IsUserConfirmed);
    }

    [Fact]
    public void Regeneration_adds_new_suggestions_after_existing_positions()
    {
        var result = StudyMaterialMergeService.Merge(
            new[] { Generated("First", "Monday"), Generated("Second", "Tuesday") },
            new[] { Stored("First", "Monday", true, Guid.NewGuid(), 0) });

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].SourceOrder);
        Assert.Equal(1, result[1].SourceOrder);
        Assert.NotEqual(Guid.Empty, result[1].Id);
    }

    private static StoredStudyAssignment Stored(
        string description, string dueDate, bool confirmed, Guid id, int order) =>
        new(id, description, dueDate, null, 0.8, confirmed, 100, order);

    private static StudyAssignment Generated(string description, string dueDate) =>
        new(description, dueDate, null, 0.7, new TimestampReference(100, 200));
}
