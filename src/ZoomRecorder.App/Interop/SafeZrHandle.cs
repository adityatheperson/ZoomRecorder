using Microsoft.Win32.SafeHandles;

namespace ZoomRecorder.App.Interop;

internal sealed class SafeZrHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeZrHandle() : base(true) { }
    internal SafeZrHandle(nint handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() => NativeMethods.zr_destroy(handle) == ZrResult.Ok;
}
