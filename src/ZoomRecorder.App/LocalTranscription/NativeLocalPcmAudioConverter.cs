using System.Buffers.Binary;
using ZoomRecorder.App.Interop;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.LocalTranscription;

internal interface IPcmWavNativeApi
{
    ZrResult Convert(string m4aPath, string wavPath, NativePreparationHandleSlot handleSlot);
    ZrResult Cancel(nint handle);
    ZrResult Destroy(nint handle);
}

internal sealed class PcmWavNativeApi : IPcmWavNativeApi
{
    public unsafe ZrResult Convert(string m4aPath, string wavPath, NativePreparationHandleSlot handleSlot) =>
        NativeMethods.zr_convert_audio_to_pcm_wav(m4aPath, wavPath, (nint*)handleSlot.Storage);

    public ZrResult Cancel(nint handle) => NativeMethods.zr_cancel_pcm_conversion(handle);

    public ZrResult Destroy(nint handle) => NativeMethods.zr_destroy_pcm_conversion(handle);
}

internal sealed class NativeLocalPcmAudioConverter : ILocalPcmAudioConverter
{
    private const ushort PcmFormat = 1;
    private const ushort ChannelCount = 1;
    private const uint SampleRate = 16_000;
    private const ushort BitsPerSample = 16;
    private const ushort BlockAlignment = ChannelCount * BitsPerSample / 8;
    private const uint BytesPerSecond = SampleRate * BlockAlignment;
    private const int HeaderLength = 44;
    private readonly IPcmWavNativeApi native;

    internal NativeLocalPcmAudioConverter() : this(new PcmWavNativeApi()) { }

    internal NativeLocalPcmAudioConverter(IPcmWavNativeApi native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    public async Task<string> ConvertAsync(
        AudioChunk chunk,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        if (!Path.IsPathFullyQualified(jobDirectory))
        {
            throw new ArgumentException("The local transcription job directory must be absolute.", nameof(jobDirectory));
        }

        var canonicalJobDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        if (!Directory.Exists(canonicalJobDirectory))
        {
            throw new DirectoryNotFoundException($"The local transcription job directory does not exist: {canonicalJobDirectory}");
        }
        if (string.IsNullOrWhiteSpace(chunk.Path) || !Path.IsPathFullyQualified(chunk.Path))
        {
            throw InvalidSource("the M4A checkpoint path is missing or not absolute");
        }

        var sourcePath = Path.GetFullPath(chunk.Path);
        if (!string.Equals(Path.GetDirectoryName(sourcePath), canonicalJobDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(sourcePath), ".m4a", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidSource("the source escapes the job directory or is not an M4A checkpoint");
        }
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The M4A checkpoint does not exist.", sourcePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var wavPath = CreateOutputPath(canonicalJobDirectory, chunk.Index);
        using var handleSlot = new NativePreparationHandleSlot();
        var cancellation = new NativeCancellation(native, handleSlot);
        CancellationTokenRegistration cancellationRegistration = default;
        ZrResult result = ZrResult.InternalError;
        ZrResult destroyResult = ZrResult.Ok;
        var succeeded = false;
        try
        {
            try
            {
                var nativeTask = Task.Run(() =>
                {
                    try
                    {
                        return native.Convert(sourcePath, wavPath, handleSlot);
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
                cancellationRegistration.Dispose();
                var handle = handleSlot.Handle;
                if (handle != nint.Zero)
                {
                    destroyResult = native.Destroy(handle);
                }
            }

            ThrowForNativeResult(result, cancellationToken);
            if (destroyResult != ZrResult.Ok)
            {
                throw new InvalidOperationException($"Native PCM conversion failed while destroying its handle: {destroyResult}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOutput(wavPath, canonicalJobDirectory);
            succeeded = true;
            return wavPath;
        }
        finally
        {
            if (!succeeded)
            {
                DeleteTransient(wavPath + ".partial");
                DeleteTransient(wavPath);
            }
        }
    }

    private static string CreateOutputPath(string jobDirectory, int chunkIndex)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                jobDirectory,
                $"local-audio-{chunkIndex:D4}-{Guid.NewGuid():N}.wav"));
            if (!Path.IsPathFullyQualified(candidate) ||
                !string.Equals(Path.GetDirectoryName(candidate), jobDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The transient WAV path escapes the local transcription job directory.");
            }
            if (!File.Exists(candidate) && !File.Exists(candidate + ".partial"))
            {
                return candidate;
            }
        }

        throw new IOException("A unique transient WAV path could not be allocated.");
    }

    private static void ValidateOutput(string wavPath, string jobDirectory)
    {
        if (!Path.IsPathFullyQualified(wavPath) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(wavPath)), jobDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(wavPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native PCM conversion returned an invalid WAV path.");
        }
        if (!File.Exists(wavPath))
        {
            throw new InvalidDataException("Native PCM conversion did not publish a WAV file.");
        }

        using var input = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < HeaderLength || input.Length > uint.MaxValue + 8L)
        {
            throw InvalidWav();
        }
        Span<byte> header = stackalloc byte[HeaderLength];
        input.ReadExactly(header);
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
        if (!header[..4].SequenceEqual("RIFF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != input.Length - 8 ||
            !header[8..12].SequenceEqual("WAVE"u8) ||
            !header[12..16].SequenceEqual("fmt "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]) != PcmFormat ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[22..24]) != ChannelCount ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) != SampleRate ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]) != BytesPerSecond ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]) != BlockAlignment ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]) != BitsPerSample ||
            !header[36..40].SequenceEqual("data"u8) ||
            dataLength == 0 || dataLength % BlockAlignment != 0 || dataLength != input.Length - HeaderLength)
        {
            throw InvalidWav();
        }
    }

    private static InvalidDataException InvalidSource(string reason) =>
        new($"The audio chunk is not a valid local M4A checkpoint: {reason}.");

    private static InvalidDataException InvalidWav() =>
        new("Native PCM conversion returned an invalid mono 16 kHz 16-bit PCM WAV file.");

    private static void DeleteTransient(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the conversion failure; recovery cleanup retries this exact transient path.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the conversion failure; recovery cleanup retries this exact transient path.
        }
    }

    private static void ThrowForNativeResult(ZrResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case ZrResult.Ok:
                return;
            case ZrResult.Cancelled:
                throw new OperationCanceledException($"Native PCM conversion failed: {result}.", null, cancellationToken);
            case ZrResult.InvalidArgument:
                throw new ArgumentException($"Native PCM conversion failed: {result}.");
            case ZrResult.AudioStreamMissing:
                throw new InvalidDataException($"Native PCM conversion failed: {result}. The M4A has no audio stream.");
            case ZrResult.MediaError:
                throw new InvalidDataException($"Native PCM conversion failed: {result}. The M4A could not be decoded.");
            case ZrResult.IoError:
                throw new IOException($"Native PCM conversion failed: {result}. The WAV could not be published.");
            case ZrResult.InvalidState:
            case ZrResult.InternalError:
            default:
                throw new InvalidOperationException($"Native PCM conversion failed: {result}.");
        }
    }

    private sealed class NativeCancellation(IPcmWavNativeApi native, NativePreparationHandleSlot handleSlot)
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
}
