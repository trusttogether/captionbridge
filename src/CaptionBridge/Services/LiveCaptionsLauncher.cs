using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CaptionBridge.Services;

internal static class LiveCaptionsLauncher
{
    private const string ProcessName = "LiveCaptions";
    private const string ExecutableName = "LiveCaptions.exe";

    private const byte VkLeftWindows = 0x5B;
    private const byte VkControl = 0x11;
    private const byte VkL = 0x4C;
    private const uint KeyUp = 0x0002;

    /// <summary>
    /// Starts Live Captions unless it is already running. The check is the whole point: the
    /// fallback below is a shortcut that toggles, so firing it at a running Live Captions would
    /// turn the captions off rather than on.
    /// </summary>
    public static void EnsureRunning()
    {
        if (IsRunning())
        {
            return;
        }

        if (!TryStartExecutable())
        {
            Toggle();
        }
    }

    /// <summary>Presses Win + Ctrl + L, the system shortcut that toggles Live Captions.</summary>
    public static void Toggle()
    {
        keybd_event(VkLeftWindows, 0, 0, UIntPtr.Zero);
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLeftWindows, 0, KeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// Answers "yes" when the process list cannot be read. Leaving captions alone is the better
    /// mistake, because the alternative is toggling off the ones the user already had.
    /// </summary>
    private static bool IsRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return true;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Windows 11 ships Live Captions as an exe in the system directory. Starting it by path is
    /// exact in a way a synthetic keystroke is not, so it is tried first.
    /// </summary>
    private static bool TryStartExecutable()
    {
        var path = Path.Combine(Environment.SystemDirectory, ExecutableName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return started is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
