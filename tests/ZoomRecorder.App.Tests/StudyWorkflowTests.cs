using ZoomRecorder.App.Composition;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Tests;

public sealed class StudyWorkflowTests
{
    [Fact]
    public async Task Startup_migrates_registers_and_reopens_the_local_workflow()
    {
        using var temp = new TestDirectory();
        var paths = new LibraryPaths(temp.File("library.db"), temp.File("artifacts"), temp.File("jobs"));
        var recordingId = Guid.NewGuid();

        await using (var services = await AppServices.CreateAsync(paths, default))
        {
            var course = await services.Repository.CreateClassAsync("Biology 101", "Fall", default);
            await services.Repository.AddRecordingAsync(new RecordingRecord(
                recordingId, course.Id, temp.File("lecture.mp4"), "lecture.mp4", "1234567890",
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(45), 100, true), default);
            Assert.Empty(await services.Coordinator.RecoverAsync(default));
        }

        await using var reopened = await AppServices.CreateAsync(paths, default);
        var lecture = Assert.Single(await reopened.Repository.ListRecordingsAsync(null, default));
        Assert.Equal(recordingId, lecture.Id);
        Assert.Equal("Biology 101", Assert.Single(await reopened.Repository.ListClassesAsync(false, default)).Name);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZoomRecorder.Workflow", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
