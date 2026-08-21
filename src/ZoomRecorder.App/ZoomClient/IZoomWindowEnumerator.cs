namespace ZoomRecorder.App.ZoomClient;

public interface IZoomWindowEnumerator
{
    IReadOnlyList<ZoomWindowDescription> Enumerate();
}
