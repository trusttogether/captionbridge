using System.Windows.Forms;
using CaptionBridge.Services;

namespace CaptionBridge;

/// <summary>
/// The transcript box.
/// </summary>
/// <remarks>
/// This is a RichEdit rather than a plain edit control for one reason: its text object lets the
/// mirror rewrite the words without going through the selection. The box therefore never has to
/// stop following the captions. Selection itself is ordinary: a click places the caret, a drag
/// or Shift-click marks a range through every character between, and Copy reads that range.
/// </remarks>
internal sealed class CaptionTextBox : RichTextBox
{
    private RichEditDocument? _document;
    private int _dragAnchor = -1;

    public CaptionTextBox()
    {
        // A transcript that runs for hours will outgrow anything an edit control assumes by
        // default, and text past the limit is dropped rather than refused.
        MaxLength = int.MaxValue;
    }

    /// <summary>
    /// True while the mouse is dragging a selection out, which the control signals by holding
    /// the mouse capture. The view stops being pulled to the newest line for as long as that
    /// lasts, because sliding the words out from under the pointer is what makes a selection
    /// impossible to place.
    /// </summary>
    public bool IsSelecting => Capture;

    /// <summary>
    /// Brings the box up to date with the transcript. The user's selection is mapped through
    /// the edit and put back, unless they are still dragging — in which case the range stays
    /// under the pointer instead of being rebuilt from the old offsets.
    /// </summary>
    public void ApplyTranscript(string previous, string next)
    {
        var edit = CaptionTextEdit.Between(previous, next);
        var selection = CaptureSelection();

        var applied = edit.Slide == 0 || TryReplaceRange(0, edit.Slide, string.Empty);
        if (applied && (edit.RemovedLength > 0 || edit.InsertedLength > 0))
        {
            applied = TryReplaceRange(edit.KeptLength, edit.RemovedLength, next[edit.KeptLength..]);
        }

        if (!applied)
        {
            ReplaceAll(next);
        }

        if (_dragAnchor >= 0)
        {
            _dragAnchor = edit.MapOffset(_dragAnchor, previous.Length);
        }

        if (Capture && _dragAnchor >= 0)
        {
            SetNativeSelection(_dragAnchor, CharIndexFromClientPoint(PointToClient(MousePosition)));
            return;
        }

        var mapped = selection.Map(edit, previous.Length);
        if (_dragAnchor >= 0)
        {
            // The first click is the start. Mapping the range through a rewritten tail was
            // walking that mark around; keep it where they put it.
            mapped = new CaptionSelection(_dragAnchor, mapped.Caret);
        }

        RestoreSelection(mapped);
    }

    /// <summary>
    /// Replaces a run of text in place. Returns <see langword="false"/> when the text object is
    /// unavailable, leaving the caller to fall back to a plain assignment.
    /// </summary>
    public bool TryReplaceRange(int start, int length, string text)
    {
        if (_document is null)
        {
            return false;
        }

        // The document is protected while the box is read-only. The flag is flipped with the
        // message rather than the WinForms property because that property invalidates the whole
        // control on the way past, which at this refresh rate is two full repaints per caption.
        SetNativeReadOnly(false);
        try
        {
            if (!_document.TryReplaceRange(start, length, text))
            {
                return false;
            }
        }
        finally
        {
            SetNativeReadOnly(true);
        }

        return true;
    }

    public void SelectAllText()
    {
        _dragAnchor = 0;
        SelectAll();
    }

    public void ClearTranscript()
    {
        _dragAnchor = -1;
        Clear();
    }

    /// <summary>The last resort, which does collapse the selection.</summary>
    public void ReplaceAll(string text)
    {
        SetRedraw(false);
        try
        {
            Text = text;
        }
        finally
        {
            SetRedraw(true);
        }
    }

    /// <summary>
    /// The text the user has marked, read from the control itself rather than WinForms' cached
    /// SelectionStart. When nothing is marked, the whole transcript is what Ctrl+C copies.
    /// </summary>
    public string GetTextToCopy()
    {
        var (start, end) = GetNativeSelection();
        var text = Text;
        if (start >= end || start < 0 || end > text.Length)
        {
            return text;
        }

        return text[start..end];
    }

    public void ScrollToBottom()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var held = CaptureSelection();

        NativeMethods.SendMessage(
            Handle,
            NativeMethods.WmVScroll,
            new IntPtr(NativeMethods.SbBottom),
            IntPtr.Zero);

