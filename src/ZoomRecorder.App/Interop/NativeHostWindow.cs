using System.Runtime.InteropServices;

namespace ZoomRecorder.App.Interop;

internal sealed partial class NativeHostWindow : IDisposable
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    public nint Handle { get; }

    public NativeHostWindow(nint parent, int width, int height)
    {
        Handle = CreateWindowEx(0, "STATIC", null, WsChild | WsVisible, 0, 0, width, height, parent, 0, 0, 0);
        if (Handle == 0) throw new InvalidOperationException("The Zoom meeting host window could not be created.");
    }

    public void Resize(int width, int height) => MoveWindow(Handle, 0, 0, width, height, true);
    public void Dispose() { if (Handle != 0) DestroyWindow(Handle); }

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
