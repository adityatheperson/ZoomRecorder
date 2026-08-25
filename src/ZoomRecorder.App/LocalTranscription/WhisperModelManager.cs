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
    private Task<string>? _sharedAcquisition;

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

        lock (_acquisitionLock)
        {
            if (_sharedAcquisition is null || _sharedAcquisition.IsCompleted)
            {
                _sharedAcquisition = AcquireAsync(progress, cancellationToken);
            }

            return _sharedAcquisition;
        }
    }

    private async Task<string> AcquireAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var finalPath = LocalTranscriptionPaths.GetModelPath(_modelsRoot, _manifest.FileName);
        Directory.CreateDirectory(_modelsRoot);

        try
        {
            if (File.Exists(finalPath))
            {
                if (await IsVerifiedAsync(finalPath, cancellationToken))
                {
                    return finalPath;
                }

                Quarantine(finalPath, finalPath);
            }

            return await DownloadVerifiedAsync(finalPath, progress, cancellationToken);
        }
        finally
        {
            lock (_acquisitionLock)
            {
                _sharedAcquisition = null;
            }
        }
    }

    private async Task<string> DownloadVerifiedAsync(
        string finalPath,
        IProgress<ModelDownloadProgress>? progress,
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

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hasher.AppendData(buffer, 0, bytesRead);
                    completedBytes += bytesRead;
                    progress?.Report(new ModelDownloadProgress(completedBytes, _manifest.ByteLength));
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
}
