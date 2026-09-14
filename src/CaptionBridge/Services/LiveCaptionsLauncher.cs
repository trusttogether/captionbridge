using System.Runtime.InteropServices;

namespace CaptionBridge.Services;

internal static class LiveCaptionsLauncher
{
    private const byte VkLeftWindows = 0x5B;
    private const byte VkControl = 0x11;
    private const byte VkL = 0x4C;
    private const uint KeyUp = 0x0002;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
