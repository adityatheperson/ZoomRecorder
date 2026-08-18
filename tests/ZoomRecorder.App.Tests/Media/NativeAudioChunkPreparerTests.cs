using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.Media;

namespace ZoomRecorder.App.Tests.Media;

public sealed class NativeAudioChunkPreparerTests
{
    [Fact]
    public async Task Maps_native_metadata_and_roots_callback_state_until_synchronous_return()
    {
        using var files = new TestFiles();
        var first = files.CreateChunk("audio-0000.m4a", [1, 2, 3]);
        var second = files.CreateChunk("audio-0001.m4a", [4, 5, 6, 7]);
        var native = new FakeNativeAudioChunkApi
        {
            Behavior = call =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Emit(call, Metadata(0, first, 0, 10_000));
                Emit(call, Metadata(1, second, 5_000, 15_000));
                return ZrResult.Ok;
            }
        };

        var chunks = await new NativeAudioChunkPreparer(native).PrepareAsync(
            files.Mp4Path, files.JobDirectory, NativeAudioChunkPreparer.DefaultMaxBytes, CancellationToken.None);

        Assert.Collection(chunks,
            chunk =>
            {
                Assert.Equal(0, chunk.Index);
                Assert.Equal(first, chunk.Path);
                Assert.Equal(0, chunk.StartMilliseconds);
                Assert.Equal(10_000, chunk.EndMilliseconds);
                Assert.Equal(Hash(first), chunk.Sha256);
                Assert.Equal(3, chunk.ByteSize);
            },
            chunk =>
            {
                Assert.Equal(1, chunk.Index);
                Assert.Equal(second, chunk.Path);
                Assert.Equal(5_000, chunk.StartMilliseconds);
                Assert.Equal(15_000, chunk.EndMilliseconds);
                Assert.Equal(Hash(second), chunk.Sha256);
                Assert.Equal(4, chunk.ByteSize);
            });
        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal(NativeAudioChunkPreparer.DefaultMaxBytes, native.LastMaxBytes);
    }

    [Fact]
    public async Task Cancellation_reaches_the_published_request_handle_and_handle_is_destroyed()
    {
        using var files = new TestFiles();
        using var cancellation = new CancellationTokenSource();
        var native = new FakeNativeAudioChunkApi();
        var nativeCancelled = native.Cancelled;
        native.Behavior = _ =>
        {
            Assert.True(nativeCancelled.Wait(TimeSpan.FromSeconds(5)));
            return ZrResult.Cancelled;
        };

        var operation = new NativeAudioChunkPreparer(native).PrepareAsync(
            files.Mp4Path, files.JobDirectory, 100_000, cancellation.Token);
        Assert.True(native.Started.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal(new nint(0x1234), native.LastCancelledHandle);
    }

    [Theory]
    [InlineData(1, typeof(ArgumentException))]
    [InlineData(5, typeof(InvalidDataException))]
    [InlineData(6, typeof(InvalidDataException))]
    [InlineData(7, typeof(IOException))]
    [InlineData(2, typeof(InvalidOperationException))]
    [InlineData(3, typeof(InvalidOperationException))]
    public async Task Native_errors_map_to_stable_managed_exceptions(int resultCode, Type exceptionType)
    {
        using var files = new TestFiles();
        var result = (ZrResult)resultCode;
        var native = new FakeNativeAudioChunkApi { Behavior = _ => result };

        var exception = await Assert.ThrowsAsync(exceptionType, () => new NativeAudioChunkPreparer(native).PrepareAsync(
            files.Mp4Path, files.JobDirectory, 100_000, CancellationToken.None));

        Assert.Contains(result.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task Rejects_malformed_or_escaping_native_metadata_after_callback()
    {
        using var files = new TestFiles();
        var validPath = files.CreateChunk("audio.m4a", [8, 9, 10]);
        var outsidePath = Path.Combine(files.Root, "outside.m4a");
        File.WriteAllBytes(outsidePath, [11]);
        var cases = new[]
        {
            Metadata(1, validPath, 0, 1_000),
            Metadata(0, validPath, 1_000, 1_000),
            Metadata(0, validPath, 0, 1_000) with { Sha256 = new string('A', 64) },
            Metadata(0, validPath, 0, 1_000) with { ByteSize = 100_001 },
            Metadata(0, outsidePath, 0, 1_000),
            Metadata(0, validPath, 0, 1_000) with { Path = $"{files.JobDirectory}\\bad\0.m4a" },
            Metadata(0, validPath, 0, 1_000) with { NormalizedSampleRate = 48_000 },
            Metadata(0, validPath, 0, 1_000) with { EncodedSampleRate = 16_000 },
            Metadata(0, validPath, 0, 1_000) with { ChannelCount = 2 }
        };

        foreach (var malformed in cases)
        {
            var native = new FakeNativeAudioChunkApi
            {
                Behavior = call => { Emit(call, malformed); return ZrResult.Ok; }
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => new NativeAudioChunkPreparer(native).PrepareAsync(
                files.Mp4Path, files.JobDirectory, 100_000, CancellationToken.None));
            Assert.Equal(1, native.DestroyCalls);
        }

        var empty = new FakeNativeAudioChunkApi { Behavior = _ => ZrResult.Ok };
        await Assert.ThrowsAsync<InvalidDataException>(() => new NativeAudioChunkPreparer(empty).PrepareAsync(
            files.Mp4Path, files.JobDirectory, 100_000, CancellationToken.None));
    }

    [Fact]
    public async Task Destroy_cleans_only_the_native_invocations_partial_contract()
    {
        using var files = new TestFiles();
        var ownedPartial = Path.Combine(files.JobDirectory, "owned.partial");
        var unrelatedPartial = Path.Combine(files.JobDirectory, "unrelated.partial");
        File.WriteAllText(ownedPartial, "owned");
        File.WriteAllText(unrelatedPartial, "unrelated");
        var native = new FakeNativeAudioChunkApi
        {
            Behavior = _ => ZrResult.Cancelled,
            OnDestroy = _ => File.Delete(ownedPartial)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeAudioChunkPreparer(native).PrepareAsync(
            files.Mp4Path, files.JobDirectory, 100_000, CancellationToken.None));

        Assert.False(File.Exists(ownedPartial));
        Assert.True(File.Exists(unrelatedPartial));
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task Managed_arguments_are_rejected_before_calling_native_code()
    {
        using var files = new TestFiles();
        var native = new FakeNativeAudioChunkApi();
        var preparer = new NativeAudioChunkPreparer(native);

        await Assert.ThrowsAsync<FileNotFoundException>(() => preparer.PrepareAsync(
            Path.Combine(files.Root, "missing.mp4"), files.JobDirectory, 1, CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => preparer.PrepareAsync(
            files.Mp4Path, Path.Combine(files.Root, "missing"), 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preparer.PrepareAsync(
            files.Mp4Path, files.JobDirectory, 0, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparer.PrepareAsync(
            files.Mp4Path, files.JobDirectory, 1, cancelled.Token));

        Assert.Equal(0, native.PrepareCalls);
    }

    private static NativeChunkMetadata Metadata(int index, string path, long start, long end) => new(
        index,
        path,
        start,
        end,
        Hash(path),
        File.Exists(path) ? new FileInfo(path).Length : 0,
        16_000,
        48_000,
        1);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Emit(NativeCall call, NativeChunkMetadata metadata)
    {
        var path = Marshal.StringToCoTaskMemUni(metadata.Path);
        var hash = Marshal.StringToCoTaskMemUTF8(metadata.Sha256);
        var native = new NativeMethods.ZrAudioChunk
        {
            Index = checked((uint)metadata.Index),
            Path = path,
            StartMilliseconds = metadata.StartMilliseconds,
            EndMilliseconds = metadata.EndMilliseconds,
            Sha256 = hash,
            ByteSize = checked((ulong)metadata.ByteSize),
            NormalizedSampleRate = metadata.NormalizedSampleRate,
            EncodedSampleRate = metadata.EncodedSampleRate,
            ChannelCount = metadata.ChannelCount
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ZrAudioChunk>());
        try
        {
            Marshal.StructureToPtr(native, pointer, false);
            call.Callback(pointer, call.Context);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeCoTaskMem(hash);
            Marshal.FreeCoTaskMem(path);
        }
    }

    private sealed class FakeNativeAudioChunkApi : IAudioChunkNativeApi
    {
        internal Func<NativeCall, ZrResult>? Behavior { get; set; }
        internal Action<nint>? OnDestroy { get; set; }
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Cancelled { get; } = new();
        internal int PrepareCalls { get; private set; }
        internal int CancelCalls { get; private set; }
        internal int DestroyCalls { get; private set; }
        internal long LastMaxBytes { get; private set; }
        internal nint LastCancelledHandle { get; private set; }

        public ZrResult Prepare(string mp4Path, string outputDirectory, ulong maxBytes,
            NativeMethods.ChunkCallback callback, nint context, NativePreparationHandleSlot handleSlot)
        {
            PrepareCalls++;
            LastMaxBytes = checked((long)maxBytes);
            handleSlot.Publish(new nint(0x1234));
            Started.Set();
            return Behavior?.Invoke(new NativeCall(callback, context)) ?? ZrResult.Ok;
        }

        public ZrResult Cancel(nint handle)
        {
            CancelCalls++;
            LastCancelledHandle = handle;
            Cancelled.Set();
            return ZrResult.Ok;
        }

        public ZrResult Destroy(nint handle)
        {
            DestroyCalls++;
            OnDestroy?.Invoke(handle);
            return ZrResult.Ok;
        }
    }

    private sealed class TestFiles : IDisposable
    {
        internal TestFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zoom-recorder-managed-chunks-{Guid.NewGuid():N}");
            JobDirectory = Path.Combine(Root, "job");
            Directory.CreateDirectory(JobDirectory);
            Mp4Path = Path.Combine(Root, "lecture.mp4");
            File.WriteAllBytes(Mp4Path, [0]);
        }

        internal string Root { get; }
        internal string JobDirectory { get; }
        internal string Mp4Path { get; }
        internal string CreateChunk(string name, byte[] content)
        {
            var path = Path.Combine(JobDirectory, name);
            File.WriteAllBytes(path, content);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed record NativeCall(NativeMethods.ChunkCallback Callback, nint Context);
    private sealed record NativeChunkMetadata(
        int Index,
        string Path,
        long StartMilliseconds,
        long EndMilliseconds,
        string Sha256,
        long ByteSize,
        uint NormalizedSampleRate,
        uint EncodedSampleRate,
        uint ChannelCount);
}
