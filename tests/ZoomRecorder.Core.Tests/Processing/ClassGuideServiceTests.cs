using System.Text.Json;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class ClassGuideServiceTests
{
    [Fact]
    public async Task Transcript_edit_marks_package_stale_and_refresh_skips_transcription()
    {
        var store = new FakeStudyMaterialStore();
        var generation = new FakeGenerationClient();
        var artifacts = new FakeArtifacts();
        var service = new StudyMaterialMergeService(store, generation, artifacts);
        var recordingId = Guid.NewGuid();
        var edited = TranscriptArtifact("edited transcript");

        await service.SaveEditedTranscriptAsync(recordingId, edited, default);
        await service.RefreshAsync(recordingId, default);

        Assert.Equal(edited.Sha256, store.Transcript?.Sha256);
        Assert.True(store.WasMarkedStale);
        Assert.Equal(1, generation.LectureCalls);
        Assert.Equal(0, store.TranscriptionCalls);
        Assert.False(store.PackageIsStale);
    }

    [Fact]
    public async Task Guide_rebuild_uses_completed_packages_in_recording_order()
    {
        var store = new FakeStudyMaterialStore();
        var generation = new FakeGenerationClient();
        var artifacts = new FakeArtifacts();
        var classId = Guid.NewGuid();
        store.CompletedPackages.Add((new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), PackageArtifact("second")));
        store.CompletedPackages.Add((new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), PackageArtifact("first")));

        await new ClassGuideService(store, generation, artifacts).RebuildAsync(classId, default);

        Assert.Equal(new[] { "first", "second" }, generation.GuideLectureTitles);
        Assert.Equal(classId, store.SavedGuideClassId);
        Assert.False(store.GuidePending);
    }

    [Fact]
    public async Task Reassignment_commits_relationship_then_rebuilds_old_and_new_guides()
    {
        var store = new FakeStudyMaterialStore { OldClassId = Guid.NewGuid() };
        var newClassId = Guid.NewGuid();
        var service = new ClassGuideService(store, new FakeGenerationClient(), new FakeArtifacts());

        await service.ReassignAsync(Guid.NewGuid(), newClassId, default);

        Assert.Equal(new[] { store.OldClassId!.Value, newClassId }, store.RebuildRequests);
        Assert.True(store.RelationshipCommittedBeforeFirstRequest);
        Assert.Equal(0, store.TranscriptionCalls);
    }

    private static ArtifactCheckpoint TranscriptArtifact(string text) =>
        FakeArtifacts.AddJson(new Transcript([new TranscriptSegment(0, 100, text)]));

    private static ArtifactCheckpoint PackageArtifact(string title) =>
        FakeArtifacts.AddJson(Package(title));

    private static StudyPackage Package(string title) => new(
        1, title, new DateOnly(2026, 8, 1), "summary", [], [], [], [], []);

    private sealed class FakeGenerationClient : IStudyGenerationClient
    {
        public int LectureCalls { get; private set; }
        public IReadOnlyList<string> GuideLectureTitles { get; private set; } = [];

        public Task<StudyPackage> GenerateLectureAsync(Transcript transcript, CancellationToken cancellationToken)
        {
            LectureCalls++;
            return Task.FromResult(Package("refreshed"));
        }

        public Task<ClassStudyGuide> GenerateGuideAsync(IReadOnlyList<StudyPackage> lectures, CancellationToken cancellationToken)
        {
            GuideLectureTitles = lectures.Select(item => item.LectureTitle).ToArray();
            return Task.FromResult(new ClassStudyGuide(1, []));
        }
    }

    private sealed class FakeStudyMaterialStore : IStudyMaterialStore
    {
        public Guid? OldClassId { get; init; }
        public ArtifactCheckpoint? Transcript { get; private set; }
        public bool WasMarkedStale { get; private set; }
        public bool PackageIsStale { get; private set; } = true;
        public int TranscriptionCalls { get; private set; }
        public List<(DateTimeOffset RecordedAt, ArtifactCheckpoint Artifact)> CompletedPackages { get; } = [];
        public List<Guid> RebuildRequests { get; } = [];
        public bool RelationshipCommitted { get; private set; }
        public bool RelationshipCommittedBeforeFirstRequest { get; private set; }
        public Guid? SavedGuideClassId { get; private set; }
        public bool GuidePending { get; private set; } = true;

        public Task<IReadOnlyList<StoredStudyAssignment>> ListAssignmentsAsync(Guid recordingId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredStudyAssignment>>([]);

        public Task<ArtifactCheckpoint> GetTranscriptAsync(Guid recordingId, CancellationToken cancellationToken) =>
            Task.FromResult(Transcript ?? throw new InvalidOperationException());

        public Task SaveEditedTranscriptAsync(Guid recordingId, ArtifactCheckpoint transcript, CancellationToken cancellationToken)
        {
            Transcript = transcript;
            WasMarkedStale = true;
            PackageIsStale = true;
            return Task.CompletedTask;
        }

        public Task SaveRefreshedPackageAsync(Guid recordingId, ArtifactCheckpoint package, string sourceTranscriptSha256, IReadOnlyList<StoredStudyAssignment> assignments, CancellationToken cancellationToken)
        {
            PackageIsStale = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CompletedLecturePackage>> ListCompletedPackagesAsync(Guid classId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompletedLecturePackage>>(CompletedPackages.Select(x => new CompletedLecturePackage(x.RecordedAt, x.Artifact)).ToArray());

        public Task SaveClassGuideAsync(Guid classId, ArtifactCheckpoint guide, CancellationToken cancellationToken)
        {
            SavedGuideClassId = classId;
            GuidePending = false;
            return Task.CompletedTask;
        }

        public Task<Guid?> ReassignAndMarkGuidesPendingAsync(Guid recordingId, Guid newClassId, CancellationToken cancellationToken)
        {
            RelationshipCommitted = true;
            return Task.FromResult(OldClassId);
        }

        public Task MarkGuideRebuildRequestedAsync(Guid classId, CancellationToken cancellationToken)
        {
            RelationshipCommittedBeforeFirstRequest |= RelationshipCommitted;
            RebuildRequests.Add(classId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifacts : IProcessingArtifactStore
    {
        private static readonly Dictionary<string, byte[]> Values = [];

        public static ArtifactCheckpoint AddJson<T>(T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var path = Guid.NewGuid().ToString("D");
            Values[path] = bytes;
            return new ArtifactCheckpoint(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public Task<ReadOnlyMemory<byte>?> ReadVerifiedAsync(ArtifactCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(Values.TryGetValue(checkpoint.Path, out var bytes) ? bytes : null);

        public Task<ArtifactCheckpoint> WriteRecordingArtifactAsync(Guid recordingId, string artifactName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(AddBytes(content));

        public Task<ArtifactCheckpoint> WriteClassArtifactAsync(Guid classId, string artifactName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(AddBytes(content));

        private static ArtifactCheckpoint AddBytes(ReadOnlyMemory<byte> content)
        {
            var bytes = content.ToArray();
            var path = Guid.NewGuid().ToString("D");
            Values[path] = bytes;
            return new ArtifactCheckpoint(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public Task<bool> VerifyAsync(string path, string sha256, long? expectedByteSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCheckpoint> WriteJobArtifactAsync(ProcessingRequest request, string artifactName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CleanupJobAsync(ProcessingRequest request, IReadOnlyCollection<string> publishedPaths, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
