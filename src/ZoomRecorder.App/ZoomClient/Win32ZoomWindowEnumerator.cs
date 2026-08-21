using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZoomRecorder.App.ZoomClient;

public sealed class Win32ZoomWindowEnumerator : IZoomWindowEnumerator
{
    public IReadOnlyList<ZoomWindowDescription> Enumerate()
    {
        var windows = new List<ZoomWindowDescription>();
        EnumWindows((handle, _) =>
        {
            if (TryDescribe(handle, out var description))
            {
                windows.Add(description);
            }

            return true;
        }, nint.Zero);
        return windows;
    }

    private static bool TryDescribe(nint handle, out ZoomWindowDescription description)
    {
        description = default!;
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || !GetClientRect(handle, out var rectangle))
        {
            return false;
        }

        string processName;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        description = new ZoomWindowDescription(
            handle,
            checked((int)processId),
            processName,
            ReadText(handle, GetClassName),
            ReadText(handle, GetWindowText),
            IsWindowVisible(handle),
            IsIconic(handle),
            Math.Max(0, rectangle.Right - rectangle.Left),
            Math.Max(0, rectangle.Bottom - rectangle.Top));
        return true;
    }

    private static string ReadText(nint handle, WindowTextReader reader)
    {
        var buffer = new StringBuilder(512);
        _ = reader(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);
    private delegate int WindowTextReader(nint handle, StringBuilder buffer, int maximumCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint handle, out Rect rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder title, int maximumCount);
}
