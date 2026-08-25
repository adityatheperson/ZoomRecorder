using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.LocalTranscription;

public sealed class WhisperWorkerJsonTests
{
    [Fact]
    public void Parses_v191_fixture_and_normalizes_ordered_segments()
    {
        var result = WhisperWorkerJson.Parse(Fixture(), chunkDurationMs: 2_000);

        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(new TranscriptSegment(0, 1_250, "hello world"), segment),
            segment => Assert.Equal(new TranscriptSegment(1_250, 2_000, "from class"), segment));
    }

    [Fact]
    public void Final_segment_within_rounding_tolerance_is_clamped()
    {
        var result = WhisperWorkerJson.Parse(JsonWithSegments((0, 10_200, "lecture")), chunkDurationMs: 10_000);

        Assert.Equal(10_000, result.Segments[^1].EndMilliseconds);
    }

    [Theory]
    [InlineData(-1, 100, "text")]
    [InlineData(200, 100, "text")]
    [InlineData(0, 100, "   ")]
    public void Rejects_invalid_segment_values(double from, double to, string text) =>
        AssertInvalid(JsonWithSegments((from, to, text)), 1_000);

    [Fact]
    public void Rejects_nonfinite_offsets() =>
        AssertInvalid(JsonWithRawSegment("1e9999", "100", "text"), 1_000);

    [Fact]
    public void Rejects_overlap_and_out_of_order_segments() =>
        AssertInvalid(JsonWithSegments((0, 600, "one"), (500, 900, "two")), 1_000);

    [Fact]
    public void Rejects_nonfinal_or_excessive_duration_overflow()
    {
        AssertInvalid(JsonWithSegments((0, 1_100, "one"), (1_100, 1_200, "two")), 1_000);
        AssertInvalid(JsonWithSegments((0, 1_251, "one")), 1_000);
    }

    [Theory]
    [InlineData("{\"transcription\":[],\"unexpected\":true}")]
    [InlineData("{\"systeminfo\":\"x\"}")]
    [InlineData("{\"transcription\":[{\"text\":\"x\"}]}")]
    [InlineData("{\"transcription\":[{\"offsets\":{\"from\":0,\"to\":1}}]}")]
    [InlineData("{\"transcription\":[{\"offsets\":{\"from\":0},\"text\":\"x\"}]}")]
    public void Rejects_unknown_root_and_missing_required_fields(string json) => AssertInvalid(json, 1_000);

    private static void AssertInvalid(string json, long duration)
    {
        var error = Assert.Throws<ProcessingOperationException>(() => WhisperWorkerJson.Parse(json, duration));
        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionOutputInvalid, error.Code);
    }

    private static string JsonWithSegments(params (double From, double To, string Text)[] segments) =>
        "{\"transcription\":[" + string.Join(',', segments.Select(segment =>
            $"{{\"offsets\":{{\"from\":{segment.From.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"to\":{segment.To.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},\"text\":{System.Text.Json.JsonSerializer.Serialize(segment.Text)}}}")) + "]}";

    private static string JsonWithRawSegment(string from, string to, string text) =>
        $"{{\"transcription\":[{{\"offsets\":{{\"from\":{from},\"to\":{to}}},\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]}}";

    private static string Fixture() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "LocalTranscription",
        "Fixtures",
        "whisper-output-full.json"));
}
