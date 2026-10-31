using CaptionBridge.Services;

namespace CaptionBridge.Tests;

public class CaptionTextEditTests
{
    [Fact]
    public void DescribesAnAppendAsAKeptPrefix()
    {
        var edit = CaptionTextEdit.Between("hello there", "hello there everyone");

        Assert.Equal(0, edit.Slide);
        Assert.Equal(11, edit.KeptLength);
        Assert.Equal(0, edit.RemovedLength);
        Assert.Equal(9, edit.InsertedLength);
    }

    [Fact]
    public void DescribesARewrittenTail()
    {
        var edit = CaptionTextEdit.Between("i think the", "i think that");

        Assert.Equal(0, edit.Slide);
        Assert.Equal(10, edit.KeptLength);
        Assert.Equal(1, edit.RemovedLength);
        Assert.Equal(2, edit.InsertedLength);
    }

    [Fact]
    public void FindsHowFarTheTextScrolledOffTheFront()
    {
        var edit = CaptionTextEdit.Between("one two three four five", "two three four five six");

        Assert.Equal(4, edit.Slide);
        Assert.Equal(19, edit.KeptLength);
        Assert.Equal(0, edit.RemovedLength);
        Assert.Equal(4, edit.InsertedLength);
    }

    /// <summary>
    /// The two spans are what the mirror edits, so applying them has to land on the new text
    /// exactly. Anything else would leave the box quietly out of step with the captions.
    /// </summary>
    [Theory]
    [InlineData("hello there", "hello there everyone")]
    [InlineData("i think the", "i think that")]
    [InlineData("one two three four five", "two three four five six")]
    [InlineData("the previous caption", "nothing in common here")]
    [InlineData("", "first words")]
    [InlineData("going away", "")]
    [InlineData("a long caption that mentions yes twice, yes", "yes")]
    [InlineData("hi \U0001F600", "hi \U0001F601")]
    public void ApplyingTheEditReproducesTheNewText(string before, string after)
    {
        var edit = CaptionTextEdit.Between(before, after);

        var applied = before.Remove(0, edit.Slide);
        applied = applied.Remove(edit.KeptLength, edit.RemovedLength);
        applied += after[edit.KeptLength..];

        Assert.Equal(after, applied);
    }

    [Fact]
    public void DoesNotMistakeAShortCaptionForAScroll()
    {
        var edit = CaptionTextEdit.Between("a long caption that mentions yes twice, yes", "yes");

        Assert.Equal(0, edit.Slide);
        Assert.Equal(0, edit.KeptLength);
    }

    [Fact]
    public void HandlesAnEmptyStartingText()
    {
        var edit = CaptionTextEdit.Between(string.Empty, "first words");

        Assert.Equal(0, edit.Slide);
        Assert.Equal(0, edit.KeptLength);
        Assert.Equal(0, edit.RemovedLength);
        Assert.Equal(11, edit.InsertedLength);
    }

    [Fact]
    public void NeverSplitsASurrogatePair()
    {
        var edit = CaptionTextEdit.Between("hi \U0001F600", "hi \U0001F601");

        Assert.Equal(3, edit.KeptLength);
        Assert.Equal(2, edit.RemovedLength);
        Assert.Equal(2, edit.InsertedLength);
    }

    [Fact]
    public void MapsOffsetsThroughAnAppendWithoutMovingThem()
    {
        var edit = CaptionTextEdit.Between("hello there", "hello there everyone");

        Assert.Equal(0, edit.MapOffset(0));
        Assert.Equal(6, edit.MapOffset(6));
        Assert.Equal(11, edit.MapOffset(11));
    }

    [Fact]
    public void MapsOffsetsThroughAFrontScroll()
    {
        var edit = CaptionTextEdit.Between("one two three four five", "two three four five six");

        Assert.Equal(0, edit.MapOffset(0));
        Assert.Equal(0, edit.MapOffset(3));
        Assert.Equal(4, edit.MapOffset(8));
    }

    [Fact]
    public void DoesNotPullNewWordsIntoASelectionThatEndedInTheOldTail()
    {
        var edit = CaptionTextEdit.Between("hello there", "hello there everyone");

        Assert.Equal(11, edit.MapOffset(11, 12));
    }

    [Fact]
    public void PinsOffsetsInsideARewrittenTailToTheSharedRun()
    {
        var edit = CaptionTextEdit.Between("i think the", "i think that");

        Assert.Equal(10, edit.MapOffset(10, 11));
        Assert.Equal(11, edit.MapOffset(11, 12));
    }

    [Fact]
    public void ASelectionThroughTheEndStaysAtTheNewEnd()
    {
        var appended = CaptionTextEdit.Between("hello there", "hello there everyone");
        Assert.Equal(20, appended.MapOffset(11, 11));

        var rewritten = CaptionTextEdit.Between("i think the", "i think that");
        Assert.Equal(12, rewritten.MapOffset(11, 11));
    }
}
