namespace ZoomRecorder.Core.Library;

public sealed record ClassRecord(Guid Id, string Name, string? Term, DateTimeOffset CreatedAt, bool IsArchived);
