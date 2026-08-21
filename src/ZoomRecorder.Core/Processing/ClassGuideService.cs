using System.Text.Json;

namespace ZoomRecorder.Core.Processing;

public sealed class ClassGuideService
{
    private readonly IStudyMaterialStore store;
    private readonly IStudyGenerationClient generation;
    private readonly IProcessingArtifactStore artifacts;

    public ClassGuideService(
        IStudyMaterialStore store,
        IStudyGenerationClient generation,
        IProcessingArtifactStore artifacts)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public async Task RebuildAsync(Guid classId, CancellationToken cancellationToken)
    {
        var completed = await store.ListCompletedPackagesAsync(classId, cancellationToken);
        var packages = new List<StudyPackage>(completed.Count);
        foreach (var item in completed.OrderBy(item => item.RecordedAt))
        {
            var bytes = await artifacts.ReadVerifiedAsync(item.Artifact, cancellationToken)
                ?? throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
            StudyPackage package;
            try
            {
                package = JsonSerializer.Deserialize<StudyPackage>(bytes.Span)
                    ?? throw new JsonException();
                StudyPackageValidator.Validate(package);
            }
            catch (Exception exception) when (exception is JsonException or StudyPackageValidationException)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
            }

            packages.Add(package);
        }

        if (packages.Count == 0)
        {
            return;
        }

        var guide = await generation.GenerateGuideAsync(packages, cancellationToken);
        ValidateGuide(guide);
        var artifact = await artifacts.WriteClassArtifactAsync(
            classId,
            $"class-guide-{Guid.NewGuid():D}.json",
            JsonSerializer.SerializeToUtf8Bytes(guide),
            cancellationToken);
        await store.SaveClassGuideAsync(classId, artifact, cancellationToken);
    }

    public async Task ReassignAsync(Guid recordingId, Guid newClassId, CancellationToken cancellationToken)
    {
        var oldClassId = await store.ReassignAndMarkGuidesPendingAsync(
            recordingId, newClassId, cancellationToken);
        if (oldClassId is { } oldId && oldId != newClassId)
        {
            await store.MarkGuideRebuildRequestedAsync(oldId, cancellationToken);
        }

        await store.MarkGuideRebuildRequestedAsync(newClassId, cancellationToken);
    }

    private static void ValidateGuide(ClassStudyGuide guide)
    {
        if (guide is null || guide.SchemaVersion != StudyPackageValidator.SupportedSchemaVersion || guide.Topics is null ||
            guide.Topics.Any(topic => topic is null || string.IsNullOrWhiteSpace(topic.Topic) ||
                topic.Contributions is null || topic.Contributions.Any(string.IsNullOrWhiteSpace)))
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.ClassGuideUpdateFailed);
        }
    }
}
