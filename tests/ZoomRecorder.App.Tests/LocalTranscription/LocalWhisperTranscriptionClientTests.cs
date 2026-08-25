using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.LocalTranscription;

public sealed class LocalWhisperTranscriptionClientTests
{
    [Fact]
    public async Task Orchestrates_model_conversion_worker_offset_activity_and_cleanup()
    {
        using var files = new ClientFiles();
        var activities = new List<TranscriptionActivity>();
        var model = new FakeModel(files.ModelPath)
        {
            OnEnsure = progress => progress?.Report(new ModelDownloadProgress(5, 10))
        };
        var converter = new FakeConverter(files.WavPath);
        var runner = new FakeRunner(request =>
        {
            Assert.Equal(files.ModelPath, request.ModelPath);
            Assert.Equal(files.WavPath, request.WavPath);
            Assert.Equal(files.JobDirectory, Path.GetDirectoryName(request.OutputBasePath), StringComparer.OrdinalIgnoreCase);
            var json = request.OutputBasePath + ".json";
            File.WriteAllText(json, Json((1_000, 2_000, "  important   topic ")));
            return new WhisperWorkerResult(json, UsedCpuFallback: true);
        });

        var result = await new LocalWhisperTranscriptionClient(model, converter, runner).TranscribeAsync(
            files.Chunk,
            new ImmediateProgress<TranscriptionActivity>(activities.Add),
            CancellationToken.None);

        Assert.Equal(files.Chunk.Index, result.Index);
        Assert.Equal(new TranscriptSegment(11_000, 12_000, "important topic"), Assert.Single(result.Segments));
        Assert.Equal(
            [
                TranscriptionActivityKind.AcquiringModel,
                TranscriptionActivityKind.AcquiringModel,
                TranscriptionActivityKind.Transcribing,
                TranscriptionActivityKind.UsingCpuFallback
            ],
            activities.Select(activity => activity.Kind));
        Assert.Equal((5L, 10L), (activities[1].CompletedBytes, activities[1].TotalBytes));
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, converter.Calls);
        Assert.Equal(1, runner.Calls);
        Assert.True(File.Exists(files.M4aPath));
        Assert.False(File.Exists(files.WavPath));
        Assert.Empty(Directory.GetFiles(files.JobDirectory, "*.json"));
    }

    [Fact]
    public async Task Invalid_worker_json_maps_output_error_and_cleans_wav_and_json()
    {
        using var files = new ClientFiles();
        var runner = new FakeRunner(request =>
        {
            var json = request.OutputBasePath + ".json";
            File.WriteAllText(json, "{\"transcription\":[{\"text\":\"missing offsets\"}]}");
            return new WhisperWorkerResult(json, false);
        });
        var client = new LocalWhisperTranscriptionClient(new FakeModel(files.ModelPath), new FakeConverter(files.WavPath), runner);

        var error = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            client.TranscribeAsync(files.Chunk, null, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionOutputInvalid, error.Code);
        Assert.False(File.Exists(files.WavPath));
        Assert.Empty(Directory.GetFiles(files.JobDirectory, "*.json"));
        Assert.True(File.Exists(files.M4aPath));
    }

    [Fact]
    public async Task Cancellation_during_model_download_never_converts_or_runs_worker()
    {
        using var files = new ClientFiles();
        using var cancellation = new CancellationTokenSource();
        var model = new FakeModel(files.ModelPath)
        {
            Ensure = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<string>(token);
            }
        };
        var converter = new FakeConverter(files.WavPath);
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalWhisperTranscriptionClient(model, converter, runner).TranscribeAsync(
                files.Chunk, null, cancellation.Token));

        Assert.Equal(1, model.Calls);
        Assert.Equal(0, converter.Calls);
        Assert.Equal(0, runner.Calls);
        Assert.True(File.Exists(files.M4aPath));
    }

    [Fact]
    public async Task Cancellation_during_worker_cleans_owned_wav_and_json_and_preserves_m4a()
    {
        using var files = new ClientFiles();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(request =>
        {
            File.WriteAllText(request.OutputBasePath + ".json", Json((0, 100, "partial")));
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalWhisperTranscriptionClient(new FakeModel(files.ModelPath), new FakeConverter(files.WavPath), runner)
                .TranscribeAsync(files.Chunk, null, cancellation.Token));

        Assert.Equal(1, runner.Calls);
        Assert.False(File.Exists(files.WavPath));
        Assert.Empty(Directory.GetFiles(files.JobDirectory, "*.json"));
        Assert.True(File.Exists(files.M4aPath));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), CloudProcessingErrorCode.ModelDownloadFailed)]
    [InlineData(typeof(InvalidDataException), CloudProcessingErrorCode.ModelVerificationFailed)]
    public async Task Model_failures_map_to_specific_local_errors(Type failureType, CloudProcessingErrorCode expected)
    {
        using var files = new ClientFiles();
        var model = new FakeModel(files.ModelPath)
        {
            Ensure = _ => Task.FromException<string>((Exception)Activator.CreateInstance(failureType)!)
        };

        var error = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            new LocalWhisperTranscriptionClient(model, new FakeConverter(files.WavPath), new FakeRunner(_ => throw new Exception()))
                .TranscribeAsync(files.Chunk, null, CancellationToken.None));

        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task Conversion_failure_maps_local_audio_error_without_running_worker()
    {
        using var files = new ClientFiles();
        var converter = new FakeConverter(files.WavPath) { Failure = new IOException("disk") };
        var runner = new FakeRunner(_ => throw new Exception());

        var error = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            new LocalWhisperTranscriptionClient(new FakeModel(files.ModelPath), converter, runner)
                .TranscribeAsync(files.Chunk, null, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalAudioConversionFailed, error.Code);
        Assert.Equal(0, runner.Calls);
        Assert.True(File.Exists(files.M4aPath));
    }

    [Fact]
    public async Task Converter_path_outside_job_is_rejected_without_deleting_foreign_file()
    {
        using var files = new ClientFiles();
        var foreignWav = Path.Combine(files.Root, "foreign.wav");
        var converter = new FakeConverter(foreignWav);
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));

        var error = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            new LocalWhisperTranscriptionClient(new FakeModel(files.ModelPath), converter, runner)
                .TranscribeAsync(files.Chunk, null, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalAudioConversionFailed, error.Code);
        Assert.Equal(0, runner.Calls);
        Assert.True(File.Exists(foreignWav));
        Assert.True(File.Exists(files.M4aPath));
    }

    private static string Json(params (long From, long To, string Text)[] segments) =>
        "{\"transcription\":[" + string.Join(',', segments.Select(segment =>
            $"{{\"offsets\":{{\"from\":{segment.From},\"to\":{segment.To}}},\"text\":{System.Text.Json.JsonSerializer.Serialize(segment.Text)}}}")) + "]}";

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeModel(string path) : IWhisperModelManager
    {
        public int Calls { get; private set; }
        public Action<IProgress<ModelDownloadProgress>?>? OnEnsure { get; init; }
        public Func<CancellationToken, Task<string>>? Ensure { get; init; }

        public Task<string> EnsureModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            OnEnsure?.Invoke(progress);
            return Ensure?.Invoke(cancellationToken) ?? Task.FromResult(path);
        }
    }

    private sealed class FakeConverter(string wavPath) : ILocalPcmAudioConverter
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }

        public Task<string> ConvertAsync(AudioChunk chunk, string jobDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) return Task.FromException<string>(Failure);
            File.WriteAllBytes(wavPath, [1, 2, 3]);
            return Task.FromResult(wavPath);
        }
    }

    private sealed class FakeRunner(Func<WhisperWorkerRequest, WhisperWorkerResult> run) : IWhisperWorkerRunner
    {
        public int Calls { get; private set; }

        public Task<WhisperWorkerResult> RunAsync(WhisperWorkerRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(run(request));
        }
    }

    private sealed class ClientFiles : IDisposable
    {
        internal ClientFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zoom-recorder-local-client-{Guid.NewGuid():N}");
            JobDirectory = Path.Combine(Root, "job");
            Directory.CreateDirectory(JobDirectory);
            M4aPath = Path.Combine(JobDirectory, "chunk.m4a");
            ModelPath = Path.Combine(Root, "model.bin");
            WavPath = Path.Combine(JobDirectory, "chunk.wav");
            File.WriteAllBytes(M4aPath, [1, 2, 3, 4]);
            File.WriteAllBytes(ModelPath, [5]);
            Chunk = new AudioChunk(2, M4aPath, 10_000, 20_000, new string('a', 64), 4);
        }

        internal string Root { get; }
        internal string JobDirectory { get; }
        internal string M4aPath { get; }
        internal string ModelPath { get; }
        internal string WavPath { get; }
        internal AudioChunk Chunk { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
