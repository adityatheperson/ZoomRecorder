using System.Diagnostics;
using System.Text;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed class WhisperWorkerRunner : IWhisperWorkerRunner
{
    private const int MaximumDiagnosticCharacters = 16 * 1024;
    private static readonly string[] RecognizedGpuInitializationDiagnostics =
    [
        "ggml_vulkan: failed to initialize vulkan",
        "vulkan initialization failed",
        "failed to create vulkan instance",
        "failed to create vulkan device",
        "vkcreateinstance failed",
        "vkcreatedevice failed"
    ];

    private readonly string _cpuWorkerPath;
    private readonly string _gpuWorkerPath;

    public WhisperWorkerRunner(string gpuWorkerPath, string cpuWorkerPath)
    {
        _gpuWorkerPath = CanonicalAbsolutePath(gpuWorkerPath, nameof(gpuWorkerPath));
        _cpuWorkerPath = CanonicalAbsolutePath(cpuWorkerPath, nameof(cpuWorkerPath));
    }

    public async Task<WhisperWorkerResult> RunAsync(
        WhisperWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = ValidateRequest(request);
        using var publication = new PublicationGate(cancellationToken);
        var ownedAttemptJsonPaths = new List<string>();

        try
        {
            publication.ThrowIfCancellationAccepted();
            var gpuOutputBasePath = CreateAttemptOutputBase(paths.OutputBasePath, "gpu");
            ownedAttemptJsonPaths.Add(gpuOutputBasePath + ".json");
            var gpu = await RunWorkerAsync(
                _gpuWorkerPath,
                paths,
                gpuOutputBasePath,
                cancellationToken);

            if (gpu.Succeeded)
            {
                return Publish(gpu.JsonPath, paths.JsonPath, UsedCpuFallback: false, publication);
            }

            DeleteOwnedAttempt(gpu.JsonPath);
            publication.ThrowIfCancellationAccepted();
            if (!gpu.AllowsCpuFallback)
            {
                throw RuntimeFailure();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var cpuOutputBasePath = CreateAttemptOutputBase(paths.OutputBasePath, "cpu");
            ownedAttemptJsonPaths.Add(cpuOutputBasePath + ".json");
            var cpu = await RunWorkerAsync(
                _cpuWorkerPath,
                paths,
                cpuOutputBasePath,
                cancellationToken);

            if (cpu.Succeeded)
            {
                return Publish(cpu.JsonPath, paths.JsonPath, UsedCpuFallback: true, publication);
            }

            DeleteOwnedAttempt(cpu.JsonPath);
            throw RuntimeFailure();
        }
        catch (OperationCanceledException) when (publication.CancellationAccepted)
        {
            DeleteOwnedAttempts(ownedAttemptJsonPaths);
            throw;
        }
        catch
        {
            DeleteOwnedAttempts(ownedAttemptJsonPaths);
            throw;
        }
    }

    private static async Task<WorkerAttempt> RunWorkerAsync(
        string workerPath,
        ValidatedPaths paths,
        string attemptOutputBasePath,
        CancellationToken cancellationToken)
    {
        var jsonPath = attemptOutputBasePath + ".json";
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("--model");
        arguments.Add(paths.ModelPath);
        arguments.Add("--file");
        arguments.Add(paths.WavPath);
        arguments.Add("--language");
        arguments.Add("en");
        arguments.Add("--output-json-full");
        arguments.Add("--output-file");
        arguments.Add(attemptOutputBasePath);
        arguments.Add("--no-prints");

        cancellationToken.ThrowIfCancellationRequested();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return WorkerAttempt.StartFailed(jsonPath);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return WorkerAttempt.StartFailed(jsonPath);
        }

        using (process)
        {
            var stderrTask = ReadBoundedAsync(process.StandardError, MaximumDiagnosticCharacters);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumDiagnosticCharacters);
            using var cancellationRegistration = cancellationToken.Register(
                static state => KillProcessTree((Process)state!), process);
            try
            {
                await process.WaitForExitAsync();
                await Task.WhenAll(stderrTask, stdoutTask);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillProcessTree(process);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stderrTask, stdoutTask);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                return WorkerAttempt.Failed(jsonPath, IsRecognizedGpuInitializationFailure(stderr.Value));
            }

            return File.Exists(jsonPath)
                ? WorkerAttempt.Success(jsonPath)
                : WorkerAttempt.MissingJson(jsonPath, IsRecognizedGpuInitializationFailure(stderr.Value));
        }
    }

    private static WhisperWorkerResult Publish(
        string attemptJsonPath,
        string finalJsonPath,
        bool UsedCpuFallback,
        PublicationGate publication)
    {
        publication.BeginCommit();
        try
        {
            File.Move(attemptJsonPath, finalJsonPath, overwrite: false);
        }
        catch (IOException)
        {
            throw RuntimeFailure();
        }
        catch (UnauthorizedAccessException)
        {
            throw RuntimeFailure();
        }

        publication.MarkCommitted();
        return new WhisperWorkerResult(finalJsonPath, UsedCpuFallback);
    }

    private static ValidatedPaths ValidateRequest(WhisperWorkerRequest request)
    {
        var modelPath = CanonicalAbsolutePath(request.ModelPath, nameof(request.ModelPath));
        var wavPath = CanonicalAbsolutePath(request.WavPath, nameof(request.WavPath));
        var outputBasePath = CanonicalAbsolutePath(request.OutputBasePath, nameof(request.OutputBasePath));
        var wavDirectory = Path.GetDirectoryName(wavPath)!;
        var outputDirectory = Path.GetDirectoryName(outputBasePath)!;

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The verified Whisper model does not exist.", modelPath);
        }
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException("The PCM WAV input does not exist.", wavPath);
        }
        if (!string.Equals(wavDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The transient Whisper output path escapes the WAV job directory.");
        }

        return new ValidatedPaths(modelPath, wavPath, outputBasePath, outputBasePath + ".json");
    }

    private static string CreateAttemptOutputBase(string finalOutputBasePath, string workerKind)
    {
        var directory = Path.GetDirectoryName(finalOutputBasePath)!;
        var leaf = Path.GetFileName(finalOutputBasePath);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var outputBasePath = Path.Combine(directory, $".{leaf}.{workerKind}.{Guid.NewGuid():N}");
            if (!File.Exists(outputBasePath) && !File.Exists(outputBasePath + ".json"))
            {
                return outputBasePath;
            }
        }

        throw RuntimeFailure();
    }

    private static string CanonicalAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The local Whisper path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static bool IsRecognizedGpuInitializationFailure(string diagnostics) =>
        RecognizedGpuInitializationDiagnostics.Any(category =>
            diagnostics.Contains(category, StringComparison.OrdinalIgnoreCase));

    private static async Task<BoundedOutput> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                return new BoundedOutput(output.ToString(), truncated);
            }

            var remaining = maximumCharacters - output.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            var copied = Math.Min(remaining, read);
            output.Append(buffer, 0, copied);
            truncated |= copied != read;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The worker exited between the state check and tree termination.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The worker exited before Windows accepted the termination request.
        }
    }

    private static void DeleteOwnedAttempts(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            DeleteOwnedAttempt(path);
        }
    }

    private static void DeleteOwnedAttempt(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                throw RuntimeFailure();
            }
            catch (UnauthorizedAccessException)
            {
                throw RuntimeFailure();
            }
        }
    }

    private static ProcessingOperationException RuntimeFailure() =>
        new(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed);

    private sealed record ValidatedPaths(
        string ModelPath,
        string WavPath,
        string OutputBasePath,
        string JsonPath);

    private sealed record BoundedOutput(string Value, bool Truncated);

    private sealed record WorkerAttempt(string JsonPath, bool Succeeded, bool AllowsCpuFallback)
    {
        internal static WorkerAttempt Success(string jsonPath) => new(jsonPath, true, false);
        internal static WorkerAttempt StartFailed(string jsonPath) => new(jsonPath, false, true);
        internal static WorkerAttempt MissingJson(string jsonPath, bool allowsCpuFallback) =>
            new(jsonPath, false, allowsCpuFallback);
        internal static WorkerAttempt Failed(string jsonPath, bool allowsCpuFallback) =>
            new(jsonPath, false, allowsCpuFallback);
    }

    private sealed class PublicationGate : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private readonly object _sync = new();
        private bool _cancellationAccepted;
        private bool _commitStarted;
        private bool _committed;

        internal PublicationGate(CancellationToken cancellationToken) =>
            _registration = cancellationToken.Register(static state => ((PublicationGate)state!).AcceptCancellation(), this);

        internal bool CancellationAccepted
        {
            get
            {
                lock (_sync)
                {
                    return _cancellationAccepted;
                }
            }
        }

        internal void ThrowIfCancellationAccepted()
        {
            lock (_sync)
            {
                if (_cancellationAccepted)
                {
                    throw new OperationCanceledException();
                }
            }
        }

        internal void BeginCommit()
        {
            lock (_sync)
            {
                if (_cancellationAccepted)
                {
                    throw new OperationCanceledException();
                }

                _commitStarted = true;
            }
        }

        internal void MarkCommitted()
        {
            lock (_sync)
            {
                _committed = true;
            }
        }

        private void AcceptCancellation()
        {
            lock (_sync)
            {
                if (!_commitStarted && !_committed)
                {
                    _cancellationAccepted = true;
                }
            }
        }

        public void Dispose() => _registration.Dispose();
    }
}
