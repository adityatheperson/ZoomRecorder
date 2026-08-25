using System.Text.Json.Serialization;

namespace ZoomRecorder.Core.Processing;

public sealed record TranscriptSegment(
    long StartMilliseconds,
    long EndMilliseconds,
    [property: JsonRequired] string Text);

public sealed record TranscriptChunk(
    int Index,
    long StartMilliseconds,
    long EndMilliseconds,
    [property: JsonRequired] IReadOnlyList<TranscriptSegment> Segments)
{
    public int SchemaVersion { get; init; } = 1;

    public string Text => string.Join(' ', Segments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
}

public sealed record Transcript([property: JsonRequired] IReadOnlyList<TranscriptSegment> Segments)
{
    public int SchemaVersion { get; init; } = 1;

    public string? EditedText { get; init; }

    public long StartMilliseconds => Segments.Count == 0 ? 0 : Segments.Min(segment => segment.StartMilliseconds);

    public long EndMilliseconds => Segments.Count == 0 ? 0 : Segments.Max(segment => segment.EndMilliseconds);

    public string Text => string.IsNullOrWhiteSpace(EditedText)
        ? string.Join(' ', Segments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text)))
        : EditedText;
}
