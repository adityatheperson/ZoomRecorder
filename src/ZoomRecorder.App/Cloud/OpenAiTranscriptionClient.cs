using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Cloud;

internal sealed class OpenAiTranscriptionClient(OpenAiApiClient api) : ITranscriptionClient
{
    private readonly OpenAiApiClient api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<TranscriptChunk> TranscribeAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        Validate(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await api.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, api.Endpoint("/v1/audio/transcriptions"))
            {
                Content = Multipart(chunk)
            },
            cancellationToken);

        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<TranscriptionResponse>(
                content,
                OpenAiJson.TransportOptions,
                cancellationToken);
            return Map(payload, chunk);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw OpenAiErrorMapper.NetworkUnavailable();
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or OverflowException)
        {
            throw OpenAiErrorMapper.InvalidResponse();
        }
    }

    private MultipartFormDataContent Multipart(AudioChunk chunk)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(api.Options.TranscriptionModel), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("segment"), "timestamp_granularities[]");
        var file = new StreamContent(File.OpenRead(chunk.Path));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        content.Add(file, "file", Path.GetFileName(chunk.Path));
        return content;
    }

    private static TranscriptChunk Map(TranscriptionResponse? payload, AudioChunk chunk)
    {
        if (payload?.Segments is null)
        {
            throw new InvalidDataException("The transcription response did not contain timestamped segments.");
        }

        var duration = checked(chunk.EndMilliseconds - chunk.StartMilliseconds);
        var segments = new List<TranscriptSegment>(payload.Segments.Count);
        foreach (var segment in payload.Segments)
        {
            if (segment is null || !double.IsFinite(segment.Start) || !double.IsFinite(segment.End) ||
                segment.Start < 0 || segment.End < segment.Start || string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new InvalidDataException("The transcription response contained an invalid segment.");
            }

            var relativeStart = Milliseconds(segment.Start);
            var relativeEnd = Milliseconds(segment.End);
            if (relativeEnd > duration)
            {
                throw new InvalidDataException("The transcription response contained a segment outside the audio chunk.");
            }

            segments.Add(new TranscriptSegment(
                checked(chunk.StartMilliseconds + relativeStart),
                checked(chunk.StartMilliseconds + relativeEnd),
                segment.Text));
        }

        return new TranscriptChunk(chunk.Index, chunk.StartMilliseconds, chunk.EndMilliseconds, segments);
    }

    private static long Milliseconds(double seconds) =>
        checked((long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero));

    private static void Validate(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), "The audio chunk index cannot be negative.");
        }
        if (chunk.StartMilliseconds < 0 || chunk.EndMilliseconds <= chunk.StartMilliseconds)
        {
            throw new ArgumentException("The audio chunk has an invalid timestamp range.", nameof(chunk));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.Path);
        if (!Path.IsPathFullyQualified(chunk.Path) || !File.Exists(chunk.Path))
        {
            throw new FileNotFoundException("The audio chunk does not exist at an absolute path.", chunk.Path);
        }
        if (chunk.ByteSize <= 0 || new FileInfo(chunk.Path).Length != chunk.ByteSize)
        {
            throw new InvalidDataException("The audio chunk size does not match its metadata.");
        }
    }

    private sealed class TranscriptionResponse
    {
        [JsonRequired]
        [JsonPropertyName("segments")]
        public List<TranscriptionSegment?>? Segments { get; init; }
    }

    private sealed class TranscriptionSegment
    {
        [JsonRequired]
        [JsonPropertyName("start")]
        public double Start { get; init; }

        [JsonRequired]
        [JsonPropertyName("end")]
        public double End { get; init; }

        [JsonRequired]
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}

internal static class OpenAiJson
{
    internal static JsonSerializerOptions TransportOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
