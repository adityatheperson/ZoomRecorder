namespace ZoomRecorder.App.LocalTranscription;

internal sealed class LocalTranscriptionPaths
{
    public LocalTranscriptionPaths(string modelsRoot, string gpuWorkerPath, string cpuWorkerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuWorkerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cpuWorkerPath);

        ModelsRoot = Path.GetFullPath(modelsRoot);
        GpuWorkerPath = Path.GetFullPath(gpuWorkerPath);
        CpuWorkerPath = Path.GetFullPath(cpuWorkerPath);
    }

    public string ModelsRoot { get; }
    public string GpuWorkerPath { get; }
    public string CpuWorkerPath { get; }

    public static LocalTranscriptionPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var applicationRoot = AppContext.BaseDirectory;

        return new LocalTranscriptionPaths(
            Path.Combine(localAppData, "ZoomRecorder", "Models"),
            Path.Combine(applicationRoot, "tools", "whisper", "vulkan", "whisper-cli.exe"),
            Path.Combine(applicationRoot, "tools", "whisper", "cpu", "whisper-cli.exe"));
    }

    public string GetModelPath(WhisperModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return GetModelPath(ModelsRoot, manifest.FileName);
    }

    internal static string GetModelPath(string modelsRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var canonicalRoot = Path.GetFullPath(modelsRoot);
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, fileName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The model file path escapes the canonical model root.");
        }

        return candidate;
    }
}
