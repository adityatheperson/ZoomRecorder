using System.Runtime.InteropServices;

namespace ZoomRecorder.App.Interop;

internal interface IAudioChunkNativeApi
{
    ZrResult Prepare(
        string mp4Path,
        string outputDirectory,
        ulong maxBytes,
        NativeMethods.ChunkCallback callback,
        nint context,
        NativePreparationHandleSlot handleSlot);

    ZrResult Cancel(nint handle);

    ZrResult Destroy(nint handle);
}

internal sealed class NativePreparationHandleSlot : IDisposable
{
    private nint storage = Marshal.AllocHGlobal(nint.Size);

    internal NativePreparationHandleSlot() => Marshal.WriteIntPtr(storage, nint.Zero);

    internal nint Storage => storage != nint.Zero
        ? storage
        : throw new ObjectDisposedException(nameof(NativePreparationHandleSlot));

    internal nint Handle
    {
        get
        {
            Thread.MemoryBarrier();
            return storage == nint.Zero ? nint.Zero : Marshal.ReadIntPtr(storage);
        }
    }

    internal void Publish(nint handle)
    {
        Marshal.WriteIntPtr(Storage, handle);
        Thread.MemoryBarrier();
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref storage, nint.Zero);
        if (value != nint.Zero)
        {
            Marshal.FreeHGlobal(value);
        }
    }
}

internal sealed class AudioChunkNativeApi : IAudioChunkNativeApi
{
    public unsafe ZrResult Prepare(
        string mp4Path,
        string outputDirectory,
        ulong maxBytes,
        NativeMethods.ChunkCallback callback,
        nint context,
        NativePreparationHandleSlot handleSlot) =>
        NativeMethods.zr_prepare_audio_chunks(
            mp4Path,
            outputDirectory,
            maxBytes,
            callback,
            context,
            (nint*)handleSlot.Storage);

    public ZrResult Cancel(nint handle) => NativeMethods.zr_cancel_audio_preparation(handle);

    public ZrResult Destroy(nint handle) => NativeMethods.zr_destroy_audio_preparation(handle);
}
