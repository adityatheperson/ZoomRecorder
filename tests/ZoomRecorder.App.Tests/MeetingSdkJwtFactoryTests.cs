using System.Text;
using System.Text.Json;
using ZoomRecorder.App.Interop;

namespace ZoomRecorder.App.Tests;

public sealed class MeetingSdkJwtFactoryTests
{
    [Fact]
    public void Create_produces_native_sdk_claims_without_secret()
    {
        var token = MeetingSdkJwtFactory.Create("client-id", "do-not-leak", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("client-id", payload.RootElement.GetProperty("appKey").GetString());
        Assert.Equal(1_699_999_970, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(1_700_007_170, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.DoesNotContain("do-not-leak", token);
    }

    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '='));
    }
}