        // SB_BOTTOM has been seen to walk the caret with the view. If the user is not dragging,
        // put the range they chose back where it was.
        if (!Capture)
        {
            RestoreSelection(held);
        }
    }

    internal CaptionSelection CaptureSelection()
    {
        var (start, end) = GetNativeSelection();
        if (_dragAnchor < 0)
        {
            return new CaptionSelection(start, end);
        }

        var other = Math.Abs(start - _dragAnchor) > Math.Abs(end - _dragAnchor) ? start : end;
        return new CaptionSelection(_dragAnchor, other);
    }

    internal void RestoreSelection(CaptionSelection selection)
    {
        var start = Math.Clamp(Math.Min(selection.Anchor, selection.Caret), 0, TextLength);
        var end = Math.Clamp(Math.Max(selection.Anchor, selection.Caret), 0, TextLength);
        Select(start, end - start);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _document = RichEditDocument.FromHandle(Handle);

        // The native control ships with auto-word-selection on. WinForms reports the property
        // as false, and setting it false uses XOR, which would turn the native flag back on.
        // AND-ing it off is what lets a click place a caret instead of extending the last word.
        DisableAutoWordSelection();

        // Nothing in a mirror is ever undone, and recording it would have the control hold on
        // to captions that have already scrolled away.
        NativeMethods.SendMessage(Handle, NativeMethods.EmSetUndoLimit, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _document = null;
        _dragAnchor = -1;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WmLButtonDown:
            {
                var shift = (m.WParam.ToInt32() & NativeMethods.MkShift) != 0;
                // Capture the existing range before RichEdit processes the click: after that
                // the native hit-test has already replaced it, and Shift would extend from
                // the wrong end.
                if (shift && _dragAnchor < 0)
                {
                    _dragAnchor = AnchorForShiftClick(CharIndexFromPackedPoint(m.LParam));
                }

                base.WndProc(ref m);
                ApplyMouseSelection(CharIndexFromPackedPoint(m.LParam), shift);
                return;
            }

            case NativeMethods.WmMouseMove:
                base.WndProc(ref m);
                if (Capture && _dragAnchor >= 0)
                {
                    SetNativeSelection(_dragAnchor, CharIndexFromPackedPoint(m.LParam));
                }

                return;

            case NativeMethods.WmLButtonUp:
                if (Capture && _dragAnchor >= 0)
                {
                    SetNativeSelection(_dragAnchor, CharIndexFromPackedPoint(m.LParam));
                }

                base.WndProc(ref m);
                return;
        }

        base.WndProc(ref m);
    }

    internal bool HasAutoWordSelection()
    {
        if (!IsHandleCreated)
        {
            return false;
        }

        var options = NativeMethods.SendMessage(
            Handle,
            NativeMethods.EmGetOptions,
            IntPtr.Zero,
            IntPtr.Zero);

        return (options.ToInt32() & NativeMethods.EcoAutoWordSelection) != 0;
    }

    internal (int Start, int End) GetNativeSelection()
    {
        if (!IsHandleCreated)
        {
            return (SelectionStart, SelectionStart + SelectionLength);
        }

        NativeMethods.SendMessage(Handle, NativeMethods.EmGetSel, out var start, out var end);
        if (start > end)
        {
            (start, end) = (end, start);
        }

        return (start, end);
    }

    /// <summary>
    /// Insertion point for a client coordinate. Unlike
    /// <see cref="RichTextBox.GetCharIndexFromPosition"/> this can return
    /// <see cref="TextBoxBase.TextLength"/>, which is what a click past the last word means.
    /// </summary>
    internal int CharIndexFromClientPoint(Point point)
    {
        var length = TextLength;
        if (length == 0)
        {
            return 0;
        }

        var lineHeight = Math.Max(1, Font.Height);
        var lastPos = GetPositionFromCharIndex(length - 1);

        // Empty space under the last line, or to the right of the last glyph, is the end
        // of the transcript — the place Shift-select-to-end has to land.
        if (point.Y >= lastPos.Y + lineHeight)
        {
            return length;
        }

        if (point.Y >= lastPos.Y - lineHeight / 4 && point.X > lastPos.X)
        {
            return length;
        }

        var index = NativeCharFromPos(point);
        if (index < 0)
        {
            return 0;
        }

        if (index >= length)
        {
            return length;
        }

        var charPos = GetPositionFromCharIndex(index);
        var nextIndex = index + 1;
        if (nextIndex >= length)
        {
            return point.X > charPos.X ? length : index;
        }

        var nextPos = GetPositionFromCharIndex(nextIndex);
        var wrapped = nextPos.Y > charPos.Y + lineHeight / 2;
        if (wrapped)
        {
            return point.X > charPos.X ? nextIndex : index;
        }

        var width = nextPos.X - charPos.X;
        if (width > 0 && point.X > charPos.X + (width / 2))
        {
            return nextIndex;
        }

        return index;
    }

    private void ApplyMouseSelection(int index, bool shift)
    {
        if (!shift)
        {
            _dragAnchor = index;
        }
        else if (_dragAnchor < 0)
        {
            _dragAnchor = index;
        }

        SetNativeSelection(_dragAnchor, index);
    }

    private int AnchorForShiftClick(int clickIndex)
    {
        var (start, end) = GetNativeSelection();
        if (start == end)
        {
            return start;
        }

        return clickIndex <= start ? end : start;
    }

    private void DisableAutoWordSelection()
    {
        NativeMethods.SendMessage(
            Handle,
            NativeMethods.EmSetOptions,
            new IntPtr(NativeMethods.EcoOpAnd),
            new IntPtr(unchecked((int)~NativeMethods.EcoAutoWordSelection)));
    }

    private void SetNativeSelection(int anchor, int caret)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        NativeMethods.SendMessage(Handle, NativeMethods.EmSetSel, new IntPtr(anchor), new IntPtr(caret));
    }

    private int CharIndexFromPackedPoint(IntPtr lParam)
    {
        var packed = lParam.ToInt32();
        var point = new Point((short)packed, (short)(packed >> 16));
        return CharIndexFromClientPoint(point);
    }

    private int NativeCharFromPos(Point point)
    {
        var nativePoint = new NativeMethods.Point32 { X = point.X, Y = point.Y };
        var result = NativeMethods.SendMessage(
            Handle,
            NativeMethods.EmCharFromPos,
            IntPtr.Zero,
            ref nativePoint);

        return unchecked((int)result.ToInt64());
    }

    private void SetNativeReadOnly(bool enabled)
    {
        NativeMethods.SendMessage(
            Handle,
            NativeMethods.EmSetReadOnly,
            enabled ? new IntPtr(1) : IntPtr.Zero,
            IntPtr.Zero);
    }

    private void SetRedraw(bool enabled)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        NativeMethods.SendMessage(
            Handle,
            NativeMethods.WmSetRedraw,
            enabled ? new IntPtr(1) : IntPtr.Zero,
            IntPtr.Zero);

        if (enabled)
        {
            Invalidate();
        }
    }
}
