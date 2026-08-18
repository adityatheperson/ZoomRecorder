namespace ZoomRecorder.Core.Processing;

public static class TranscriptMerger
{
    public static Transcript Merge(IReadOnlyList<TranscriptChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("At least one transcript chunk is required.", nameof(chunks));
        }

        if (chunks.Any(chunk => chunk is null))
        {
            throw new ArgumentException("Transcript chunks cannot contain null values.", nameof(chunks));
        }

        var ordered = chunks.OrderBy(chunk => chunk.Index).ToArray();
        Validate(ordered);

        var merged = new List<TranscriptSegment>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentSegments = OrderedSegments(ordered[index]);
            var wordsToRemove = index == 0 ? 0 : FindOverlapWordCount(ordered[index - 1], ordered[index]);
            merged.AddRange(RemoveLeadingWords(currentSegments, wordsToRemove));
        }

        var timestampOrdered = merged
            .OrderBy(segment => segment.StartMilliseconds)
            .ThenBy(segment => segment.EndMilliseconds)
            .ToArray();
        return new Transcript(timestampOrdered);
    }

    private static int FindOverlapWordCount(TranscriptChunk previous, TranscriptChunk current)
    {
        var overlapStart = Math.Max(previous.StartMilliseconds, current.StartMilliseconds);
        var overlapEnd = Math.Min(previous.EndMilliseconds, current.EndMilliseconds);
        if (overlapEnd < overlapStart)
        {
            return 0;
        }

        var previousWords = Flatten(OrderedSegments(previous));
        var currentWords = Flatten(OrderedSegments(current));
        var previousEligible = EligibleSuffixLength(previousWords, overlapStart, overlapEnd);
        var currentEligible = EligiblePrefixLength(currentWords, overlapStart, overlapEnd);
        var maximum = Math.Min(previousEligible, currentEligible);

        for (var length = maximum; length > 0; length--)
        {
            var matches = true;
            for (var offset = 0; offset < length; offset++)
            {
                var left = Normalize(previousWords[previousWords.Count - length + offset].Text);
                var right = Normalize(currentWords[offset].Text);
                if (left.Length == 0 || right.Length == 0 || !string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return length;
            }
        }

        return 0;
    }

    private static IReadOnlyList<TranscriptSegment> RemoveLeadingWords(
        IReadOnlyList<TranscriptSegment> segments,
        int wordsToRemove)
    {
        var result = new List<TranscriptSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var words = SplitWords(segment.Text);
            var removeFromSegment = Math.Min(wordsToRemove, words.Length);
            wordsToRemove -= removeFromSegment;
            if (removeFromSegment == words.Length)
            {
                continue;
            }

            result.Add(segment with { Text = string.Join(' ', words[removeFromSegment..]) });
        }

        return result;
    }

    private static List<Word> Flatten(IReadOnlyList<TranscriptSegment> segments)
    {
        var words = new List<Word>();
        foreach (var segment in segments)
        {
            words.AddRange(SplitWords(segment.Text).Select(text =>
                new Word(text, segment.StartMilliseconds, segment.EndMilliseconds)));
        }

        return words;
    }

    private static int EligibleSuffixLength(IReadOnlyList<Word> words, long overlapStart, long overlapEnd)
    {
        var length = 0;
        for (var index = words.Count - 1; index >= 0 && Intersects(words[index], overlapStart, overlapEnd); index--)
        {
            length++;
        }

        return length;
    }

    private static int EligiblePrefixLength(IReadOnlyList<Word> words, long overlapStart, long overlapEnd)
    {
        var length = 0;
        while (length < words.Count && Intersects(words[length], overlapStart, overlapEnd))
        {
            length++;
        }

        return length;
    }

    private static bool Intersects(Word word, long overlapStart, long overlapEnd) =>
        word.EndMilliseconds >= overlapStart && word.StartMilliseconds <= overlapEnd;

    private static string Normalize(string word)
    {
        var start = 0;
        var end = word.Length - 1;
        while (start <= end && IsSurroundingPunctuation(word[start]))
        {
            start++;
        }

        while (end >= start && IsSurroundingPunctuation(word[end]))
        {
            end--;
        }

        return start > end ? string.Empty : word[start..(end + 1)];
    }

    private static bool IsSurroundingPunctuation(char character) =>
        char.IsPunctuation(character) || char.IsSymbol(character);

    private static string[] SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static TranscriptSegment[] OrderedSegments(TranscriptChunk chunk) =>
        chunk.Segments.OrderBy(segment => segment.StartMilliseconds).ThenBy(segment => segment.EndMilliseconds).ToArray();

    private static void Validate(IReadOnlyList<TranscriptChunk> ordered)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            var chunk = ordered[index] ?? throw new ArgumentException("Transcript chunks cannot contain null values.", nameof(ordered));
            if (chunk.Index != index)
            {
                throw new ArgumentException("Transcript chunk indexes must be unique, zero-based, and contiguous.", nameof(ordered));
            }

            if (chunk.StartMilliseconds < 0 || chunk.EndMilliseconds < chunk.StartMilliseconds)
            {
                throw new ArgumentException($"Transcript chunk {chunk.Index} has an invalid timestamp range.", nameof(ordered));
            }

            if (index > 0 && chunk.StartMilliseconds < ordered[index - 1].StartMilliseconds)
            {
                throw new ArgumentException("Transcript chunk timestamps must move forward with their indexes.", nameof(ordered));
            }

            if (chunk.Segments is null)
            {
                throw new ArgumentException($"Transcript chunk {chunk.Index} is missing segments.", nameof(ordered));
            }

            foreach (var segment in chunk.Segments)
            {
                if (segment is null ||
                    segment.StartMilliseconds < chunk.StartMilliseconds ||
                    segment.EndMilliseconds < segment.StartMilliseconds ||
                    segment.EndMilliseconds > chunk.EndMilliseconds)
                {
                    throw new ArgumentException($"Transcript chunk {chunk.Index} contains an invalid segment timestamp.", nameof(ordered));
                }
            }
        }
    }

    private sealed record Word(string Text, long StartMilliseconds, long EndMilliseconds);
}
