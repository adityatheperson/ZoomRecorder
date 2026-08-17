using System.Text.RegularExpressions;

namespace ZoomRecorder.Core.Meetings;

public static partial class MeetingInputParser
{
    public static MeetingJoinRequest Parse(string input, string? passcode, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new MeetingInputException("Enter a display name.");
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new MeetingInputException("Enter a Zoom link or meeting ID.");
        }

        var embeddedPasscode = default(string);
        var meetingId = TryParseZoomUri(input.Trim(), out var uri)
            ? ExtractMeetingIdFromUri(uri!, out embeddedPasscode)
            : NonDigitRegex().Replace(input, string.Empty);

        if (meetingId.Length is < 9 or > 11 || !meetingId.All(char.IsAsciiDigit))
        {
            throw new MeetingInputException("Enter a valid Zoom link or meeting ID.");
        }

        var chosenPasscode = string.IsNullOrWhiteSpace(passcode) ? embeddedPasscode : passcode.Trim();
        return new MeetingJoinRequest(meetingId, chosenPasscode, displayName.Trim());
    }

    private static bool TryParseZoomUri(string input, out Uri? uri)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" &&
               (uri.Host.Equals("zoom.us", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".zoom.us", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractMeetingIdFromUri(Uri uri, out string? passcode)
    {
        passcode = ParseQueryValue(uri.Query, "pwd");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var markerIndex = Array.FindIndex(segments, segment => segment is "j" or "wc");
        var candidateIndex = markerIndex + 1;
        return markerIndex >= 0 && candidateIndex < segments.Length
            ? NonDigitRegex().Replace(segments[candidateIndex], string.Empty)
            : string.Empty;
    }

    private static string? ParseQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            }
        }

        return null;
    }

    [GeneratedRegex("[^0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonDigitRegex();
}
