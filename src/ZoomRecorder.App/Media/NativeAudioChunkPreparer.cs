using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ZoomRecorder.App.Interop;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Media;

internal sealed class NativeAudioChunkPreparer : IAudioChunkPreparer
{
    internal const long DefaultMaxBytes = 24L * 1024L * 1024L;
    private const uint NormalizedSampleRate = 16_000;
    private const uint EncodedSampleRate = 48_000;
    private const uint ChannelCount = 1;
    private const long OverlapMilliseconds = 5_000;
    private static readonly NativeMethods.ChunkCallback ChunkCallback = OnChunk;
    private readonly IAudioChunkNativeApi native;

    internal NativeAudioChunkPreparer() : this(new AudioChunkNativeApi()) { }

    internal NativeAudioChunkPreparer(IAudioChunkNativeApi native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    public async Task<IReadOnlyList<AudioChunk>> PrepareAsync(
        string mp4Path,
        string jobDirectory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mp4Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The chunk size limit must be positive.");
        }

        var canonicalMp4 = Path.GetFullPath(mp4Path);
        if (!File.Exists(canonicalMp4))
        {
            throw new FileNotFoundException("The finalized MP4 does not exist.", canonicalMp4);
        }

        var canonicalJobDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        if (!Directory.Exists(canonicalJobDirectory))
        {
            throw new DirectoryNotFoundException($"The audio job directory does not exist: {canonicalJobDirectory}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var callbackState = new CallbackState();
        var callbackHandle = GCHandle.Alloc(callbackState);
        using var handleSlot = new NativePreparationHandleSlot();
        var cancellation = new NativeCancellation(native, handleSlot);
        CancellationTokenRegistration cancellationRegistration = default;
        ZrResult result;
        ZrResult destroyResult = ZrResult.Ok;
        try
        {
            var nativeTask = Task.Run(() =>
            {
                try
                {
                    return native.Prepare(
                        canonicalMp4,
                        canonicalJobDirectory,
                        checked((ulong)maxBytes),
                        ChunkCallback,
                        GCHandle.ToIntPtr(callbackHandle),
                        handleSlot);
                }
                finally
                {
                    cancellation.MarkFinished();
                }
            });
            cancellationRegistration = cancellationToken.Register(
                static state => ((NativeCancellation)state!).Request(), cancellation);
            result = await nativeTask;
        }
        finally
        {
            try
            {
                cancellationRegistration.Dispose();
                var handle = handleSlot.Handle;
                if (handle != nint.Zero)
                {
                    destroyResult = native.Destroy(handle);
                }
            }
            finally
            {
                callbackHandle.Free();
            }
        }

        ThrowForNativeResult(result, cancellationToken);
        if (destroyResult != ZrResult.Ok)
        {
            throw new InvalidOperationException($"Native audio preparation failed while destroying its handle: {destroyResult}.");
        }

        return ValidateMetadata(callbackState, canonicalJobDirectory, maxBytes);
    }

    private static void OnChunk(nint chunk, nint context)
    {
        try
        {
            if (chunk == nint.Zero || context == nint.Zero)
            {
                throw new InvalidDataException("Native audio preparation returned an empty callback value.");
            }

            var state = (CallbackState?)GCHandle.FromIntPtr(context).Target
                ?? throw new InvalidDataException("Native audio preparation callback state is unavailable.");
            var nativeChunk = Marshal.PtrToStructure<NativeMethods.ZrAudioChunk>(chunk);
            var metadata = new NativeChunkMetadata(
                nativeChunk.Index,
                Marshal.PtrToStringUni(nativeChunk.Path),
                nativeChunk.StartMilliseconds,
                nativeChunk.EndMilliseconds,
                Marshal.PtrToStringUTF8(nativeChunk.Sha256),
                nativeChunk.ByteSize,
                nativeChunk.NormalizedSampleRate,
                nativeChunk.EncodedSampleRate,
                nativeChunk.ChannelCount);
            lock (state.Gate)
            {
                state.Chunks.Add(metadata);
            }
        }
        catch (Exception exception)
        {
            try
            {
                var state = context == nint.Zero ? null : (CallbackState?)GCHandle.FromIntPtr(context).Target;
                if (state is not null)
                {
                    lock (state.Gate)
                    {
                        state.CallbackFailure ??= exception;
                    }
                }
            }
            catch (Exception)
            {
                // Never let managed exceptions cross the native callback boundary.
            }
        }
    }

    private static IReadOnlyList<AudioChunk> ValidateMetadata(
        CallbackState state,
        string jobDirectory,
        long maxBytes)
    {
        NativeChunkMetadata[] metadata;
        Exception? callbackFailure;
        lock (state.Gate)
        {
            metadata = [.. state.Chunks];
            callbackFailure = state.CallbackFailure;
        }

        if (callbackFailure is not null)
        {
            throw new InvalidDataException("Native audio preparation returned malformed callback data.", callbackFailure);
        }
        if (metadata.Length == 0)
        {
            throw new InvalidDataException("Native audio preparation returned no audio chunks.");
        }

        var result = new List<AudioChunk>(metadata.Length);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var expectedIndex = 0; expectedIndex < metadata.Length; expectedIndex++)
        {
            var chunk = metadata[expectedIndex];
            if (chunk.Index != (uint)expectedIndex)
            {
                throw InvalidMetadata("chunk indexes are not ordered and contiguous");
            }
            if (string.IsNullOrWhiteSpace(chunk.Path) || !Path.IsPathFullyQualified(chunk.Path))
            {
                throw InvalidMetadata("a chunk path is missing or not absolute");
            }

            var path = Path.GetFullPath(chunk.Path);
            if (!string.Equals(Path.GetDirectoryName(path), jobDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(path), ".m4a", StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(path))
            {
                throw InvalidMetadata("a chunk path escapes the job directory, is duplicated, or is not an M4A file");
            }
            if (chunk.StartMilliseconds < 0 || chunk.EndMilliseconds <= chunk.StartMilliseconds)
            {
                throw InvalidMetadata("a chunk has an invalid absolute time range");
            }
            if (expectedIndex > 0 &&
                (chunk.StartMilliseconds <= metadata[expectedIndex - 1].StartMilliseconds ||
                 chunk.EndMilliseconds <= metadata[expectedIndex - 1].EndMilliseconds))
            {
                throw InvalidMetadata("chunk start and end times are not strictly increasing");
            }
            if (expectedIndex > 0 && metadata[expectedIndex - 1].EndMilliseconds - chunk.StartMilliseconds != OverlapMilliseconds)
            {
                throw InvalidMetadata("adjacent chunks do not overlap by exactly five seconds");
            }
            if (chunk.ByteSize == 0 || chunk.ByteSize > (ulong)maxBytes || chunk.ByteSize > long.MaxValue)
            {
                throw InvalidMetadata("a chunk has an invalid or oversized byte count");
            }
            if (chunk.NormalizedSampleRate != NormalizedSampleRate || chunk.EncodedSampleRate != EncodedSampleRate ||
                chunk.ChannelCount != ChannelCount)
            {
                throw InvalidMetadata("a chunk has an unexpected audio format");
            }
            if (chunk.Sha256 is not { } sha256 || !IsLowercaseSha256(sha256))
            {
                throw InvalidMetadata("a chunk has an invalid SHA-256 value");
            }
            if (!File.Exists(path))
            {
                throw InvalidMetadata("a published chunk file does not exist");
            }

            var file = new FileInfo(path);
            if ((ulong)file.Length != chunk.ByteSize || !string.Equals(Hash(path), sha256, StringComparison.Ordinal))
            {
                throw InvalidMetadata("a chunk file does not match its native size or SHA-256 metadata");
            }

            result.Add(new AudioChunk(
                expectedIndex,
                path,
                chunk.StartMilliseconds,
                chunk.EndMilliseconds,
                sha256,
                file.Length));
        }

        return result;
    }

    private static InvalidDataException InvalidMetadata(string reason) =>
        new($"Native audio preparation returned invalid metadata: {reason}.");

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ThrowForNativeResult(ZrResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case ZrResult.Ok:
                return;
            case ZrResult.Cancelled:
                throw new OperationCanceledException($"Native audio preparation failed: {result}.", null, cancellationToken);
            case ZrResult.InvalidArgument:
                throw new ArgumentException($"Native audio preparation failed: {result}.");
            case ZrResult.AudioStreamMissing:
                throw new InvalidDataException($"Native audio preparation failed: {result}. The MP4 has no audio stream.");
            case ZrResult.MediaError:
                throw new InvalidDataException($"Native audio preparation failed: {result}. The MP4 could not be decoded or encoded.");
            case ZrResult.IoError:
                throw new IOException($"Native audio preparation failed: {result}. A chunk file could not be published.");
            case ZrResult.InvalidState:
            case ZrResult.InternalError:
            default:
                throw new InvalidOperationException($"Native audio preparation failed: {result}.");
        }
    }

    private sealed class CallbackState
    {
        internal object Gate { get; } = new();
        internal List<NativeChunkMetadata> Chunks { get; } = [];
        internal Exception? CallbackFailure { get; set; }
    }

    private sealed class NativeCancellation(IAudioChunkNativeApi native, NativePreparationHandleSlot handleSlot)
    {
        private int requested;
        private int finished;

        internal void Request()
        {
            if (Interlocked.Exchange(ref requested, 1) != 0)
            {
                return;
            }

            var spinner = new SpinWait();
            while (Volatile.Read(ref finished) == 0)
            {
                var handle = handleSlot.Handle;
                if (handle != nint.Zero)
                {
                    native.Cancel(handle);
                    return;
                }
                spinner.SpinOnce();
            }
        }

        internal void MarkFinished() => Volatile.Write(ref finished, 1);
    }

    private sealed record NativeChunkMetadata(
        uint Index,
        string? Path,
        long StartMilliseconds,
        long EndMilliseconds,
        string? Sha256,
        ulong ByteSize,
        uint NormalizedSampleRate,
        uint EncodedSampleRate,
        uint ChannelCount);
}
