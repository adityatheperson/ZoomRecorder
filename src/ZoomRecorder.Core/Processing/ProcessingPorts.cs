namespace ZoomRecorder.Core.Processing;

public sealed record AudioChunk(
    int Index,
    string Path,
    long StartMilliseconds,
    long EndMilliseconds,
    string Sha256,
    long ByteSize);

public sealed record RecycleResult(bool Recycled, string? RecycledPath);

public interface IAudioChunkPreparer
{
    Task<IReadOnlyList<AudioChunk>> PrepareAsync(
        string mp4Path,
        string jobDirectory,
        long maxBytes,
        CancellationToken cancellationToken);
}

public interface ITranscriptionClient
{
    Task<TranscriptChunk> TranscribeAsync(AudioChunk chunk, CancellationToken cancellationToken);
}

public interface IStudyGenerationClient
{
    Task<StudyPackage> GenerateLectureAsync(Transcript transcript, CancellationToken cancellationToken);

    Task<ClassStudyGuide> GenerateGuideAsync(
        IReadOnlyList<StudyPackage> lectures,
        CancellationToken cancellationToken);
}

public interface ICredentialVault
{
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken);

    Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken);

    Task DeleteApiKeyAsync(CancellationToken cancellationToken);
}

public interface IVideoRecycler
{
    Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken);
}
