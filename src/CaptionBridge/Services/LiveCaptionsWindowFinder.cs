using System.Diagnostics;

namespace CaptionBridge.Services;

/// <summary>
/// Locates the Windows Live Captions window by HWND. Enumerating top-level windows costs a
/// fraction of a UI Automation tree search, which matters because the reader re-runs this
/// whenever the cached window goes away.
/// </summary>
internal static class LiveCaptionsWindowFinder
{
    private const string TitleMarker = "live captions";
    private const string ProcessMarker = "livecaption";

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    public static IntPtr Find()
    {
        var byTitle = IntPtr.Zero;
        var byProcess = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == CurrentProcessId)
            {
                // Our own title bar says "waiting for Live Captions" while idle, which would
                // otherwise match the title marker and make the app mirror itself.
                return true;
            }

            if (NativeMethods.GetWindowTitle(hwnd).Contains(TitleMarker, StringComparison.OrdinalIgnoreCase))
            {
                byTitle = hwnd;
                return false;
            }

            if (byProcess == IntPtr.Zero && OwnedByLiveCaptions(processId))
            {
                byProcess = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return byTitle != IntPtr.Zero ? byTitle : byProcess;
    }

    private static bool OwnedByLiveCaptions(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Contains(ProcessMarker, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
