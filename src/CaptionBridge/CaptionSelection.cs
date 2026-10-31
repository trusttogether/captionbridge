namespace CaptionBridge;

/// <summary>
/// A selection as the control stores it: the anchor is where the range started, the caret is
/// where it ends. Mapping both through a <see cref="Services.CaptionTextEdit"/> keeps the same
/// words marked after the transcript moves, without rebuilding the range from scratch.
/// </summary>
internal readonly record struct CaptionSelection(int Anchor, int Caret)
{
    public CaptionSelection Map(Services.CaptionTextEdit edit, int oldLength) =>
        new(edit.MapOffset(Anchor, oldLength), edit.MapOffset(Caret, oldLength));
}
