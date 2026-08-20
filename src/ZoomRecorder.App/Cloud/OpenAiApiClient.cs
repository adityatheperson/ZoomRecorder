using System.Net;
using System.Net.Http.Headers;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Cloud;

internal interface IOpenAiRetryScheduler
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class OpenAiRetryScheduler : IOpenAiRetryScheduler
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

internal sealed class OpenAiApiClient
{
    private readonly HttpClient http;
    private readonly ICredentialVault credentialVault;
    private readonly IOpenAiRetryScheduler scheduler;

    internal OpenAiApiClient(
        HttpClient http,
        ICredentialVault credentialVault,
        OpenAiOptions options,
        IOpenAiRetryScheduler? scheduler = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        this.scheduler = scheduler ?? new OpenAiRetryScheduler();
    }

    internal OpenAiOptions Options { get; }
    internal Uri Endpoint(string path) => new(Options.Endpoint, path);

    internal async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        cancellationToken.ThrowIfCancellationRequested();
        var apiKey = await credentialVault.GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw OpenAiErrorMapper.InvalidCredential();
        }

        AuthenticationHeaderValue authorization;
        try
        {
            authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        catch (FormatException)
        {
            throw OpenAiErrorMapper.InvalidCredential();
        }

        for (var attempt = 0; attempt < Options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = requestFactory() ??
                throw new InvalidOperationException("The OpenAI request factory returned no request.");
            request.Headers.Authorization = authorization;

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw OpenAiErrorMapper.NetworkUnavailable();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw OpenAiErrorMapper.NetworkUnavailable();
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = response.StatusCode;
            if (IsRetryable(statusCode) && attempt + 1 < Options.MaxAttempts)
            {
                var delay = RetryDelay(response.Headers.RetryAfter, attempt);
                response.Dispose();
                await scheduler.DelayAsync(delay, cancellationToken);
                continue;
            }

            response.Dispose();
            throw OpenAiErrorMapper.Map(statusCode);
        }

        throw OpenAiErrorMapper.ServiceUnavailable();
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode is >= 500 and <= 599;

    private TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return Cap(delta < TimeSpan.Zero ? TimeSpan.Zero : delta);
        }
        if (retryAfter?.Date is { } date)
        {
            var deltaFromClock = date - scheduler.UtcNow;
            return Cap(deltaFromClock < TimeSpan.Zero ? TimeSpan.Zero : deltaFromClock);
        }
        return ExponentialDelay(attempt);
    }

    private TimeSpan ExponentialDelay(int attempt)
    {
        var milliseconds = Options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, Options.MaxRetryDelay.TotalMilliseconds));
    }

    private TimeSpan Cap(TimeSpan delay) => delay > Options.MaxRetryDelay ? Options.MaxRetryDelay : delay;
}
