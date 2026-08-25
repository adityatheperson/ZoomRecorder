using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.LocalTranscription;

public sealed class WhisperWorkerRunnerTests
{
    [Fact]
    public async Task Gpu_success_publishes_the_expected_json_without_cpu_fallback()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.Success);
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);

        var result = await files.CreateRunner().RunAsync(files.Request, CancellationToken.None);

        Assert.Equal(files.JsonPath, result.JsonPath);
        Assert.False(result.UsedCpuFallback);
        Assert.True(File.Exists(result.JsonPath));
        Assert.False(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Recognized_vulkan_initialization_failure_retries_once_with_cpu_and_reports_fallback()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerFiles.GpuInitializationFail(17));
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);

        var result = await files.CreateRunner().RunAsync(files.Request, CancellationToken.None);

        Assert.Equal(files.JsonPath, result.JsonPath);
        Assert.True(result.UsedCpuFallback);
        Assert.True(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Ordinary_gpu_failure_does_not_launch_cpu_and_throws_the_local_runtime_error()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerFiles.Fail(17));
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);

        var exception = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            files.CreateRunner().RunAsync(files.Request, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed, exception.Code);
        Assert.False(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Cancellation_kills_the_worker_process_tree_before_returning()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.SlowWithChild);
        files.WriteWorker("cpu.cmd", WorkerBehavior.Success);
        using var cancellation = new CancellationTokenSource();

        var operation = files.CreateRunner().RunAsync(files.Request, cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(files.ReadyPath), TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(File.Exists(files.OrphanMarkerPath));
        Assert.False(File.Exists(files.JsonPath));
        Assert.Empty(Directory.GetFiles(files.JobDirectory, ".*.json"));
    }

    [Fact]
    public async Task Missing_gpu_output_without_gpu_initialization_does_not_launch_cpu()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.ExitWithoutJson);
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);

        var exception = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            files.CreateRunner().RunAsync(files.Request, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed, exception.Code);
        Assert.False(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Gpu_process_start_failure_retries_once_with_cpu()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);

        var result = await files.CreateRunner().RunAsync(files.Request, CancellationToken.None);

        Assert.True(result.UsedCpuFallback);
        Assert.True(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Foreign_final_output_created_while_the_worker_runs_is_preserved_and_not_overwritten()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.DelayedSuccess);
        files.WriteWorker("cpu.cmd", WorkerBehavior.SuccessWithMarker);
        var operation = files.CreateRunner().RunAsync(files.Request, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(files.ReadyPath), TimeSpan.FromSeconds(5)));
        File.WriteAllText(files.JsonPath, "foreign output");

        var exception = await Assert.ThrowsAsync<ProcessingOperationException>(() => operation);

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed, exception.Code);
        Assert.Equal("foreign output", File.ReadAllText(files.JsonPath));
        Assert.False(File.Exists(files.CpuMarkerPath));
    }

    [Fact]
    public async Task Rejects_relative_paths_before_starting_any_worker()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.Success);
        files.WriteWorker("cpu.cmd", WorkerBehavior.Success);
        var relativeRequest = files.Request with { ModelPath = "model.bin" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            files.CreateRunner().RunAsync(relativeRequest, CancellationToken.None));

        Assert.False(File.Exists(files.JsonPath));
    }

    [Fact]
    public async Task Rejects_an_output_base_outside_the_wav_job_directory()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.Success);
        files.WriteWorker("cpu.cmd", WorkerBehavior.Success);
        var outsideBase = Path.Combine(files.Root, "outside", "worker-output");
        var request = files.Request with { OutputBasePath = outsideBase };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            files.CreateRunner().RunAsync(request, CancellationToken.None));

        Assert.False(File.Exists(outsideBase + ".json"));
    }

    [Fact]
    public async Task Rejects_and_preserves_an_existing_json_output_collision()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerBehavior.Success);
        files.WriteWorker("cpu.cmd", WorkerBehavior.Success);
        File.WriteAllText(files.JsonPath, "foreign output");

        var exception = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            files.CreateRunner().RunAsync(files.Request, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed, exception.Code);
        Assert.Equal("foreign output", File.ReadAllText(files.JsonPath));
    }

    [Fact]
    public async Task Runtime_diagnostics_report_stderr_truncation_without_retaining_unbounded_stderr()
    {
        using var files = new WorkerFiles();
        files.WriteWorker("gpu.cmd", WorkerFiles.NoisyFail(31));
        files.WriteWorker("cpu.cmd", WorkerFiles.NoisyFail(37));

        var exception = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            files.CreateRunner().RunAsync(files.Request, CancellationToken.None));

        Assert.Equal(CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed, exception.Code);
        Assert.DoesNotContain(new string('x', 16_385), exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length < 200);
    }

    private enum WorkerBehavior
    {
        Success,
        SuccessWithMarker,
        ExitWithoutJson,
        SlowWithChild,
        DelayedSuccess
    }

    private sealed class WorkerFiles : IDisposable
    {
        internal WorkerFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zoom-recorder-whisper-worker-{Guid.NewGuid():N}");
            JobDirectory = Path.Combine(Root, "job");
            Directory.CreateDirectory(JobDirectory);
            ModelPath = Path.Combine(Root, "model.bin");
            WavPath = Path.Combine(JobDirectory, "audio.wav");
            OutputBasePath = Path.Combine(JobDirectory, "transcript");
            JsonPath = OutputBasePath + ".json";
            CpuMarkerPath = Path.Combine(Root, "cpu-ran");
            ReadyPath = Path.Combine(Root, "ready");
            OrphanMarkerPath = Path.Combine(Root, "orphan");
            File.WriteAllText(ModelPath, "model");
            File.WriteAllText(WavPath, "wav");
        }

        internal string Root { get; }
        internal string JobDirectory { get; }
        internal string ModelPath { get; }
        internal string WavPath { get; }
        internal string OutputBasePath { get; }
        internal string JsonPath { get; }
        internal string CpuMarkerPath { get; }
        internal string ReadyPath { get; }
        internal string OrphanMarkerPath { get; }
        internal string GpuWorkerPath => Path.Combine(Root, "gpu.cmd");
        internal string CpuWorkerPath => Path.Combine(Root, "cpu.cmd");
        internal WhisperWorkerRequest Request => new(ModelPath, WavPath, OutputBasePath);

        internal WhisperWorkerRunner CreateRunner() => new(GpuWorkerPath, CpuWorkerPath);

        internal void WriteWorker(string name, WorkerBehavior behavior)
        {
            var script = behavior switch
            {
                WorkerBehavior.Success => SuccessScript(marker: null),
                WorkerBehavior.SuccessWithMarker => SuccessScript(CpuMarkerPath),
                WorkerBehavior.ExitWithoutJson => "@echo off\r\nexit /b 0\r\n",
                WorkerBehavior.SlowWithChild => SlowScript(),
                WorkerBehavior.DelayedSuccess => DelayedSuccessScript(),
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
            File.WriteAllText(Path.Combine(Root, name), script);
        }

        internal void WriteWorker(string name, FailedWorkerBehavior behavior) =>
            File.WriteAllText(Path.Combine(Root, name), FailureScript(behavior.ExitCode, behavior.Noisy, behavior.GpuInitializationFailure));

        internal static FailedWorkerBehavior Fail(int exitCode) => new(exitCode, false);
        internal static FailedWorkerBehavior NoisyFail(int exitCode) => new(exitCode, true);
        internal static FailedWorkerBehavior GpuInitializationFail(int exitCode) => new(exitCode, false, true);

        private string SuccessScript(string? marker) =>
            $"@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\nset \"out=\"\r\n" +
            $":next\r\nif \"%~1\"==\"\" goto done\r\nif /I \"%~1\"==\"--output-file\" set \"out=%~2\"\r\n" +
            $"shift\r\ngoto next\r\n:done\r\n> \"%out%.json\" echo {{\"result\":\"ok\"}}\r\n" +
            $"{MarkerLine(marker)}\r\nexit /b 0\r\n";

        private string SlowScript() =>
            $"@echo off\r\nstart \"\" /b cmd.exe /d /c \"timeout /t 3 /nobreak >nul & > \\\"{OrphanMarkerPath}\\\" echo orphan\"\r\n" +
            $"> \"{ReadyPath}\" echo ready\r\ntimeout /t 30 /nobreak >nul\r\nexit /b 0\r\n";

        private string DelayedSuccessScript() =>
            $"@echo off\r\nset \"out=\"\r\n:next\r\nif \"%~1\"==\"\" goto done\r\nif /I \"%~1\"==\"--output-file\" set \"out=%~2\"\r\nshift\r\ngoto next\r\n:done\r\n" +
            $"> \"{ReadyPath}\" echo ready\r\ntimeout /t 2 /nobreak >nul\r\n> \"%out%.json\" echo {{\"result\":\"worker\"}}\r\nexit /b 0\r\n";

        private static string FailureScript(int exitCode, bool noisy, bool gpuInitializationFailure = false) => noisy
            ? $"@echo off\r\nfor /L %%i in (1,1,20000) do <nul set /p =x 1>&2\r\nexit /b {exitCode}\r\n"
            : $"@echo off\r\necho {(gpuInitializationFailure ? "ggml_vulkan: failed to initialize Vulkan" : "worker failure")} 1>&2\r\nexit /b {exitCode}\r\n";

        private static string? MarkerLine(string? marker) => marker is null ? null : $"> \"{marker}\" echo cpu";

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record FailedWorkerBehavior(int ExitCode, bool Noisy, bool GpuInitializationFailure = false);
}
