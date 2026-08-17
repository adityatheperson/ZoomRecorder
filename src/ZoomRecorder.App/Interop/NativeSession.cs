using System.Runtime.InteropServices;
using System.Text.Json;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.Interop;

internal sealed class NativeSession : IDisposable
{
    private readonly SafeZrHandle handle;
    private readonly NativeMethods.EventCallback callback;
    private string? lastNativeError;
    public event EventHandler<string>? NativeEvent;

    public NativeSession()
    {
        ThrowIfFailed(NativeMethods.zr_create(out var value), "create native session");
        handle = new SafeZrHandle(value);
        callback = OnNativeEvent;
        ThrowIfFailed(NativeMethods.zr_set_event_callback(handle.DangerousGetHandle(), callback, nint.Zero), "register native events");
    }

    public void Prepare(MeetingJoinRequest request)
    {
        var clientId = Environment.GetEnvironmentVariable("ZOOM_CLIENT_ID", EnvironmentVariableTarget.User);
        var clientSecret = Environment.GetEnvironmentVariable("ZOOM_CLIENT_SECRET", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Zoom Meeting SDK credentials are not configured.");
        var jwt = MeetingSdkJwtFactory.Create(clientId, clientSecret, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Serialize(new { request.MeetingId, request.Passcode, request.DisplayName, Jwt = jwt });
        lastNativeError = null;
        ThrowIfFailed(NativeMethods.zr_prepare_meeting(handle.DangerousGetHandle(), payload), "prepare Zoom meeting");
    }
    public void SetMeetingHost(nint windowHandle) => ThrowIfFailed(NativeMethods.zr_set_meeting_host(handle.DangerousGetHandle(), windowHandle), "set meeting capture area");
    public void StartRecording(string path) { lastNativeError = null; ThrowIfFailed(NativeMethods.zr_start_recording(handle.DangerousGetHandle(), path), "start recording"); }
    public void Enter() => ThrowIfFailed(NativeMethods.zr_enter_meeting(handle.DangerousGetHandle()), "enter Zoom meeting");
    public void FinalizeRecording() => ThrowIfFailed(NativeMethods.zr_finalize_recording(handle.DangerousGetHandle()), "finalize recording");
    public void Dispose() => handle.Dispose();

    private void OnNativeEvent(nint json, nint _)
    {
        var text = Marshal.PtrToStringUTF8(json) ?? "{}";
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "failed")
                lastNativeError = document.RootElement.TryGetProperty("message", out var message) ? message.GetString() :
                    document.RootElement.TryGetProperty("component", out var component) ? component.GetString() : null;
        }
        catch (JsonException) { }
        NativeEvent?.Invoke(this, text);
    }
    private void ThrowIfFailed(ZrResult result, string operation)
    {
        if (result != ZrResult.Ok)
            throw new InvalidOperationException(lastNativeError is null ? $"Could not {operation}: {result}." : $"Could not {operation}: {lastNativeError}.");
    }
}
