using System.Runtime.InteropServices;

namespace ZoomRecorder.App.Interop;

internal enum ZrResult
{
    Ok,
    InvalidArgument,
    InvalidState,
    InternalError,
    Cancelled,
    AudioStreamMissing,
    MediaError,
    IoError
}

internal static partial class NativeMethods
{
    private const string Library = "ZoomRecorder.Native";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventCallback(nint json, nint context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ChunkCallback(nint chunk, nint context);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ZrAudioChunk
    {
        internal uint Index;
        internal nint Path;
        internal long StartMilliseconds;
        internal long EndMilliseconds;
        internal nint Sha256;
        internal ulong ByteSize;
        internal uint NormalizedSampleRate;
        internal uint EncodedSampleRate;
        internal uint ChannelCount;
    }

    [LibraryImport(Library)] internal static partial ZrResult zr_create(out nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_destroy(nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_set_event_callback(nint handle, EventCallback callback, nint context);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)] internal static partial ZrResult zr_start_recording(nint handle, string outputPath, nint meetingWindow);
    [LibraryImport(Library)] internal static partial ZrResult zr_finalize_recording(nint handle);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial ZrResult zr_prepare_audio_chunks(
        string mp4Path,
        string outputDirectory,
        ulong maxChunkBytes,
        ChunkCallback callback,
        nint context,
        nint* outHandle);
    [LibraryImport(Library)] internal static partial ZrResult zr_cancel_audio_preparation(nint handle);
    [LibraryImport(Library)] internal static partial ZrResult zr_destroy_audio_preparation(nint handle);
}
