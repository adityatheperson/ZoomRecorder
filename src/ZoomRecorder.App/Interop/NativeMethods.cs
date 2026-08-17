using System.Runtime.InteropServices;

namespace ZoomRecorder.App.Interop;

internal enum ZrResult { Ok, InvalidArgument, InvalidState, InternalError }

internal static partial class NativeMethods
{
    private const string Library = "ZoomRecorder.Native";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventCallback(nint json, nint context);

    [LibraryImport(Library)] internal static partial ZrResult zr_create(out nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_destroy(nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_set_event_callback(nint handle, EventCallback callback, nint context);
    [LibraryImport(Library)] internal static partial ZrResult zr_set_meeting_host(nint handle, nint windowHandle);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial ZrResult zr_prepare_meeting(nint handle, string requestJson);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)] internal static partial ZrResult zr_start_recording(nint handle, string outputPath);
    [LibraryImport(Library)] internal static partial ZrResult zr_enter_meeting(nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_finalize_recording(nint handle);
}
