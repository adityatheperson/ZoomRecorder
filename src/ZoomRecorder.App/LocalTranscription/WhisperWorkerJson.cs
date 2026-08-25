using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed record WhisperWorkerJsonResult(IReadOnlyList<TranscriptSegment> Segments);

internal static class WhisperWorkerJson
{
    private const long FinalBoundaryToleranceMilliseconds = 250;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static WhisperWorkerJsonResult Parse(string json, long chunkDurationMs)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            if (chunkDurationMs <= 0)
            {
                throw new InvalidDataException("The local audio chunk duration is invalid.");
            }

            var payload = JsonSerializer.Deserialize<WhisperOutput>(json, Options)
                ?? throw new InvalidDataException("The Whisper output is empty.");
            var source = payload.Transcription
                ?? throw new InvalidDataException("The Whisper output has no transcription array.");
            var segments = new List<TranscriptSegment>(source.Count);
            long previousEnd = 0;

            for (var index = 0; index < source.Count; index++)
            {
                var item = source[index]
                    ?? throw new InvalidDataException("The Whisper output contains a null segment.");
                var offsets = item.Offsets
                    ?? throw new InvalidDataException("The Whisper output segment has no offsets.");
                if (offsets.From is null || offsets.To is null ||
                    !double.IsFinite(offsets.From.Value) || !double.IsFinite(offsets.To.Value))
                {
                    throw new InvalidDataException("The Whisper output segment has invalid offsets.");
                }

                var start = Milliseconds(offsets.From.Value);
                var end = Milliseconds(offsets.To.Value);
                var text = Normalize(item.Text);
                if (start < 0 || end <= start || start < previousEnd || text.Length == 0)
                {
                    throw new InvalidDataException("The Whisper output contains an invalid or unordered segment.");
                }

                if (end > chunkDurationMs)
                {
                    var isFinal = index == source.Count - 1;
                    if (!isFinal || checked(end - chunkDurationMs) > FinalBoundaryToleranceMilliseconds)
                    {
                        throw new InvalidDataException("The Whisper output extends beyond the audio chunk.");
                    }
                    end = chunkDurationMs;
                    if (end <= start)
                    {
                        throw new InvalidDataException("Clamping produced an invalid final segment.");
                    }
                }

                segments.Add(new TranscriptSegment(start, end, text));
                previousEnd = end;
            }

            return new WhisperWorkerJsonResult(segments);
        }
        catch (ProcessingOperationException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException or OverflowException)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.LocalTranscriptionOutputInvalid);
        }
    }

    private static long Milliseconds(double milliseconds) =>
        checked((long)Math.Round(milliseconds, MidpointRounding.AwayFromZero));

    private static string Normalize(string? text) => string.IsNullOrWhiteSpace(text)
        ? string.Empty
        : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class WhisperOutput
    {
        [JsonPropertyName("systeminfo")]
        public JsonElement SystemInfo { get; init; }

        [JsonPropertyName("model")]
        public JsonElement Model { get; init; }

        [JsonPropertyName("params")]
        public JsonElement Params { get; init; }

        [JsonPropertyName("result")]
        public JsonElement Result { get; init; }

        [JsonRequired]
        [JsonPropertyName("transcription")]
        public List<WhisperSegment?>? Transcription { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
    private sealed class WhisperSegment
    {
        [JsonRequired]
        [JsonPropertyName("offsets")]
        public WhisperOffsets? Offsets { get; init; }

        [JsonRequired]
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
    private sealed class WhisperOffsets
    {
        [JsonRequired]
        [JsonPropertyName("from")]
        public double? From { get; init; }

        [JsonRequired]
        [JsonPropertyName("to")]
        public double? To { get; init; }
    }
}
