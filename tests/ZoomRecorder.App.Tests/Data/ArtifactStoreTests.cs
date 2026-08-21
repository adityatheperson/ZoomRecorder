using System.Text;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.Data;

public sealed class ArtifactStoreTests
{
    [Fact]
    public async Task Write_is_atomic_and_replaces_existing_content()
    {
        using var temp = new TestDirectory();
        var ownerId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var store = new ArtifactStore(temp.Path);

        var destination = await store.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("first"), default);
        var replacedDestination = await store.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("second"), default);

        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, ownerId.ToString("D"), "lecture.json")), destination);
        Assert.Equal(destination, replacedDestination);
        Assert.Equal("second", await File.ReadAllTextAsync(destination));
        Assert.Equal(new[] { "lecture.json" }, Directory.GetFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../lecture.json")]
    [InlineData("..\\lecture.json")]
    [InlineData("folder/lecture.json")]
    [InlineData("folder\\lecture.json")]
    [InlineData("bad<name.json")]
    public async Task Invalid_or_non_leaf_names_are_rejected(string artifactName)
    {
        using var temp = new TestDirectory();
        var store = new ArtifactStore(temp.Path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.WriteAtomicallyAsync(Guid.NewGuid(), artifactName, Bytes("content"), default));
    }

    [Fact]
    public async Task Rooted_names_and_empty_content_are_rejected()
    {
        using var temp = new TestDirectory();
        var store = new ArtifactStore(temp.Path);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAtomicallyAsync(Guid.NewGuid(), Path.GetFullPath(temp.File("outside.json")), Bytes("content"), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAtomicallyAsync(Guid.NewGuid(), "empty.json", ReadOnlyMemory<byte>.Empty, default));
    }

    [Fact]
    public async Task Injected_failure_preserves_existing_artifact_and_removes_temp_file()
    {
        using var temp = new TestDirectory();
        var ownerId = Guid.NewGuid();
        var workingStore = new ArtifactStore(temp.Path);
        var destination = await workingStore.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("original"), default);
        var failingStore = new ArtifactStore(
            temp.Path,
            _ => ValueTask.FromException(new IOException("Injected before commit.")));

        await Assert.ThrowsAsync<IOException>(() =>
            failingStore.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("replacement"), default));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Equal(new[] { "lecture.json" }, Directory.GetFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancellation_preserves_existing_artifact_and_removes_temp_file()
    {
        using var temp = new TestDirectory();
        var ownerId = Guid.NewGuid();
        var workingStore = new ArtifactStore(temp.Path);
        var destination = await workingStore.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("original"), default);
        using var cancellation = new CancellationTokenSource();
        var cancellingStore = new ArtifactStore(temp.Path, _ =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellingStore.WriteAtomicallyAsync(ownerId, "lecture.json", Bytes("replacement"), cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Equal(new[] { "lecture.json" }, Directory.GetFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Processing_port_writes_and_verifies_job_recording_and_class_artifacts()
    {
        using var temp = new TestDirectory();
        var request = new ProcessingRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            temp.File("lecture.mp4"),
            temp.File("registered-job"),
            false);
        IProcessingArtifactStore store = new ArtifactStore(temp.File("artifacts"));

        var job = await store.WriteJobArtifactAsync(request, "chunk-result.json", Bytes("job"), default);
        var recording = await store.WriteRecordingArtifactAsync(
            request.RecordingId, "transcript.json", Bytes("recording"), default);
        var @class = await store.WriteClassArtifactAsync(
            request.ClassId, "guide.json", Bytes("class"), default);

        Assert.Equal(Path.GetFullPath(Path.Combine(request.JobDirectory, "chunk-result.json")), job.Path);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.File("artifacts"), request.RecordingId.ToString("D"), "transcript.json")),
            recording.Path);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.File("artifacts"), request.ClassId.ToString("D"), "guide.json")),
            @class.Path);
        Assert.True(await store.VerifyAsync(job.Path, job.Sha256, expectedByteSize: 3, default));
        Assert.Equal("recording", Encoding.UTF8.GetString((await store.ReadVerifiedAsync(recording, default))!.Value.Span));

        await File.WriteAllTextAsync(@class.Path, "corrupt");
        Assert.False(await store.VerifyAsync(@class.Path, @class.Sha256, expectedByteSize: null, default));
        Assert.Null(await store.ReadVerifiedAsync(@class, default));
    }

    [Fact]
    public async Task Processing_cleanup_removes_only_unpublished_spool_files_in_the_registered_job_directory()
    {
        using var temp = new TestDirectory();
        var jobDirectory = temp.File("registered-job");
        var siblingDirectory = temp.File("other-job");
        Directory.CreateDirectory(jobDirectory);
        Directory.CreateDirectory(siblingDirectory);
        Directory.CreateDirectory(Path.Combine(jobDirectory, "nested"));
        var request = new ProcessingRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), temp.File("lecture.mp4"), jobDirectory, false);
        var publishedAudio = Write(jobDirectory, "chunk-0.m4a");
        var publishedResult = Write(jobDirectory, "transcript-chunk-000000.json");
        var unpublishedAudio = Write(jobDirectory, "chunk-1.m4a");
        var unpublishedResult = Write(jobDirectory, "transcript-chunk-000001.json");
        var partial = Write(jobDirectory, "upload.partial");
        var temporary = Write(jobDirectory, ".artifact.tmp");
        var unrelated = Write(jobDirectory, "notes.txt");
        var nested = Write(Path.Combine(jobDirectory, "nested"), "nested.partial");
        var sibling = Write(siblingDirectory, "sibling.partial");
        await File.WriteAllBytesAsync(request.Mp4Path, [1, 2, 3]);
        IProcessingArtifactStore store = new ArtifactStore(temp.File("artifacts"));

        await store.CleanupJobAsync(request, [publishedAudio, publishedResult], default);

        Assert.True(File.Exists(publishedAudio));
        Assert.True(File.Exists(publishedResult));
        Assert.False(File.Exists(unpublishedAudio));
        Assert.False(File.Exists(unpublishedResult));
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(request.Mp4Path));
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Write(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "content");
        return Path.GetFullPath(path);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
