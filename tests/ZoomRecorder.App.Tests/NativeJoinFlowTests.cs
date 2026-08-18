using ZoomRecorder.App.Services;

namespace ZoomRecorder.App.Tests;

public sealed class NativeJoinFlowTests
{
    [Fact]
    public void Finalization_gate_can_begin_only_once()
    {
        var gate = new FinalizationGate();

        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());
        gate.Reset();
        Assert.True(gate.TryBegin());
    }

    [Fact]
    public void Join_flow_exposes_manual_stop_and_save()
    {
        Assert.NotNull(typeof(NativeJoinFlow).GetMethod(nameof(NativeJoinFlow.StopAndSaveAsync)));
    }

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
