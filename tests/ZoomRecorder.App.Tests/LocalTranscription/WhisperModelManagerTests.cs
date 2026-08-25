using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ZoomRecorder.App.LocalTranscription;

namespace ZoomRecorder.App.Tests.LocalTranscription;

public sealed class WhisperModelManagerTests
{
    [Fact]
    public void Load_pinned_manifest_reads_exact_model_metadata()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Whisper",
            "model-small.en.json");

        using var stream = File.OpenRead(manifestPath);
        var manifest = WhisperModelManifest.Load(stream);

        Assert.Equal("ggml-small.en.bin", manifest.FileName);
        Assert.Equal(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.en.bin",
            manifest.DownloadUri.AbsoluteUri);
        Assert.Equal(487614201, manifest.ByteLength);
        Assert.Equal("c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", manifest.Sha256);
        Assert.True(manifest.ByteLength > 0);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Sha256);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 2")]
    [InlineData("\"fileName\": \"..\\\\outside.bin\"")]
    [InlineData("\"downloadUri\": \"http://huggingface.co/ggerganov/whisper.cpp/resolve/revision/ggml-small.en.bin\"")]
    [InlineData("\"downloadUri\": \"https://huggingface.co/other/repository/resolve/revision/ggml-small.en.bin\"")]
    [InlineData("\"byteLength\": 0")]
    [InlineData("\"sha256\": \"C6138D6D58ECC8322097E0F987C32F1BE8BB0A18532A3F88F734D1BBF9C41E5D\"")]
    public void Load_rejects_an_invalid_or_unpinned_manifest(string replacement)
    {
        const string validManifest = """
            {
              "schemaVersion": 1,
              "fileName": "ggml-small.en.bin",
              "downloadUri": "https://huggingface.co/ggerganov/whisper.cpp/resolve/revision/ggml-small.en.bin",
              "byteLength": 1,
              "sha256": "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"
            }
            """;
        var propertyName = replacement[..replacement.IndexOf(':')];
        var json = validManifest.Replace($"{propertyName}: {PropertyValue(validManifest, propertyName)}", replacement);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<InvalidDataException>(() => WhisperModelManifest.Load(stream));
    }

    [Fact]
    public void Model_path_rejects_a_filename_that_escapes_the_canonical_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidDataException>(() =>
            LocalTranscriptionPaths.GetModelPath(root, "..\\outside.bin"));
    }

    [Fact]
    public async Task Downloads_and_publishes_a_verified_model()
    {
        using var fixture = ModelFixture.ValidPayload();

        var modelPath = await fixture.Manager.EnsureModelAsync(null, CancellationToken.None);

        Assert.Equal(fixture.FinalModelPath, modelPath);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task Removes_stale_owned_partial_files_before_acquisition()
    {
        using var fixture = ModelFixture.ValidPayload();
        var stale = fixture.FinalModelPath + ".stale.partial";
        var foreign = Path.Combine(fixture.ModelsRoot, "other-model.stale.partial");
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(foreign, "foreign");

        await fixture.Manager.EnsureModelAsync(null, CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Rejects_an_overlong_response_from_headers_without_creating_a_partial()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.ReportedContentLength = fixture.Payload.Length + 1;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Manager.EnsureModelAsync(null, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(fixture.ModelsRoot, "*.partial"));
        Assert.False(File.Exists(fixture.FinalModelPath));
    }

    [Fact]
    public async Task Verifies_a_cached_model_without_another_request()
    {
        using var fixture = ModelFixture.ValidPayload();
        await File.WriteAllBytesAsync(fixture.FinalModelPath, fixture.Payload);

        var modelPath = await fixture.Manager.EnsureModelAsync(null, CancellationToken.None);

        Assert.Equal(fixture.FinalModelPath, modelPath);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_download()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.BlockAfterFirstResponse = true;
        using var cancellation = new CancellationTokenSource();
        var progress = new DelegateProgress<ModelDownloadProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Manager.EnsureModelAsync(progress, cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(fixture.ModelsRoot, "*.partial"));
        Assert.False(File.Exists(fixture.FinalModelPath));
    }

    [Fact]
    public async Task Final_canceled_caller_waits_for_partial_cleanup_and_stream_disposal()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.DelayCancellationCleanup = true;
        using var cancellation = new CancellationTokenSource();
        var wrotePartial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var caller = fixture.Manager.EnsureModelAsync(
            new DelegateProgress<ModelDownloadProgress>(_ => wrotePartial.TrySetResult(true)),
            cancellation.Token);
        await wrotePartial.Task;
        cancellation.Cancel();
        await fixture.Handler.WaitForCancellationCleanupAsync();

        var callerCompletedBeforeCleanup = caller.IsCompleted;
        var partialsBeforeCleanup = Directory.EnumerateFiles(fixture.ModelsRoot, "*.partial").ToArray();
        var streamDisposedBeforeCleanup = fixture.Handler.StreamDisposed.IsCompleted;
        fixture.Handler.ReleaseCancellationCleanup();
        var cancellationException = await Record.ExceptionAsync(() => caller);

        Assert.False(callerCompletedBeforeCleanup);
        Assert.NotEmpty(partialsBeforeCleanup);
        Assert.False(streamDisposedBeforeCleanup);
        Assert.IsAssignableFrom<OperationCanceledException>(cancellationException);
        Assert.Empty(Directory.EnumerateFiles(fixture.ModelsRoot, "*.partial"));
        Assert.True(fixture.Handler.StreamDisposed.IsCompleted);
    }

    [Fact]
    public async Task Quarantines_a_corrupt_cached_model_before_downloading_a_replacement()
    {
        using var fixture = ModelFixture.ValidPayload();
        await File.WriteAllBytesAsync(
            fixture.FinalModelPath,
            Enumerable.Repeat((byte)0x99, fixture.Payload.Length).ToArray());

        var modelPath = await fixture.Manager.EnsureModelAsync(null, CancellationToken.None);

        Assert.Equal(fixture.FinalModelPath, modelPath);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(modelPath));
        Assert.Single(Directory.EnumerateFiles(fixture.ModelsRoot, "ggml-small.en.bin.corrupt-*"));
    }

    [Fact]
    public async Task Rejects_a_download_with_a_hash_mismatch_without_publishing_it()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.Payload = Enumerable.Repeat((byte)0x01, fixture.Payload.Length).ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Manager.EnsureModelAsync(null, CancellationToken.None));

        Assert.False(File.Exists(fixture.FinalModelPath));
        Assert.Single(Directory.EnumerateFiles(fixture.ModelsRoot, "ggml-small.en.bin.corrupt-*"));
    }

    [Fact]
    public async Task Reports_downloaded_byte_progress_against_the_expected_total()
    {
        using var fixture = ModelFixture.ValidPayload();
        var reports = new List<ModelDownloadProgress>();

        await fixture.Manager.EnsureModelAsync(
            new DelegateProgress<ModelDownloadProgress>(reports.Add),
            CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(fixture.Payload.Length, reports[^1].CompletedBytes);
        Assert.Equal(fixture.Payload.Length, reports[^1].TotalBytes);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_verified_download()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.BlockUntilReleased = true;
        var first = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);
        await fixture.Handler.WaitForRequestAsync();
        var second = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);

        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.False(File.Exists(fixture.FinalModelPath));
        fixture.Handler.ReleaseBody();
        Assert.Equal(await first, await second);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.True(File.Exists(fixture.FinalModelPath));
    }

    [Fact]
    public async Task First_caller_can_cancel_while_a_later_caller_keeps_the_shared_download_alive()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.BlockUntilReleased = true;
        using var firstCancellation = new CancellationTokenSource();

        var first = fixture.Manager.EnsureModelAsync(null, firstCancellation.Token);
        await fixture.Handler.WaitForRequestAsync();
        var second = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);
        firstCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        fixture.Handler.ReleaseBody();

        Assert.Equal(fixture.FinalModelPath, await second);
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task Later_caller_can_cancel_while_the_first_caller_keeps_the_shared_download_alive()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.BlockUntilReleased = true;
        using var laterCancellation = new CancellationTokenSource();

        var first = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);
        await fixture.Handler.WaitForRequestAsync();
        var second = fixture.Manager.EnsureModelAsync(null, laterCancellation.Token);
        laterCancellation.Cancel();

        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(second, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(first.IsCompleted);
        fixture.Handler.ReleaseBody();

        Assert.Equal(fixture.FinalModelPath, await first);
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task Concurrent_callers_each_receive_download_progress()
    {
        using var fixture = ModelFixture.ValidPayload();
        fixture.Handler.BlockUntilReleased = true;
        var firstReports = new List<ModelDownloadProgress>();
        var secondReports = new List<ModelDownloadProgress>();

        var first = fixture.Manager.EnsureModelAsync(
            new DelegateProgress<ModelDownloadProgress>(firstReports.Add),
            CancellationToken.None);
        await fixture.Handler.WaitForRequestAsync();
        var second = fixture.Manager.EnsureModelAsync(
            new DelegateProgress<ModelDownloadProgress>(secondReports.Add),
            CancellationToken.None);
        fixture.Handler.ReleaseBody();

        await Task.WhenAll(first, second);

        Assert.NotEmpty(firstReports);
        Assert.NotEmpty(secondReports);
        Assert.Equal(fixture.Payload.Length, firstReports[^1].CompletedBytes);
        Assert.Equal(fixture.Payload.Length, secondReports[^1].CompletedBytes);
    }

    private sealed class ModelFixture : IDisposable
    {
        private ModelFixture(byte[] payload, TestHttpHandler handler, string modelsRoot, WhisperModelManager manager)
        {
            Payload = payload;
            Handler = handler;
            ModelsRoot = modelsRoot;
            Manager = manager;
        }

        public byte[] Payload { get; }
        public TestHttpHandler Handler { get; }
        public string ModelsRoot { get; }
        public WhisperModelManager Manager { get; }
        public string FinalModelPath => Path.Combine(ModelsRoot, "ggml-small.en.bin");

        public static ModelFixture ValidPayload()
        {
            var payload = Encoding.UTF8.GetBytes("verified test model payload");
            var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var manifest = LoadManifest($$"""
                {
                  "schemaVersion": 1,
                  "fileName": "ggml-small.en.bin",
                  "downloadUri": "https://huggingface.co/ggerganov/whisper.cpp/resolve/test-revision/ggml-small.en.bin",
                  "byteLength": {{payload.Length}},
                  "sha256": "{{digest}}"
                }
                """);
            var root = Path.Combine(Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var handler = new TestHttpHandler(payload);
            var client = new HttpClient(handler);

            return new ModelFixture(payload, handler, root, new WhisperModelManager(client, manifest, root));
        }

        public void Dispose()
        {
            Handler.Dispose();
            Directory.Delete(ModelsRoot, recursive: true);
        }

        private static WhisperModelManifest LoadManifest(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return WhisperModelManifest.Load(stream);
        }
    }

    private sealed class TestHttpHandler(byte[] payload) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _requestObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _bodyReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationCleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationCleanupReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _streamDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Payload { get; set; } = payload;
        public bool BlockAfterFirstResponse { get; set; }
        public bool BlockUntilReleased { get; set; }
        public bool DelayCancellationCleanup { get; set; }
        public long? ReportedContentLength { get; set; }
        private int _requestCount;
        public int RequestCount => _requestCount;
        public Task StreamDisposed => _streamDisposed.Task;
        public Task WaitForRequestAsync() => _requestObserved.Task;
        public Task WaitForCancellationCleanupAsync() => _cancellationCleanupStarted.Task;
        public void ReleaseBody() => _bodyReleased.TrySetResult(true);
        public void ReleaseCancellationCleanup() => _cancellationCleanupReleased.TrySetResult(true);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requestObserved.TrySetResult(true);
            HttpContent content = DelayCancellationCleanup
                ? new CancellationCleanupContent(
                    Payload,
                    _cancellationCleanupStarted,
                    _cancellationCleanupReleased.Task,
                    _streamDisposed)
                : BlockUntilReleased
                ? new GatedContent(Payload, _bodyReleased.Task)
                : BlockAfterFirstResponse
                    ? new BlockingContent(Payload)
                    : new ByteArrayContent(Payload);
            content.Headers.ContentLength = ReportedContentLength ?? Payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class BlockingContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(payload.AsMemory(0, 1));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingReadStream(payload));
    }

    private sealed class BlockingReadStream(byte[] payload) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                buffer.Span[0] = payload[0];
                return ValueTask.FromResult(1);
            }

            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }

    private sealed class GatedContent(byte[] payload, Task released) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await released;
            await stream.WriteAsync(payload);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new GatedReadStream(payload, released));
    }

    private sealed class CancellationCleanupContent(
        byte[] payload,
        TaskCompletionSource<bool> cleanupStarted,
        Task cleanupReleased,
        TaskCompletionSource<bool> streamDisposed) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CancellationCleanupReadStream(payload, cleanupStarted, cleanupReleased, streamDisposed));
    }

    private sealed class CancellationCleanupReadStream(
        byte[] payload,
        TaskCompletionSource<bool> cleanupStarted,
        Task cleanupReleased,
        TaskCompletionSource<bool> streamDisposed) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                buffer.Span[0] = payload[0];
                return 1;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cleanupStarted.TrySetResult(true);
                await cleanupReleased;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                streamDisposed.TrySetResult(true);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GatedReadStream(byte[] payload, Task released) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await released.WaitAsync(cancellationToken);
            if (_position == payload.Length)
            {
                return 0;
            }

            var bytesRead = Math.Min(buffer.Length, payload.Length - _position);
            payload.AsMemory(_position, bytesRead).CopyTo(buffer);
            _position += bytesRead;
            return bytesRead;
        }
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string PropertyValue(string json, string propertyName)
    {
        var start = json.IndexOf(propertyName, StringComparison.Ordinal) + propertyName.Length + 2;
        var end = json.IndexOf('\n', start);
        return json[start..end].TrimEnd(',', '\r');
    }
}
