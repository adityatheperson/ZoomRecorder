using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZoomRecorder.App.Interop;

public static class MeetingSdkJwtFactory
{
    public static string Create(string clientId, string clientSecret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var issuedAt = now.ToUnixTimeSeconds() - 30;
        var expiresAt = issuedAt + (2 * 60 * 60);
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { appKey = clientId, iat = issuedAt, exp = expiresAt, tokenExp = expiresAt }));
        var unsignedToken = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(clientSecret));
        return $"{unsignedToken}.{Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken)))}";
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
