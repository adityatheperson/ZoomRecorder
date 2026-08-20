using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomRecorder.App.Cloud;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.Cloud;

public sealed class OpenAiClientTests
{
    private const string ApiKey = "sk-fake-only";
    private const string LectureContent = "PRIVATE LECTURE CONTENT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Transcription_uses_the_exact_endpoint_authorization_model_and_multipart_file()
    {
        using var audio = new TestAudioFile("audio-bytes");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello world","segments":[{"start":0.5,"end":1.25,"text":"hello"},{"start":1.25,"end":2.0,"text":"world"}]}"""));
        using var http = new HttpClient(handler);
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http));

        var result = await transcriber.TranscribeAsync(audio.Chunk(index: 2, start: 10_000, end: 20_000), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        Assert.Equal("multipart/form-data", request.ContentType);
        Assert.Contains("name=model", request.Body, StringComparison.Ordinal);
        Assert.Contains("gpt-transcribe", request.Body, StringComparison.Ordinal);
        Assert.Contains("name=file", request.Body, StringComparison.Ordinal);
        Assert.Contains("filename=lecture.m4a", request.Body, StringComparison.Ordinal);
        Assert.Contains("audio-bytes", request.Body, StringComparison.Ordinal);
        Assert.Contains("verbose_json", request.Body, StringComparison.Ordinal);
        Assert.Contains("timestamp_granularities[]", request.Body, StringComparison.Ordinal);
        Assert.Equal(2, result.Index);
        Assert.Equal(10_000, result.StartMilliseconds);
        Assert.Equal(20_000, result.EndMilliseconds);
        Assert.Collection(result.Segments,
            segment => Assert.Equal(new TranscriptSegment(10_500, 11_250, "hello"), segment),
            segment => Assert.Equal(new TranscriptSegment(11_250, 12_000, "world"), segment));
    }

    [Fact]
    public async Task Authorization_is_request_scoped_and_never_leaks_through_HttpClient_defaults()
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"end":0.1,"text":"hello"}]}"""));
        handler.Enqueue(_ => Json(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http));

        await transcriber.TranscribeAsync(audio.Chunk(), default);
        using var unrelated = await http.GetAsync("https://example.test/unrelated");

        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal($"Bearer {ApiKey}", handler.Requests[0].Authorization);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Lecture_generation_uses_the_Responses_API_configured_model_and_strict_named_schema()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Responses(ValidPackageJson()));
        using var http = new HttpClient(handler);
        var generator = new OpenAiStudyGenerationClient(CreateApi(http));

        var package = await generator.GenerateLectureAsync(Transcript(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/responses", request.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("gpt-5.6-luna", body.RootElement.GetProperty("model").GetString());
        Assert.Contains(LectureContent, request.Body, StringComparison.Ordinal);
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("zoom_recorder_study_package_v1", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        StudyPackageValidator.Validate(package);
        Assert.Equal("Thermodynamics", package.LectureTitle);
    }

    [Fact]
    public async Task Class_guide_generation_validates_inputs_and_uses_its_strict_operation_schema()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Responses(JsonSerializer.Serialize(ValidGuide(), JsonOptions)));
        using var http = new HttpClient(handler);
        var generator = new OpenAiStudyGenerationClient(CreateApi(http));

        var guide = await generator.GenerateGuideAsync([ValidPackage()], default);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("zoom_recorder_class_study_guide_v1", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Single(guide.Topics);
        Assert.Equal("Thermodynamics", guide.Topics[0].Topic);

        var invalid = ValidPackage() with { LectureTitle = " " };
        await Assert.ThrowsAsync<StudyPackageValidationException>(() =>
            generator.GenerateGuideAsync([invalid], default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401, (int)CloudErrorCode.InvalidCredential)]
    [InlineData(402, (int)CloudErrorCode.AccountRestricted)]
    [InlineData(403, (int)CloudErrorCode.AccountRestricted)]
    [InlineData(408, (int)CloudErrorCode.NetworkUnavailable)]
    [InlineData(429, (int)CloudErrorCode.RateLimited)]
    [InlineData(500, (int)CloudErrorCode.ServiceUnavailable)]
    [InlineData(599, (int)CloudErrorCode.ServiceUnavailable)]
    [InlineData(400, (int)CloudErrorCode.InvalidResponse)]
    public async Task Http_failures_map_to_the_closed_sanitized_error_set(int status, int expected)
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Json((HttpStatusCode)status, $"{{\"error\":\"{ApiKey} {LectureContent}\"}}"));
        using var http = new HttpClient(handler);
        var options = Options(maxAttempts: 1);
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http, options: options));

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            transcriber.TranscribeAsync(audio.Chunk(), default));

        Assert.Equal((CloudErrorCode)expected, error.Code);
        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LectureContent, error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_exceptions_map_to_NetworkUnavailable_without_sensitive_details()
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => throw new HttpRequestException($"network {ApiKey} {LectureContent}"));
        using var http = new HttpClient(handler);
        var scheduler = new FakeRetryScheduler();
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http, scheduler: scheduler));

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            transcriber.TranscribeAsync(audio.Chunk(), default));

        Assert.Equal(CloudErrorCode.NetworkUnavailable, error.Code);
        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LectureContent, error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Empty(scheduler.Delays);
    }

    [Theory]
    [InlineData("transcription", "io")]
    [InlineData("transcription", "http-io")]
    [InlineData("transcription", "non-caller-cancellation")]
    [InlineData("study", "io")]
    [InlineData("study", "http-io")]
    [InlineData("study", "non-caller-cancellation")]
    public async Task Response_body_stream_failures_map_to_sanitized_NetworkUnavailable(
        string operation,
        string failure)
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => ThrowingBody(failure));
        using var http = new HttpClient(handler);
        var api = CreateApi(http);

        var error = operation == "transcription"
            ? await Assert.ThrowsAsync<CloudProcessingException>(() =>
                new OpenAiTranscriptionClient(api).TranscribeAsync(audio.Chunk(), default))
            : await Assert.ThrowsAsync<CloudProcessingException>(() =>
                new OpenAiStudyGenerationClient(api).GenerateLectureAsync(Transcript(), default));

        Assert.Equal(CloudErrorCode.NetworkUnavailable, error.Code);
        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LectureContent, error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_the_HTTP_request_and_are_never_retried()
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var scheduler = new FakeRetryScheduler();
        var api = new OpenAiApiClient(http, new StaticCredentialVault(null), Options(), scheduler);

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            new OpenAiTranscriptionClient(api).TranscribeAsync(audio.Chunk(), default));

        Assert.Equal(CloudErrorCode.InvalidCredential, error.Code);
        Assert.Empty(handler.Requests);
        Assert.Empty(scheduler.Delays);
    }

    [Fact]
    public async Task Retry_After_and_capped_exponential_backoff_are_honored_by_the_injected_scheduler()
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(3)));
        handler.Enqueue(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        handler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"end":0.1,"text":"hello"}]}"""));
        using var http = new HttpClient(handler);
        var scheduler = new FakeRetryScheduler();
        var options = Options(maxAttempts: 3, initialDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromSeconds(5));
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http, options: options, scheduler: scheduler));

        await transcriber.TranscribeAsync(audio.Chunk(), default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)], scheduler.Delays);
    }

    [Fact]
    public async Task Http_date_Retry_After_uses_the_injected_clock()
    {
        using var audio = new TestAudioFile("audio");
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Retry(HttpStatusCode.TooManyRequests, now.AddSeconds(6)));
        handler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"end":0.1,"text":"hello"}]}"""));
        using var http = new HttpClient(handler);
        var scheduler = new FakeRetryScheduler { UtcNow = now };
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http, scheduler: scheduler));

        await transcriber.TranscribeAsync(audio.Chunk(), default);

        Assert.Equal([TimeSpan.FromSeconds(6)], scheduler.Delays);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Only_transient_statuses_are_retried(int status)
    {
        using var audio = new TestAudioFile("audio");
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Json((HttpStatusCode)status, "{}"));
        handler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"end":0.1,"text":"hello"}]}"""));
        using var http = new HttpClient(handler);
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http));

        await transcriber.TranscribeAsync(audio.Chunk(), default);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_during_retry_delay_stops_before_another_request()
    {
        using var audio = new TestAudioFile("audio");
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));
        var scheduler = new FakeRetryScheduler
        {
            OnDelay = (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using var http = new HttpClient(handler);
        var transcriber = new OpenAiTranscriptionClient(CreateApi(http, scheduler: scheduler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(audio.Chunk(), cancellation.Token));

        Assert.Single(handler.Requests);
        Assert.Single(scheduler.Delays);
    }

    [Fact]
    public async Task Malformed_or_out_of_range_transcription_responses_are_sanitized_and_not_retried()
    {
        using var audio = new TestAudioFile("audio");
        var malformedHandler = new RecordingHandler();
        malformedHandler.Enqueue(_ => Json(HttpStatusCode.OK, $"{{ malformed {ApiKey} {LectureContent}"));
        using var malformedHttp = new HttpClient(malformedHandler);
        var malformed = new OpenAiTranscriptionClient(CreateApi(malformedHttp));

        var malformedError = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            malformed.TranscribeAsync(audio.Chunk(), default));

        Assert.Equal(CloudErrorCode.InvalidResponse, malformedError.Code);
        Assert.DoesNotContain(ApiKey, malformedError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LectureContent, malformedError.ToString(), StringComparison.Ordinal);
        Assert.Single(malformedHandler.Requests);

        var rangeHandler = new RecordingHandler();
        rangeHandler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"end":999,"text":"hello"}]}"""));
        using var rangeHttp = new HttpClient(rangeHandler);
        var range = new OpenAiTranscriptionClient(CreateApi(rangeHttp));

        var rangeError = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            range.TranscribeAsync(audio.Chunk(), default));

        Assert.Equal(CloudErrorCode.InvalidResponse, rangeError.Code);
        Assert.Single(rangeHandler.Requests);

        var missingTimestampHandler = new RecordingHandler();
        missingTimestampHandler.Enqueue(_ => Json(HttpStatusCode.OK,
            """{"text":"hello","segments":[{"start":0,"text":"hello"}]}"""));
        using var missingTimestampHttp = new HttpClient(missingTimestampHandler);
        var missingTimestamp = new OpenAiTranscriptionClient(CreateApi(missingTimestampHttp));

        var missingTimestampError = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            missingTimestamp.TranscribeAsync(audio.Chunk(), default));

        Assert.Equal(CloudErrorCode.InvalidResponse, missingTimestampError.Code);
        Assert.Single(missingTimestampHandler.Requests);
    }

    [Fact]
    public async Task Invalid_study_JSON_or_schema_maps_to_InvalidResponse_without_retry_or_content_leakage()
    {
        var invalidPackage = ValidPackage() with { Assignments = [ValidPackage().Assignments[0] with { Description = " " }] };
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Responses(JsonSerializer.Serialize(invalidPackage, JsonOptions)));
        using var http = new HttpClient(handler);
        var generator = new OpenAiStudyGenerationClient(CreateApi(http));

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            generator.GenerateLectureAsync(Transcript(), default));

        Assert.Equal(CloudErrorCode.InvalidResponse, error.Code);
        Assert.DoesNotContain(LectureContent, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Read chapter 3", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("note.startMilliseconds")]
    [InlineData("note.endMilliseconds")]
    [InlineData("keyTerm.startMilliseconds")]
    [InlineData("keyTerm.endMilliseconds")]
    [InlineData("assignment.confidence")]
    [InlineData("assignment.normalizedDueDate")]
    [InlineData("assignment.startMilliseconds")]
    [InlineData("assignment.endMilliseconds")]
    public async Task Omitted_required_value_members_map_to_InvalidResponse(string member)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Responses(PackageWithoutRequiredValue(member)));
        using var http = new HttpClient(handler);
        var generator = new OpenAiStudyGenerationClient(CreateApi(http));

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            generator.GenerateLectureAsync(Transcript(), default));

        Assert.Equal(CloudErrorCode.InvalidResponse, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Invalid_class_guide_response_maps_to_InvalidResponse()
    {
        var invalidGuide = ValidGuide() with
        {
            Topics = [new StudyGuideContribution(" ", ["Review entropy"])]
        };
        var handler = new RecordingHandler();
        handler.Enqueue(_ => Responses(JsonSerializer.Serialize(invalidGuide, JsonOptions)));
        using var http = new HttpClient(handler);
        var generator = new OpenAiStudyGenerationClient(CreateApi(http));

        var error = await Assert.ThrowsAsync<CloudProcessingException>(() =>
            generator.GenerateGuideAsync([ValidPackage()], default));

        Assert.Equal(CloudErrorCode.InvalidResponse, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Options_reject_non_https_endpoints_blank_models_and_invalid_retry_bounds()
    {
        using var http = new HttpClient(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => CreateApi(http,
            options: Options() with { Endpoint = new Uri("http://api.openai.com") }));
        Assert.Throws<ArgumentException>(() => CreateApi(http,
            options: Options() with { TranscriptionModel = " " }));
        Assert.Throws<ArgumentException>(() => CreateApi(http,
            options: Options() with { StudyModel = "" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateApi(http,
            options: Options() with { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateApi(http,
            options: Options() with { InitialRetryDelay = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateApi(http,
            options: Options() with { MaxRetryDelay = TimeSpan.Zero }));
    }

    private static OpenAiApiClient CreateApi(
        HttpClient http,
        string? key = ApiKey,
        OpenAiOptions? options = null,
        IOpenAiRetryScheduler? scheduler = null) =>
        new(http, new StaticCredentialVault(key), options ?? Options(), scheduler ?? new FakeRetryScheduler());

    private static OpenAiOptions Options(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null) =>
        new()
        {
            MaxAttempts = maxAttempts,
            InitialRetryDelay = initialDelay ?? TimeSpan.FromSeconds(1),
            MaxRetryDelay = maxDelay ?? TimeSpan.FromSeconds(10)
        };

    private static Transcript Transcript() => new(
        [new TranscriptSegment(1_000, 2_000, LectureContent)]);

    private static StudyPackage ValidPackage() => new(
        SchemaVersion: 1,
        LectureTitle: "Thermodynamics",
        LectureDate: new DateOnly(2026, 8, 18),
        ShortSummary: "Energy and entropy.",
        NoteSections:
        [
            new NoteSection("Entropy", "Entropy measures multiplicity.", [new TimestampReference(1_000, 4_000)])
        ],
        KeyTerms:
        [
            new KeyTerm("Entropy", "A measure of multiplicity.", [new TimestampReference(1_500, 2_500)])
        ],
        Assignments:
        [
            new StudyAssignment(
                "Read chapter 3", "Friday", new DateOnly(2026, 8, 21), 0.75,
                new TimestampReference(5_000, 6_000))
        ],
        ReviewQuestions:
        [
            new ReviewQuestion("What is entropy?", "A measure of multiplicity.", "Entropy")
        ],
        StudyGuideContributions:
        [
            new StudyGuideContribution("Thermodynamics", ["Define entropy", "Review the second law"])
        ]);

    private static string ValidPackageJson() => JsonSerializer.Serialize(ValidPackage(), JsonOptions);

    private static string PackageWithoutRequiredValue(string member)
    {
        var package = JsonNode.Parse(ValidPackageJson())!.AsObject();
        var noteTimestamp = package["noteSections"]!.AsArray()[0]!.AsObject()
            ["timestampReferences"]!.AsArray()[0]!.AsObject();
        var keyTermTimestamp = package["keyTerms"]!.AsArray()[0]!.AsObject()
            ["timestampReferences"]!.AsArray()[0]!.AsObject();
        var assignment = package["assignments"]!.AsArray()[0]!.AsObject();
        var assignmentTimestamp = assignment["sourceTimestamp"]!.AsObject();

        switch (member)
        {
            case "note.startMilliseconds":
                noteTimestamp.Remove("startMilliseconds");
                break;
            case "note.endMilliseconds":
                noteTimestamp["startMilliseconds"] = 0;
                noteTimestamp.Remove("endMilliseconds");
                break;
            case "keyTerm.startMilliseconds":
                keyTermTimestamp.Remove("startMilliseconds");
                break;
            case "keyTerm.endMilliseconds":
                keyTermTimestamp["startMilliseconds"] = 0;
                keyTermTimestamp.Remove("endMilliseconds");
                break;
            case "assignment.confidence":
                assignment.Remove("confidence");
                break;
            case "assignment.normalizedDueDate":
                assignment.Remove("normalizedDueDate");
                break;
            case "assignment.startMilliseconds":
                assignmentTimestamp.Remove("startMilliseconds");
                break;
            case "assignment.endMilliseconds":
                assignmentTimestamp["startMilliseconds"] = 0;
                assignmentTimestamp.Remove("endMilliseconds");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }

        return package.ToJsonString(JsonOptions);
    }

    private static ClassStudyGuide ValidGuide() => new(
        SchemaVersion: 1,
        Topics: [new StudyGuideContribution("Thermodynamics", ["Review entropy"])]);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Responses(string outputText)
    {
        var body = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = outputText } }
                }
            }
        });
        return Json(HttpStatusCode.OK, body);
    }

    private static HttpResponseMessage Retry(HttpStatusCode status, TimeSpan delay)
    {
        var response = Json(status, "{}");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private static HttpResponseMessage Retry(HttpStatusCode status, DateTimeOffset date)
    {
        var response = Json(status, "{}");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        return response;
    }

    private static HttpResponseMessage ThrowingBody(string failure)
    {
        var message = $"stream {ApiKey} {LectureContent}";
        Exception exception = failure switch
        {
            "io" => new IOException(message),
            "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, message),
            "non-caller-cancellation" => new OperationCanceledException(message),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream(exception))
        };
    }

    private sealed class StaticCredentialVault(string? key) : ICredentialVault
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult(key);
        public Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteApiKeyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRetryScheduler : IOpenAiRetryScheduler
    {
        internal List<TimeSpan> Delays { get; } = [];
        internal Func<TimeSpan, CancellationToken, Task>? OnDelay { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return OnDelay?.Invoke(delay, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<RequestSnapshot, HttpResponseMessage>> responses = new();
        internal List<RequestSnapshot> Requests { get; } = [];

        internal void Enqueue(Func<RequestSnapshot, HttpResponseMessage> response) => responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var snapshot = new RequestSnapshot(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(snapshot);
            return responses.Count == 0
                ? Json(HttpStatusCode.OK, "{}")
                : responses.Dequeue()(snapshot);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? ContentType,
        string Body);

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => Task.FromException<int>(exception);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TestAudioFile : IDisposable
    {
        internal TestAudioFile(string content)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zoom-recorder-openai-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "lecture.m4a");
            File.WriteAllText(Path, content);
        }

        private string Directory { get; }
        private string Path { get; }

        internal AudioChunk Chunk(int index = 0, long start = 0, long end = 10_000) =>
            new(index, Path, start, end, new string('a', 64), new FileInfo(Path).Length);

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
