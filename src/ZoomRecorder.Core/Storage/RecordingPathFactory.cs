namespace ZoomRecorder.Core.Storage;

public static class RecordingPathFactory
{
    public static string Create(
        string directory,
        string? meetingLabel,
        DateTimeOffset startedAt,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(exists);

        var label = Sanitize(string.IsNullOrWhiteSpace(meetingLabel) ? "Zoom Meeting" : meetingLabel.Trim());
        var stem = $"{label} - {startedAt:yyyy-MM-dd HHmmss}";
        var candidate = Path.Combine(directory, $"{stem}.mp4");
        var suffix = 2;

        while (exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix++}).mp4");
        }

        return candidate;
    }

    private static string Sanitize(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalidCharacters.Contains(character) ? '_' : character)).Trim();
    }
}
