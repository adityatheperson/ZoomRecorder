using System.Text.Json;

namespace ZoomRecorder.App.Interop;

internal sealed class MeetingEntryAwaiter(TimeSpan timeout)
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Observe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var type)) return;

            switch (type.GetString())
            {
                case "meeting_window_ready":
                    completion.TrySetResult();
                    break;
                case "failed":
                    var detail = document.RootElement.TryGetProperty("message", out var message)
                        ? message.GetString()
                        : document.RootElement.TryGetProperty("component", out var component)
                            ? component.GetString()
                            : null;
                    completion.TrySetException(new InvalidOperationException(
                        $"Zoom could not open the meeting window: {detail ?? "unknown SDK error"}."));
                    break;
            }
        }
        catch (JsonException) { }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "Zoom joined but its meeting window did not appear within 30 seconds.", exception);
        }
    }
}
