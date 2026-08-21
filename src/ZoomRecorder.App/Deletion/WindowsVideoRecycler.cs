using Microsoft.VisualBasic.FileIO;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Deletion;

internal interface IRecycleShell
{
    void Delete(string path, bool sendToRecycleBin);
}

internal sealed class VisualBasicRecycleShell : IRecycleShell
{
    public void Delete(string path, bool sendToRecycleBin) => FileSystem.DeleteFile(
        path,
        UIOption.OnlyErrorDialogs,
        sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
}

internal sealed class WindowsVideoRecycler : IVideoRecycler
{
    private readonly IRecycleShell shell;
    private readonly Func<string, bool> exists;

    public WindowsVideoRecycler() : this(new VisualBasicRecycleShell(), File.Exists) { }

    internal WindowsVideoRecycler(IRecycleShell shell, Func<string, bool> exists)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.exists = exists ?? throw new ArgumentNullException(nameof(exists));
    }

    public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = Path.GetFullPath(path);
        if (!exists(canonicalPath)) return Task.FromResult(new RecycleResult(false, null));

        try
        {
            shell.Delete(canonicalPath, sendToRecycleBin: true);
            return Task.FromResult(new RecycleResult(true, canonicalPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          PlatformNotSupportedException or NotSupportedException)
        {
            return Task.FromResult(new RecycleResult(false, null));
        }
    }
}
