using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class TranscriptMergerTests
{
    [Fact]
    public void Exact_words_in_adjacent_time_overlap_are_removed_once()
    {
        var chunks = new[]
        {
            Chunk(0, 0, 10_000, Segment(0, 6_000, "Today we study"), Segment(6_000, 10_000, "energy transfer")),
            Chunk(1, 8_000, 18_000, Segment(8_000, 11_000, "energy transfer in cells"), Segment(11_000, 18_000, "and ecosystems"))
        };

        var transcript = TranscriptMerger.Merge(chunks);

        Assert.Equal("Today we study energy transfer in cells and ecosystems", transcript.Text);
        Assert.Equal(
            ["Today we study", "energy transfer", "in cells", "and ecosystems"],
            transcript.Segments.Select(segment => segment.Text));
    }

    [Fact]
    public void Overlap_comparison_ignores_case_and_surrounding_punctuation()
    {
        var chunks = new[]
        {
            Chunk(0, 0, 10_000, Segment(7_000, 10_000, "The mitochondria,")),
            Chunk(1, 8_000, 15_000, Segment(8_000, 12_000, "the MITOCHONDRIA! creates ATP"))
        };

        var transcript = TranscriptMerger.Merge(chunks);

        Assert.Equal("The mitochondria, creates ATP", transcript.Text);
    }

    [Fact]
    public void Repeated_words_outside_a_time_overlap_are_preserved()
    {
        var chunks = new[]
        {
            Chunk(0, 0, 5_000, Segment(0, 5_000, "very very")),
            Chunk(1, 5_001, 10_000, Segment(5_001, 10_000, "very important"))
        };

        var transcript = TranscriptMerger.Merge(chunks);

        Assert.Equal("very very very important", transcript.Text);
    }

    [Fact]
    public void Merge_retains_absolute_timestamps_and_orders_segments()
    {
        var chunks = new[]
        {
            Chunk(1, 9_000, 20_000, Segment(11_000, 20_000, "second unique")),
            Chunk(0, 0, 10_000, Segment(0, 7_000, "first"), Segment(7_000, 10_000, "shared phrase"))
        };

        var transcript = TranscriptMerger.Merge(chunks);

        Assert.Equal([0L, 7_000L, 11_000L], transcript.Segments.Select(segment => segment.StartMilliseconds));
        Assert.Equal(20_000, transcript.Segments[^1].EndMilliseconds);
    }

    [Fact]
    public void Merge_orders_unique_segments_that_interleave_across_chunk_overlap()
    {
        var chunks = new[]
        {
            Chunk(0, 0, 10_000, Segment(0, 7_000, "first"), Segment(9_000, 10_000, "third")),
            Chunk(1, 8_000, 12_000, Segment(8_000, 12_000, "second"))
        };

        var transcript = TranscriptMerger.Merge(chunks);

        Assert.Equal([0L, 8_000L, 9_000L], transcript.Segments.Select(segment => segment.StartMilliseconds));
        Assert.Equal("first second third", transcript.Text);
        Assert.Equal(12_000, transcript.EndMilliseconds);
    }

    [Fact]
    public void One_chunk_is_returned_as_a_versioned_transcript()
    {
        var transcript = TranscriptMerger.Merge(
            [Chunk(0, 5_000, 9_000, Segment(5_000, 9_000, "one chunk"))]);

        Assert.Equal(1, transcript.SchemaVersion);
        Assert.Equal("one chunk", transcript.Text);
        Assert.Single(transcript.Segments);
    }

    [Fact]
    public void Merge_rejects_null_or_empty_input()
    {
        Assert.Throws<ArgumentNullException>(() => TranscriptMerger.Merge(null!));
        Assert.Throws<ArgumentException>(() => TranscriptMerger.Merge([]));
        Assert.Throws<ArgumentException>(() => TranscriptMerger.Merge([null!]));
    }

    [Theory]
    [MemberData(nameof(InvalidIndexes))]
    public void Merge_rejects_duplicate_or_noncontiguous_indexes(IReadOnlyList<TranscriptChunk> chunks)
    {
        Assert.Throws<ArgumentException>(() => TranscriptMerger.Merge(chunks));
    }

    public static TheoryData<IReadOnlyList<TranscriptChunk>> InvalidIndexes => new()
    {
        { [Chunk(0, 0, 10, Segment(0, 10, "a")), Chunk(0, 10, 20, Segment(10, 20, "b"))] },
        { [Chunk(0, 0, 10, Segment(0, 10, "a")), Chunk(2, 10, 20, Segment(10, 20, "b"))] },
        { [Chunk(1, 0, 10, Segment(0, 10, "a"))] }
    };

    [Theory]
    [MemberData(nameof(InvalidTimes))]
    public void Merge_rejects_invalid_chunk_or_segment_times(IReadOnlyList<TranscriptChunk> chunks)
    {
        Assert.Throws<ArgumentException>(() => TranscriptMerger.Merge(chunks));
    }

    public static TheoryData<IReadOnlyList<TranscriptChunk>> InvalidTimes => new()
    {
        { [Chunk(0, -1, 10, Segment(0, 10, "a"))] },
        { [Chunk(0, 10, 9, Segment(9, 10, "a"))] },
        { [Chunk(0, 0, 10, Segment(-1, 5, "a"))] },
        { [Chunk(0, 0, 10, Segment(8, 7, "a"))] },
        { [Chunk(0, 0, 10, Segment(0, 11, "outside"))] }
    };

    private static TranscriptChunk Chunk(
        int index,
        long start,
        long end,
        params TranscriptSegment[] segments) =>
        new(index, start, end, segments);

    private static TranscriptSegment Segment(long start, long end, string text) =>
        new(start, end, text);
}
