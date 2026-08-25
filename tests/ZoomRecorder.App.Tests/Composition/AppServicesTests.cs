using System.Security.Cryptography;
using ZoomRecorder.App.Composition;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.Composition;

public sealed class AppServicesTests
{
    [Fact]
    public async Task Transcript_only_composition_uses_local_whisper_without_reading_credentials()
    {
        using var files = new ServiceFiles();
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var vault = new CountingVault();
        var modelBytes = new byte[] { 0x42 };
        Directory.CreateDirectory(files.Models);
        File.WriteAllBytes(Path.Combine(files.Models, "ggml-small.en.bin"), modelBytes);
        var manifest = new WhisperModelManifest(
            "ggml-small.en.bin",
            new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/revision/ggml-small.en.bin"),
            modelBytes.Length,
            Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant());
        var localPaths = new LocalTranscriptionPaths(files.Models, files.GpuWorker, files.CpuWorker);

        await using var services = await AppServices.CreateAsync(
            files.LibraryPaths, http, vault, manifest, localPaths, CancellationToken.None);

        var field = typeof(ProcessingCoordinator).GetField(
            "transcription",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var transcription = Assert.IsType<LocalWhisperTranscriptionClient>(field!.GetValue(services.Coordinator));
        var modelField = typeof(LocalWhisperTranscriptionClient).GetField(
            "modelManager",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var modelManager = Assert.IsAssignableFrom<IWhisperModelManager>(modelField!.GetValue(transcription));
        Assert.Equal(
            Path.Combine(files.Models, "ggml-small.en.bin"),
            await modelManager.EnsureModelAsync(null, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, vault.Reads);
        Assert.Equal(0, vault.Writes);
        Assert.Equal(0, vault.Deletes);
    }

    private sealed class CountingVault : ICredentialVault
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) { Reads++; return Task.FromResult<string?>(null); }
        public Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken) { Writes++; return Task.CompletedTask; }
        public Task DeleteApiKeyAsync(CancellationToken cancellationToken) { Deletes++; return Task.CompletedTask; }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Offline cached-model test attempted network call {++Calls}.");
    }

    private sealed class ServiceFiles : IDisposable
    {
        public ServiceFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zoom-recorder-local-services-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Models = Path.Combine(Root, "models");
            GpuWorker = Path.Combine(Root, "gpu", "whisper-cli.exe");
            CpuWorker = Path.Combine(Root, "cpu", "whisper-cli.exe");
            LibraryPaths = new LibraryPaths(
                Path.Combine(Root, "library.db"),
                Path.Combine(Root, "artifacts"),
                Path.Combine(Root, "jobs"));
        }

        public string Root { get; }
        public string Models { get; }
        public string GpuWorker { get; }
        public string CpuWorker { get; }
        public LibraryPaths LibraryPaths { get; }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
