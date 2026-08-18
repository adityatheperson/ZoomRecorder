using ZoomRecorder.App.Services;

namespace ZoomRecorder.App.Tests;

public sealed class NativeJoinFlowTests
{
    [Theory]
    [InlineData("{\"type\":\"meeting_ended\"}")]
    [InlineData("{\"type\":\"capture_ended\"}")]
    public void Meeting_or_capture_end_requests_finalization(string json)
    {
        Assert.True(NativeJoinFlow.ShouldFinalize(json));
    }

    [Fact]
    public void Ordinary_native_event_does_not_request_finalization()
    {
        Assert.False(NativeJoinFlow.ShouldFinalize("{\"type\":\"component_ready\"}"));
    }
}
