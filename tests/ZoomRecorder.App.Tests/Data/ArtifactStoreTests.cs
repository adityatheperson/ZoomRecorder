using System.Text;
using ZoomRecorder.App.Data;

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

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

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
