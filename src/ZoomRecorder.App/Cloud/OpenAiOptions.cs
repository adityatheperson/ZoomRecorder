namespace ZoomRecorder.App.Cloud;

internal sealed record OpenAiOptions
{
    internal Uri Endpoint { get; init; } = new("https://api.openai.com", UriKind.Absolute);
    internal string TranscriptionModel { get; init; } = "gpt-transcribe";
    internal string StudyModel { get; init; } = "gpt-5.6-luna";
    internal int MaxAttempts { get; init; } = 3;
    internal TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The OpenAI endpoint must be an absolute HTTPS URI.", nameof(Endpoint));
        }
        if (!string.IsNullOrEmpty(Endpoint.UserInfo))
        {
            throw new ArgumentException("The OpenAI endpoint cannot contain user information.", nameof(Endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(TranscriptionModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(StudyModel);
        if (MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "At least one OpenAI request attempt is required.");
        }
        if (InitialRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay), "The initial retry delay cannot be negative.");
        }
        if (MaxRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), "The maximum retry delay must be positive.");
        }
    }
}
