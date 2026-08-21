using System.Text.Json;

namespace ZoomRecorder.Core.Processing;

public sealed record StoredStudyAssignment(
    Guid Id,
    string Description,
    string DueDateText,
    DateOnly? NormalizedDueDate,
    double Confidence,
    bool IsUserConfirmed,
    long SourceTimestampMilliseconds,
    int SourceOrder);

public sealed record CompletedLecturePackage(
    DateTimeOffset RecordedAt,
    ArtifactCheckpoint Artifact);

public interface IStudyMaterialStore
{
    Task<IReadOnlyList<StoredStudyAssignment>> ListAssignmentsAsync(
        Guid recordingId, CancellationToken cancellationToken);
    Task<ArtifactCheckpoint> GetTranscriptAsync(
        Guid recordingId, CancellationToken cancellationToken);
    Task SaveEditedTranscriptAsync(
        Guid recordingId, ArtifactCheckpoint transcript, CancellationToken cancellationToken);
    Task SaveRefreshedPackageAsync(
        Guid recordingId,
        ArtifactCheckpoint package,
        string sourceTranscriptSha256,
        IReadOnlyList<StoredStudyAssignment> assignments,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CompletedLecturePackage>> ListCompletedPackagesAsync(
        Guid classId, CancellationToken cancellationToken);
    Task SaveClassGuideAsync(
        Guid classId, ArtifactCheckpoint guide, CancellationToken cancellationToken);
    Task<Guid?> ReassignAndMarkGuidesPendingAsync(
        Guid recordingId, Guid newClassId, CancellationToken cancellationToken);
    Task MarkGuideRebuildRequestedAsync(Guid classId, CancellationToken cancellationToken);
}

public sealed class StudyMaterialMergeService
{
    private readonly IStudyMaterialStore store;
    private readonly IStudyGenerationClient generation;
    private readonly IProcessingArtifactStore artifacts;

    public StudyMaterialMergeService(
        IStudyMaterialStore store,
        IStudyGenerationClient generation,
        IProcessingArtifactStore artifacts)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public static IReadOnlyList<StoredStudyAssignment> Merge(
        IReadOnlyList<StudyAssignment> generated,
        IReadOnlyList<StoredStudyAssignment> existing)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(existing);

        var byOrder = existing.ToDictionary(item => item.SourceOrder);
        var result = new List<StoredStudyAssignment>(generated.Count);
        for (var index = 0; index < generated.Count; index++)
        {
            var suggestion = generated[index] ?? throw new ArgumentException("Assignments cannot contain null values.", nameof(generated));
            if (byOrder.TryGetValue(index, out var current) && current.IsUserConfirmed)
            {
                result.Add(current);
                continue;
            }

            result.Add(new StoredStudyAssignment(
                current?.Id ?? Guid.NewGuid(),
                suggestion.Description,
                suggestion.DueDateText,
                suggestion.NormalizedDueDate,
                suggestion.Confidence,
                false,
                suggestion.SourceTimestamp.StartMilliseconds,
                index));
        }

        foreach (var confirmed in existing.Where(item => item.IsUserConfirmed && item.SourceOrder >= generated.Count))
        {
            result.Add(confirmed);
        }

        return result.OrderBy(item => item.SourceOrder).ToArray();
    }

    public Task SaveEditedTranscriptAsync(
        Guid recordingId,
        ArtifactCheckpoint transcript,
        CancellationToken cancellationToken) =>
        store.SaveEditedTranscriptAsync(recordingId, transcript, cancellationToken);

    public async Task RefreshAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        var transcriptArtifact = await store.GetTranscriptAsync(recordingId, cancellationToken);
        var transcriptBytes = await artifacts.ReadVerifiedAsync(transcriptArtifact, cancellationToken)
            ?? throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
        Transcript transcript;
        try
        {
            transcript = JsonSerializer.Deserialize<Transcript>(transcriptBytes.Span)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
        }

        var package = await generation.GenerateLectureAsync(transcript, cancellationToken);
        StudyPackageValidator.Validate(package);
        var merged = Merge(
            package.Assignments,
            await store.ListAssignmentsAsync(recordingId, cancellationToken));
        var packageArtifact = await artifacts.WriteRecordingArtifactAsync(
            recordingId,
            $"lecture-package-refresh-{Guid.NewGuid():D}.json",
            JsonSerializer.SerializeToUtf8Bytes(package),
            cancellationToken);
        await store.SaveRefreshedPackageAsync(
            recordingId, packageArtifact, transcriptArtifact.Sha256, merged, cancellationToken);
    }
}
