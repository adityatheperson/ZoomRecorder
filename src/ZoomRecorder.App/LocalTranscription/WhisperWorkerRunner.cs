using System.Diagnostics;
using System.Text;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed class WhisperWorkerRunner : IWhisperWorkerRunner
{
    private const int MaximumDiagnosticCharacters = 16 * 1024;
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
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(paths.JsonPath))
        {
            throw new IOException("The transient Whisper JSON output path is already in use.");
        }

        var outputMayBeOwned = false;
        try
        {
            outputMayBeOwned = true;
            var gpu = await RunWorkerAsync(_gpuWorkerPath, paths, cancellationToken);
            if (gpu.Succeeded)
            {
                return new WhisperWorkerResult(paths.JsonPath, UsedCpuFallback: false);
            }

            DeleteTransient(paths.JsonPath);
            var cpu = await RunWorkerAsync(_cpuWorkerPath, paths, cancellationToken);
            if (cpu.Succeeded)
            {
                return new WhisperWorkerResult(paths.JsonPath, UsedCpuFallback: true);
            }

            throw RuntimeFailure(gpu, cpu);
        }
        catch
        {
            if (outputMayBeOwned)
            {
                DeleteTransient(paths.JsonPath);
            }

            throw;
        }
    }

    private static async Task<WorkerAttempt> RunWorkerAsync(
        string workerPath,
        ValidatedPaths paths,
        CancellationToken cancellationToken)
    {
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
        arguments.Add(paths.OutputBasePath);
        arguments.Add("--no-prints");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return WorkerAttempt.StartFailed();
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return WorkerAttempt.StartFailed();
        }

        using (process)
        {
            var stderrTask = ReadBoundedAsync(process.StandardError, MaximumDiagnosticCharacters);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumDiagnosticCharacters);
            using var registration = cancellationToken.Register(
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
                return WorkerAttempt.NonzeroExit(process.ExitCode, stderr.Truncated);
            }

            return File.Exists(paths.JsonPath)
                ? WorkerAttempt.Success()
                : WorkerAttempt.MissingJson(stderr.Truncated);
        }
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

    private static string CanonicalAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The local Whisper path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

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

    private static void DeleteTransient(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Recovery cleanup retries the unique transient output path.
        }
        catch (UnauthorizedAccessException)
        {
            // Recovery cleanup retries the unique transient output path.
        }
    }

    private static InvalidOperationException RuntimeFailure(WorkerAttempt gpu, WorkerAttempt cpu) =>
        new($"Whisper worker runtime failed. GPU: {gpu.Diagnostic}; CPU: {cpu.Diagnostic}.");

    private sealed record ValidatedPaths(
        string ModelPath,
        string WavPath,
        string OutputBasePath,
        string JsonPath);

    private sealed record BoundedOutput(string Value, bool Truncated);

    private sealed record WorkerAttempt(string Diagnostic, bool Succeeded)
    {
        internal static WorkerAttempt Success() => new("success", true);
        internal static WorkerAttempt StartFailed() => new("start-failed (exit code unavailable)", false);
        internal static WorkerAttempt MissingJson(bool stderrTruncated) =>
            new($"missing-json (exit code 0{TruncationSuffix(stderrTruncated)})", false);
        internal static WorkerAttempt NonzeroExit(int exitCode, bool stderrTruncated) =>
            new($"nonzero-exit (exit code {exitCode}{TruncationSuffix(stderrTruncated)})", false);

        private static string TruncationSuffix(bool truncated) => truncated ? ", stderr-truncated" : string.Empty;
    }
}
