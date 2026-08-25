using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed class LocalWhisperTranscriptionClient(
    IWhisperModelManager modelManager,
    ILocalPcmAudioConverter converter,
    IWhisperWorkerRunner workerRunner) : ITranscriptionClient
{
    private readonly IWhisperModelManager modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    private readonly ILocalPcmAudioConverter converter = converter ?? throw new ArgumentNullException(nameof(converter));
    private readonly IWhisperWorkerRunner workerRunner = workerRunner ?? throw new ArgumentNullException(nameof(workerRunner));

    public async Task<TranscriptChunk> TranscribeAsync(
        AudioChunk chunk,
        IProgress<TranscriptionActivity>? progress,
        CancellationToken cancellationToken)
    {
        var jobDirectory = Validate(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new TranscriptionActivity(TranscriptionActivityKind.AcquiringModel));

        string modelPath;
        try
        {
            var modelProgress = progress is null
                ? null
                : new CallbackProgress<ModelDownloadProgress>(item => progress.Report(
                    new TranscriptionActivity(
                        TranscriptionActivityKind.AcquiringModel,
                        item.CompletedBytes,
                        item.TotalBytes)));
            modelPath = await modelManager.EnsureModelAsync(modelProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.ModelVerificationFailed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.ModelDownloadFailed);
        }

        progress?.Report(new TranscriptionActivity(TranscriptionActivityKind.Transcribing));
        string? wavPath = null;
        string? jsonPath = null;
        try
        {
            try
            {
                var convertedWavPath = await converter.ConvertAsync(chunk, jobDirectory, cancellationToken);
                wavPath = ValidateConverterOutput(convertedWavPath, jobDirectory);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProcessingOperationException)
            {
                throw;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.LocalAudioConversionFailed);
            }

            var outputBasePath = CreateOutputBase(jobDirectory, chunk.Index);
            jsonPath = outputBasePath + ".json";
            WhisperWorkerResult workerResult;
            try
            {
                workerResult = await workerRunner.RunAsync(
                    new WhisperWorkerRequest(modelPath, wavPath, outputBasePath),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProcessingOperationException)
            {
                throw;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed);
            }

            jsonPath = ValidateWorkerOutput(workerResult.JsonPath, outputBasePath, jobDirectory);
            if (workerResult.UsedCpuFallback)
            {
                progress?.Report(new TranscriptionActivity(TranscriptionActivityKind.UsingCpuFallback));
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.LocalTranscriptionOutputInvalid);
            }

            var parsed = WhisperWorkerJson.Parse(json, checked(chunk.EndMilliseconds - chunk.StartMilliseconds));
            var segments = parsed.Segments.Select(segment => new TranscriptSegment(
                checked(chunk.StartMilliseconds + segment.StartMilliseconds),
                checked(chunk.StartMilliseconds + segment.EndMilliseconds),
                segment.Text)).ToArray();
            return new TranscriptChunk(chunk.Index, chunk.StartMilliseconds, chunk.EndMilliseconds, segments);
        }
        finally
        {
            DeleteOwned(jsonPath);
            DeleteOwned(wavPath);
        }
    }

    private static string Validate(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Index < 0 || chunk.StartMilliseconds < 0 || chunk.EndMilliseconds <= chunk.StartMilliseconds)
        {
            throw new ArgumentException("The local audio chunk metadata is invalid.", nameof(chunk));
        }
        if (string.IsNullOrWhiteSpace(chunk.Path) || !Path.IsPathFullyQualified(chunk.Path))
        {
            throw new FileNotFoundException("The local M4A checkpoint path is missing or not absolute.", chunk.Path);
        }

        var path = Path.GetFullPath(chunk.Path);
        if (!string.Equals(Path.GetExtension(path), ".m4a", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("The local M4A checkpoint does not exist.", path);
        }
        if (chunk.ByteSize <= 0 || new FileInfo(path).Length != chunk.ByteSize)
        {
            throw new InvalidDataException("The local M4A checkpoint size does not match its metadata.");
        }

        return Path.GetDirectoryName(path)!;
    }

    private static string CreateOutputBase(string jobDirectory, int chunkIndex) => Path.Combine(
        jobDirectory,
        $"local-whisper-{chunkIndex:D4}-{Guid.NewGuid():N}");

    private static string ValidateConverterOutput(string wavPath, string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        if (!Path.IsPathFullyQualified(wavPath))
        {
            throw new InvalidDataException("The local audio converter returned a non-absolute WAV path.");
        }

        var canonical = Path.GetFullPath(wavPath);
        if (!string.Equals(Path.GetDirectoryName(canonical), jobDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(canonical), ".wav", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(canonical))
        {
            throw new InvalidDataException("The local audio converter returned an invalid WAV path.");
        }
        return canonical;
    }

    private static string ValidateWorkerOutput(string jsonPath, string outputBasePath, string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        var canonical = Path.GetFullPath(jsonPath);
        var expected = Path.GetFullPath(outputBasePath + ".json");
        if (!Path.IsPathFullyQualified(jsonPath) ||
            !string.Equals(canonical, expected, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(canonical), jobDirectory, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(canonical))
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.LocalTranscriptionOutputInvalid);
        }
        return canonical;
    }

    private static void DeleteOwned(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Processing recovery owns the job directory and retries transient cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Processing recovery owns the job directory and retries transient cleanup.
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
