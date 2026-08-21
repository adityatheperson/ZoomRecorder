namespace ZoomRecorder.App.ZoomClient;

public enum ZoomWindowSelectionKind
{
    None,
    Selected,
    Ambiguous
}

public readonly record struct ZoomWindowSelectionResult(ZoomWindowSelectionKind Kind, nint Handle)
{
    public static ZoomWindowSelectionResult None => new(ZoomWindowSelectionKind.None, nint.Zero);
    public static ZoomWindowSelectionResult Ambiguous => new(ZoomWindowSelectionKind.Ambiguous, nint.Zero);
    public static ZoomWindowSelectionResult Selected(nint handle) => new(ZoomWindowSelectionKind.Selected, handle);
}

public static class ZoomWindowSelection
{
    private static readonly string[] ExcludedTitleTerms =
        ["zoom workplace", "settings", "sign in", "signin", "updater", "update zoom"];

    private static readonly string[] ExcludedClassTerms =
        ["settings", "login", "signin", "updater", "authentication"];

    public static ZoomWindowSelectionResult Select(IReadOnlyList<ZoomWindowDescription> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var candidates = windows
            .Where(IsCandidate)
            .Select(window => (Window: window, Score: Score(window)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Window.Handle)
            .ToArray();

        if (candidates.Length == 0)
        {
            return ZoomWindowSelectionResult.None;
        }

        if (candidates.Length > 1 && candidates[0].Score == candidates[1].Score)
        {
            return ZoomWindowSelectionResult.Ambiguous;
        }

        return ZoomWindowSelectionResult.Selected(candidates[0].Window.Handle);
    }

    private static bool IsCandidate(ZoomWindowDescription window)
    {
        var processName = Path.GetFileNameWithoutExtension(window.ProcessName);
        if (!processName.Equals("Zoom", StringComparison.OrdinalIgnoreCase) ||
            !window.IsVisible ||
            window.IsMinimized ||
            window.Width < 640 ||
            window.Height < 360)
        {
            return false;
        }

        var title = window.Title.Trim();
        if (title.Length == 0 || ExcludedTitleTerms.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !ExcludedClassTerms.Any(term => window.ClassName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(ZoomWindowDescription window)
    {
        var score = 0;
        if (window.Title.Contains("meeting", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (window.ClassName.Contains("content", StringComparison.OrdinalIgnoreCase)) score += 2;
        if (window.ClassName.StartsWith("ZP", StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }
}
