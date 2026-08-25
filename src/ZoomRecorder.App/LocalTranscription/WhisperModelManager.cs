using System.Security.Cryptography;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed record ModelDownloadProgress(long CompletedBytes, long TotalBytes);

internal interface IWhisperModelManager
{
    Task<string> EnsureModelAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class WhisperModelManager : IWhisperModelManager
{
    private readonly object _acquisitionLock = new();
    private readonly HttpClient _httpClient;
    private readonly WhisperModelManifest _manifest;
    private readonly string _modelsRoot;
    private SharedAcquisition? _sharedAcquisition;

    public WhisperModelManager(HttpClient httpClient, WhisperModelManifest manifest, string modelsRoot)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        _httpClient = httpClient;
        _manifest = manifest;
        _modelsRoot = Path.GetFullPath(modelsRoot);
    }

    public Task<string> EnsureModelAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SharedAcquisition acquisition;
        long callerId;
        lock (_acquisitionLock)
        {
            var startAcquisition = false;
            if (_sharedAcquisition is null || _sharedAcquisition.Task.IsCompleted)
            {
                _sharedAcquisition = new SharedAcquisition();
                startAcquisition = true;
            }

            acquisition = _sharedAcquisition;
            callerId = acquisition.AddCaller(progress);
            if (startAcquisition)
            {
                acquisition.Task = AcquireAsync(acquisition);
            }
        }

        return AwaitForCallerAsync(acquisition, callerId, cancellationToken);
    }

    private async Task<string> AwaitForCallerAsync(
        SharedAcquisition acquisition,
        long callerId,
        CancellationToken cancellationToken)
    {
        var callerRemoved = false;
        try
        {
            return await acquisition.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var canceledAcquisition = RemoveCaller(acquisition, callerId);
            callerRemoved = true;
            if (canceledAcquisition)
            {
                await WaitForAcquisitionCleanupAsync(acquisition.Task);
            }

            throw;
        }
        finally
        {
            if (!callerRemoved)
            {
                RemoveCaller(acquisition, callerId);
            }
        }
    }

    private async Task<string> AcquireAsync(SharedAcquisition acquisition)
    {
        var cancellationToken = acquisition.Cancellation.Token;
        var finalPath = LocalTranscriptionPaths.GetModelPath(_modelsRoot, _manifest.FileName);
        Directory.CreateDirectory(_modelsRoot);

        try
        {
            RemoveStalePartials(finalPath);
            if (File.Exists(finalPath))
            {
                if (await IsVerifiedAsync(finalPath, cancellationToken))
                {
                    return finalPath;
                }

                Quarantine(finalPath, finalPath);
            }

            return await DownloadVerifiedAsync(finalPath, acquisition, cancellationToken);
        }
        finally
        {
            lock (_acquisitionLock)
            {
                if (ReferenceEquals(_sharedAcquisition, acquisition))
                {
                    _sharedAcquisition = null;
                }
            }
        }
    }

    private async Task<string> DownloadVerifiedAsync(
        string finalPath,
        SharedAcquisition acquisition,
        CancellationToken cancellationToken)
    {
        var partialPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            string digest;
            long completedBytes = 0;
            using var response = await _httpClient.GetAsync(
                _manifest.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength > _manifest.ByteLength)
            {
                throw new InvalidDataException("The downloaded Whisper model exceeded its pinned size.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];

                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (completedBytes > _manifest.ByteLength - bytesRead)
                    {
                        throw new InvalidDataException("The downloaded Whisper model exceeded its pinned size.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hasher.AppendData(buffer, 0, bytesRead);
                    completedBytes += bytesRead;
                    ReportProgress(acquisition, new ModelDownloadProgress(completedBytes, _manifest.ByteLength));
                }

                await destination.FlushAsync(cancellationToken);
                digest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (completedBytes != _manifest.ByteLength ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(digest),
                    Convert.FromHexString(_manifest.Sha256)))
            {
                Quarantine(partialPath, finalPath);
                throw new InvalidDataException("The downloaded Whisper model did not match its pinned manifest.");
            }

            File.Move(partialPath, finalPath, overwrite: false);
            return finalPath;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private async Task<bool> IsVerifiedAsync(string modelPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            modelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long completedBytes = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, bytesRead);
            completedBytes += bytesRead;
        }

        var digest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return completedBytes == _manifest.ByteLength &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(_manifest.Sha256));
    }

    private static void Quarantine(string sourcePath, string finalPath)
    {
        var quarantinePath = finalPath + ".corrupt-" + Guid.NewGuid().ToString("N");
        File.Move(sourcePath, quarantinePath, overwrite: false);
    }

    private static void RemoveStalePartials(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        var prefix = Path.GetFileName(finalPath) + ".";
        foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(finalPath) + ".*.partial"))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private bool RemoveCaller(SharedAcquisition acquisition, long callerId)
    {
        lock (_acquisitionLock)
        {
            acquisition.RemoveCaller(callerId);
            if (acquisition.CallerCount == 0 && !acquisition.Task.IsCompleted)
            {
                acquisition.Cancellation.Cancel();
                return true;
            }

            return false;
        }
    }

    private static async Task WaitForAcquisitionCleanupAsync(Task acquisitionTask)
    {
        try
        {
            await acquisitionTask;
        }
        catch
        {
            // The caller's cancellation remains the observable result after shared cleanup finishes.
        }
    }

    private void ReportProgress(SharedAcquisition acquisition, ModelDownloadProgress progress)
    {
        IProgress<ModelDownloadProgress>[] observers;
        lock (_acquisitionLock)
        {
            observers = acquisition.GetProgressObservers();
        }

        foreach (var observer in observers)
        {
            observer.Report(progress);
        }
    }

    private sealed class SharedAcquisition
    {
        private readonly Dictionary<long, IProgress<ModelDownloadProgress>?> _callers = [];
        private long _nextCallerId;

        public CancellationTokenSource Cancellation { get; } = new();
        public Task<string> Task { get; set; } = null!;
        public int CallerCount => _callers.Count;

        public long AddCaller(IProgress<ModelDownloadProgress>? progress)
        {
            var callerId = _nextCallerId++;
            _callers.Add(callerId, progress);
            return callerId;
        }

        public void RemoveCaller(long callerId) => _callers.Remove(callerId);

        public IProgress<ModelDownloadProgress>[] GetProgressObservers() =>
            _callers.Values.OfType<IProgress<ModelDownloadProgress>>().ToArray();
    }
}
