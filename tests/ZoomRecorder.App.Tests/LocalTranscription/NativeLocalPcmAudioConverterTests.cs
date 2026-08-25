using System.Diagnostics;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.LocalTranscription;

public sealed class NativeLocalPcmAudioConverterTests
{
    [Fact]
    public async Task Converts_an_absolute_checkpoint_to_a_unique_valid_wav_inside_the_job_directory()
    {
        using var files = new TestFiles();
        var native = new FakeNativePcmApi
        {
            Behavior = call =>
            {
                WritePcmWav(call.WavPath);
                return ZrResult.Ok;
            }
        };

        var result = await new NativeLocalPcmAudioConverter(native).ConvertAsync(
            files.Chunk, files.JobDirectory, CancellationToken.None);

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.Equal(files.JobDirectory, Path.GetDirectoryName(result), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(".wav", Path.GetExtension(result), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(Path.ChangeExtension(files.Chunk.Path, ".wav"), result);
        Assert.Equal(files.Chunk.Path, native.LastM4aPath);
        Assert.Equal(result, native.LastWavPath);
        Assert.True(File.Exists(result));
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task Cancellation_reaches_the_published_request_handle_and_handle_is_destroyed()
    {
        using var files = new TestFiles();
        using var cancellation = new CancellationTokenSource();
        var native = new FakeNativePcmApi();
        var nativeCancelled = native.Cancelled;
        native.Behavior = _ =>
        {
            Assert.True(nativeCancelled.Wait(TimeSpan.FromSeconds(5)));
            return ZrResult.Cancelled;
        };

        var operation = new NativeLocalPcmAudioConverter(native).ConvertAsync(
            files.Chunk, files.JobDirectory, cancellation.Token);
        Assert.True(native.Started.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal(new nint(0x5678), native.LastCancelledHandle);
        Assert.False(File.Exists(native.LastWavPath));
        Assert.False(File.Exists(native.LastWavPath + ".partial"));
    }

    [Theory]
    [InlineData(1, typeof(ArgumentException))]
    [InlineData(5, typeof(InvalidDataException))]
    [InlineData(6, typeof(InvalidDataException))]
    [InlineData(7, typeof(IOException))]
    [InlineData(2, typeof(InvalidOperationException))]
    [InlineData(3, typeof(InvalidOperationException))]
    public async Task Native_errors_map_to_stable_managed_exceptions_without_deleting_collision_files(
        int resultCode,
        Type exceptionType)
    {
        using var files = new TestFiles();
        var result = (ZrResult)resultCode;
        var native = new FakeNativePcmApi
        {
            Behavior = call =>
            {
                File.WriteAllText(call.WavPath, "final");
                File.WriteAllText(call.WavPath + ".partial", "partial");
                return result;
            }
        };

        var exception = await Assert.ThrowsAsync(exceptionType, () => new NativeLocalPcmAudioConverter(native).ConvertAsync(
            files.Chunk, files.JobDirectory, CancellationToken.None));

        Assert.Contains(result.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal("final", File.ReadAllText(native.LastWavPath));
        Assert.Equal("partial", File.ReadAllText(native.LastWavPath + ".partial"));
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task Managed_native_exception_still_destroys_the_handle_without_deleting_unowned_outputs()
    {
        using var files = new TestFiles();
        var native = new FakeNativePcmApi
        {
            Behavior = call =>
            {
                File.WriteAllText(call.WavPath, "final");
                File.WriteAllText(call.WavPath + ".partial", "partial");
                throw new InvalidOperationException("native adapter failure");
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeLocalPcmAudioConverter(native).ConvertAsync(
                files.Chunk, files.JobDirectory, CancellationToken.None));

        Assert.Equal("native adapter failure", exception.Message);
        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal("final", File.ReadAllText(native.LastWavPath));
        Assert.Equal("partial", File.ReadAllText(native.LastWavPath + ".partial"));
    }

    [Fact]
    public async Task Invalid_successful_result_removes_only_the_owned_final_and_preserves_a_foreign_partial()
    {
        using var files = new TestFiles();
        var native = new FakeNativePcmApi
        {
            Behavior = call =>
            {
                File.WriteAllBytes(call.WavPath, new byte[43]);
                File.WriteAllText(call.WavPath + ".partial", "foreign partial");
                return ZrResult.Ok;
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => new NativeLocalPcmAudioConverter(native).ConvertAsync(
            files.Chunk, files.JobDirectory, CancellationToken.None));

        Assert.False(File.Exists(native.LastWavPath));
        Assert.Equal("foreign partial", File.ReadAllText(native.LastWavPath + ".partial"));
    }

    [Fact]
    public async Task Rejects_malformed_wav_headers_and_removes_the_native_result()
    {
        using var files = new TestFiles();
        var mutations = new Action<byte[]>[]
        {
            bytes => bytes[20] = 3,
            bytes => bytes[22] = 2,
            bytes => BitConverter.GetBytes(48_000).CopyTo(bytes, 24),
            bytes => bytes[34] = 32,
            bytes => BitConverter.GetBytes(100).CopyTo(bytes, 40)
        };

        foreach (var mutate in mutations)
        {
            var native = new FakeNativePcmApi
            {
                Behavior = call =>
                {
                    WritePcmWav(call.WavPath, mutate);
                    return ZrResult.Ok;
                }
            };

            await Assert.ThrowsAsync<InvalidDataException>(() => new NativeLocalPcmAudioConverter(native).ConvertAsync(
                files.Chunk, files.JobDirectory, CancellationToken.None));
            Assert.False(File.Exists(native.LastWavPath));
            Assert.Equal(1, native.DestroyCalls);
        }

        var truncated = new FakeNativePcmApi
        {
            Behavior = call =>
            {
                File.WriteAllBytes(call.WavPath, new byte[43]);
                return ZrResult.Ok;
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new NativeLocalPcmAudioConverter(truncated).ConvertAsync(
            files.Chunk, files.JobDirectory, CancellationToken.None));
        Assert.False(File.Exists(truncated.LastWavPath));
    }

    [Fact]
    public async Task Managed_checkpoint_and_directory_validation_runs_before_native_code()
    {
        using var files = new TestFiles();
        var native = new FakeNativePcmApi();
        var converter = new NativeLocalPcmAudioConverter(native);
        var outside = Path.Combine(files.Root, "outside.m4a");
        File.WriteAllBytes(outside, [1]);
        var wrongExtension = Path.Combine(files.JobDirectory, "audio.mp3");
        File.WriteAllBytes(wrongExtension, [1]);
        var missing = Path.Combine(files.JobDirectory, "missing.m4a");

        await Assert.ThrowsAsync<InvalidDataException>(() => converter.ConvertAsync(
            files.Chunk with { Path = "relative.m4a" }, files.JobDirectory, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => converter.ConvertAsync(
            files.Chunk with { Path = outside }, files.JobDirectory, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => converter.ConvertAsync(
            files.Chunk with { Path = wrongExtension }, files.JobDirectory, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => converter.ConvertAsync(
            files.Chunk with { Path = missing }, files.JobDirectory, CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => converter.ConvertAsync(
            files.Chunk, Path.Combine(files.Root, "missing-job"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync(
            files.Chunk, "relative-job", CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(
            files.Chunk, files.JobDirectory, cancelled.Token));

        Assert.Equal(0, native.ConvertCalls);
    }

    [Fact]
    public async Task Reparse_point_job_directory_and_m4a_leaf_are_rejected_before_native_code()
    {
        using var files = new TestFiles();
        var native = new FakeNativePcmApi();
        var converter = new NativeLocalPcmAudioConverter(native);
        var linkedCheckpoint = Path.Combine(files.JobDirectory, "linked.m4a");
        var linkedCheckpointTarget = Path.Combine(files.Root, "linked-checkpoint-target");
        Directory.CreateDirectory(linkedCheckpointTarget);
        CreateJunction(linkedCheckpoint, linkedCheckpointTarget);
        var linkedJob = Path.Combine(files.Root, "linked-job");
        CreateJunction(linkedJob, files.JobDirectory);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => converter.ConvertAsync(
                files.Chunk with { Path = linkedCheckpoint }, files.JobDirectory, CancellationToken.None));

            var sourceThroughLinkedJob = Path.Combine(linkedJob, Path.GetFileName(files.Chunk.Path));
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync(
                files.Chunk with { Path = sourceThroughLinkedJob }, linkedJob, CancellationToken.None));

            Assert.Equal(0, native.ConvertCalls);
        }
        finally
        {
            Directory.Delete(linkedCheckpoint);
            Directory.Delete(linkedJob);
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static void WritePcmWav(string path, Action<byte[]>? mutate = null)
    {
        byte[] data = [1, 0, 2, 0];
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BitConverter.GetBytes(36 + data.Length).CopyTo(header, 4);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(header, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 22);
        BitConverter.GetBytes(16_000).CopyTo(header, 24);
        BitConverter.GetBytes(32_000).CopyTo(header, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(header, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(header, 34);
        "data"u8.CopyTo(header.AsSpan(36));
        BitConverter.GetBytes(data.Length).CopyTo(header, 40);
        mutate?.Invoke(header);
        using var output = File.Create(path);
        output.Write(header);
        output.Write(data);
    }

    private sealed class FakeNativePcmApi : IPcmWavNativeApi
    {
        internal Func<NativeCall, ZrResult>? Behavior { get; set; }
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Cancelled { get; } = new();
        internal int ConvertCalls { get; private set; }
        internal int CancelCalls { get; private set; }
        internal int DestroyCalls { get; private set; }
        internal string LastM4aPath { get; private set; } = string.Empty;
        internal string LastWavPath { get; private set; } = string.Empty;
        internal nint LastCancelledHandle { get; private set; }

        public ZrResult Convert(string m4aPath, string wavPath, NativePreparationHandleSlot handleSlot)
        {
            ConvertCalls++;
            LastM4aPath = m4aPath;
            LastWavPath = wavPath;
            handleSlot.Publish(new nint(0x5678));
            Started.Set();
            return Behavior?.Invoke(new NativeCall(m4aPath, wavPath)) ?? ZrResult.Ok;
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
            return ZrResult.Ok;
        }
    }

    private sealed class TestFiles : IDisposable
    {
        internal TestFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zoom-recorder-managed-pcm-{Guid.NewGuid():N}");
            JobDirectory = Path.Combine(Root, "job");
            Directory.CreateDirectory(JobDirectory);
            var source = Path.Combine(JobDirectory, "audio-0000.m4a");
            File.WriteAllBytes(source, [1, 2, 3]);
            Chunk = new AudioChunk(0, source, 0, 1_000, new string('a', 64), 3);
        }

        internal string Root { get; }
        internal string JobDirectory { get; }
        internal AudioChunk Chunk { get; }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed record NativeCall(string M4aPath, string WavPath);
}
