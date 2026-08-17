using System.Runtime.InteropServices;
using System.Text.Json;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.Interop;

internal sealed class NativeSession : IDisposable
{
    private readonly SafeZrHandle handle;
    private readonly NativeMethods.EventCallback callback;
    public event EventHandler<string>? NativeEvent;

    public NativeSession()
    {
        ThrowIfFailed(NativeMethods.zr_create(out var value));
        handle = new SafeZrHandle(value);
        callback = OnNativeEvent;
        ThrowIfFailed(NativeMethods.zr_set_event_callback(handle.DangerousGetHandle(), callback, nint.Zero));
    }

    public void Prepare(MeetingJoinRequest request) => ThrowIfFailed(NativeMethods.zr_prepare_meeting(handle.DangerousGetHandle(), JsonSerializer.Serialize(request)));
    public void StartRecording(string path) => ThrowIfFailed(NativeMethods.zr_start_recording(handle.DangerousGetHandle(), path));
    public void Enter() => ThrowIfFailed(NativeMethods.zr_enter_meeting(handle.DangerousGetHandle()));
    public void FinalizeRecording() => ThrowIfFailed(NativeMethods.zr_finalize_recording(handle.DangerousGetHandle()));
    public void Dispose() => handle.Dispose();

    private void OnNativeEvent(nint json, nint _) => NativeEvent?.Invoke(this, Marshal.PtrToStringUTF8(json) ?? "{}");
    private static void ThrowIfFailed(ZrResult result) { if (result != ZrResult.Ok) throw new InvalidOperationException($"Native operation failed: {result}."); }
}
