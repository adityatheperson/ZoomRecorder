using System.Net;

namespace ZoomRecorder.App.Cloud;

internal enum CloudErrorCode
{
    InvalidCredential,
    AccountRestricted,
    NetworkUnavailable,
    RateLimited,
    ServiceUnavailable,
    InvalidResponse
}

internal sealed class CloudProcessingException : Exception
{
    internal CloudProcessingException(CloudErrorCode code, string message) : base(message) => Code = code;
    internal CloudErrorCode Code { get; }
}

internal static class OpenAiErrorMapper
{
    internal static CloudProcessingException Map(HttpStatusCode statusCode) => (int)statusCode switch
    {
        401 => InvalidCredential(),
        402 or 403 => new CloudProcessingException(
            CloudErrorCode.AccountRestricted,
            "The OpenAI account cannot process this request. Check its billing and access settings, then try again."),
        408 => NetworkUnavailable(),
        429 => new CloudProcessingException(
            CloudErrorCode.RateLimited,
            "OpenAI is receiving too many requests. Wait a moment and try again."),
        >= 500 and <= 599 => ServiceUnavailable(),
        _ => InvalidResponse()
    };

    internal static CloudProcessingException InvalidCredential() => new(
        CloudErrorCode.InvalidCredential,
        "The OpenAI API key is missing or was not accepted. Update it in settings and try again.");

    internal static CloudProcessingException NetworkUnavailable() => new(
        CloudErrorCode.NetworkUnavailable,
        "The OpenAI request could not reach the service. Check the network connection and try again.");

    internal static CloudProcessingException ServiceUnavailable() => new(
        CloudErrorCode.ServiceUnavailable,
        "OpenAI is temporarily unavailable. Try again later.");

    internal static CloudProcessingException InvalidResponse() => new(
        CloudErrorCode.InvalidResponse,
        "OpenAI returned a response the application could not safely use. Try again later.");
}
