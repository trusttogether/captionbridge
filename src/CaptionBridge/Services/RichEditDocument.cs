using System.Reflection;
using System.Runtime.InteropServices;

namespace CaptionBridge.Services;

/// <summary>
/// The Text Object Model view of a RichEdit control.
/// </summary>
/// <remarks>
/// Editing through a range is the only way to change the text of an edit control without moving
/// the caret or collapsing what the user has selected. Every other route — EM_REPLACESEL,
/// EM_SETTEXTEX, WM_SETTEXT — works through the selection and therefore destroys it, which is
/// what forces a mirror built on a plain TextBox to choose between following the captions and
/// letting the user select.
/// </remarks>
internal sealed class RichEditDocument
{
    private readonly object _document;

    private RichEditDocument(object document) => _document = document;

    /// <summary>Returns null when the control will not hand its text object over.</summary>
    public static RichEditDocument? FromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.SendMessage(hwnd, NativeMethods.EmGetOleInterface, IntPtr.Zero, out var ole);
        if (ole == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new RichEditDocument(Marshal.GetObjectForIUnknown(ole));
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(ole);
        }
    }

    /// <summary>
    /// Replaces <paramref name="length"/> characters at <paramref name="start"/>. Offsets are
    /// the control's own, which match the string only while the text has no line breaks in it —
    /// true here because a cleaned caption is always a single run of words.
    /// </summary>
    /// <returns><see langword="false"/> when the edit did not go through.</returns>
    public bool TryReplaceRange(int start, int length, string text)
    {
        try
        {
            // Late binding rather than a hand-declared vtable. The TOM interfaces derive from
            // IDispatch, so this is a supported way to call them, and a mistake here throws
            // instead of corrupting memory the way a wrong method slot would. Two dispatch
            // calls do not register next to a 10 ms poll.
            var range = Call(_document, "Range", BindingFlags.InvokeMethod, start, start + length);
            if (range is null)
            {
                return false;
            }

            // Freeze keeps the control from treating the edit as a user action, which is what
            // would move the caret into the rewritten tail and leave Copy holding that range
            // instead of the one the user just dragged out.
            var frozen = TryCall(_document, "Freeze", BindingFlags.InvokeMethod);
            try
            {
                Call(range, "Text", BindingFlags.SetProperty, text);
            }
            finally
            {
                if (frozen)
                {
                    TryCall(_document, "Unfreeze", BindingFlags.InvokeMethod);
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is COMException or MissingMemberException or TargetInvocationException
                or InvalidCastException or NotSupportedException)
        {
            return false;
        }
    }

    private static object? Call(object target, string member, BindingFlags flags, params object[] arguments)
    {
        return target.GetType().InvokeMember(member, flags, binder: null, target, arguments);
    }

    private static bool TryCall(object target, string member, BindingFlags flags, params object[] arguments)
    {
        try
        {
            Call(target, member, flags, arguments);
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or MissingMemberException or TargetInvocationException
                or InvalidCastException or NotSupportedException)
        {
            return false;
        }
    }
}
