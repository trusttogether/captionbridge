using System.Runtime.InteropServices;
using System.Text;

namespace CaptionBridge.Services;

internal static class NativeMethods
{
    public const int WmSetRedraw = 0x000B;
    public const int WmVScroll = 0x0115;
    public const int SbBottom = 7;
    public const int EmSetRect = 0x00B3;

    public const int EmSetReadOnly = 0x00CF;
    public const int EmGetSel = 0x00B0;
    public const int EmSetSel = 0x00B1;
    public const int EmPosFromChar = 0x00D6;
    public const int EmCharFromPos = 0x00D7;

    /// <summary>EM_GETOLEINTERFACE, which hands back the RichEdit text object.</summary>
    public const int EmGetOleInterface = 0x043C;

    /// <summary>EM_SETUNDOLIMIT.</summary>
    public const int EmSetUndoLimit = 0x0452;

    public const int EmSetOptions = 0x044D;
    public const int EmGetOptions = 0x044E;
    public const int EcoOpAnd = 3;
    public const int EcoAutoWordSelection = 0x0001;

    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmMouseMove = 0x0200;
    public const int WmCaptureChanged = 0x0215;
    public const int MkShift = 0x0004;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 20H1 and later.</summary>
    public const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>The attribute index the same setting used on earlier Windows 10 builds.</summary>
    public const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref Rect lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref Point32 lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, out IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, out int wParam, out int lParam);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint period);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
