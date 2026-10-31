using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CaptionBridge.Services;

namespace CaptionBridge.Tests;

/// <summary>
/// Runs against a real RichEdit control, because the whole point of the caption box is that the
/// text can be rewritten underneath a selection the user is still making.
/// </summary>
public class CaptionTextBoxTests
{
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void ClearTranscriptEmptiesTheBox()
    {
        WithCaptionBox(box =>
        {
            box.Text = "one two three four five";
            box.Select(8, 5);
            box.ClearTranscript();

            Assert.Equal(string.Empty, box.Text);
            Assert.Equal(0, box.SelectionLength);
        });
    }

    [Fact]
    public void TurnsOffAutoWordSelectionOnceTheHandleExists()
    {
        WithCaptionBox(box =>
        {
            Assert.False(box.AutoWordSelection);
            Assert.False(box.HasAutoWordSelection());
        });
    }

    [Fact]
    public void AClickClearsThePreviousSelection()
    {
        WithCaptionBox(box =>
        {
            box.Text = "one two three four five";
            box.Select(8, 5);
            Assert.Equal("three", box.SelectedText);

            // A left click at the start of the text, without Shift, has to drop the old range
            // and leave a caret there — the thing AutoWordSelection and a read-only RichEdit
            // both refuse to do on their own.
            SendMessage(box.Handle, WmLButtonDown, IntPtr.Zero, PackPoint(4, 4));
            SendMessage(box.Handle, WmLButtonUp, IntPtr.Zero, PackPoint(4, 4));

            var (start, end) = box.GetNativeSelection();
            Assert.Equal(start, end);
            Assert.NotEqual("three", box.GetTextToCopy());
        });
    }

    [Fact]
    public void CopyUsesTheCurrentSelectionNotAPreviousOne()
    {
        WithCaptionBox(box =>
        {
            box.Text = "one two three four five";
            box.Select(0, 3);
            Assert.Equal("one", box.GetTextToCopy());

            box.Select(8, 5);
            Assert.Equal("three", box.GetTextToCopy());

            box.Select(14, 4);
            Assert.Equal("four", box.GetTextToCopy());
        });
    }

    [Fact]
    public void CopyOfAnEmptySelectionIsTheWholeTranscript()
    {
        WithCaptionBox(box =>
        {
            const string text = "one two three four five";
            box.Text = text;
            box.Select(8, 0);

            Assert.Equal(text, box.GetTextToCopy());
        });
    }

    [Fact]
    public void LeavesTheSelectionAloneWhenTextIsAppended()
    {
        WithCaptionBox(box =>
        {
            const string before = "one two three four five";
            box.Text = before;
            box.Select(8, 5);
            Assert.Equal("three", box.SelectedText);

            Assert.True(box.TryReplaceRange(before.Length, 0, " six seven"));

            Assert.Equal("one two three four five six seven", box.Text);
            Assert.Equal(8, box.SelectionStart);
            Assert.Equal("three", box.SelectedText);
        });
    }

    [Fact]
    public void LeavesTheSelectionAloneWhenTheLastWordsAreRewritten()
    {
        WithCaptionBox(box =>
        {
            const string before = "i think the answer is yes";
            const string after = "i think the answer is definitely yes";
            box.Text = before;
            box.Select(2, 5);
            Assert.Equal("think", box.SelectedText);

            var edit = CaptionTextEdit.Between(before, after);
            Assert.True(box.TryReplaceRange(edit.KeptLength, edit.RemovedLength, after[edit.KeptLength..]));

            Assert.Equal(after, box.Text);
            Assert.Equal("think", box.SelectedText);
        });
    }

    [Fact]
    public void CarriesTheSelectionAlongWhenTheFrontScrollsAway()
    {
        WithCaptionBox(box =>
        {
            box.Text = "one two three four five";
            box.Select(8, 5);
            Assert.Equal("three", box.SelectedText);

            // Live Captions dropped "one two " off the front. The control moves the selection
            // down with the words; nothing re-selects it.
            Assert.True(box.TryReplaceRange(0, 8, string.Empty));

            Assert.Equal("three four five", box.Text);
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal("three", box.SelectedText);
        });
    }

    [Fact]
    public void KeepsFollowingWhileADragIsInProgress()
    {
        WithCaptionBox(box =>
        {
            const string before = "one two three four five";
            box.Text = before;
            box.Select(8, 5);
            box.Capture = true;

            Assert.True(box.IsSelecting);
            Assert.Equal("three", box.SelectedText);

            Assert.True(box.TryReplaceRange(before.Length, 0, " six seven"));

            Assert.Equal("one two three four five six seven", box.Text);
            Assert.Equal("three", box.SelectedText);

            box.Capture = false;
            Assert.False(box.IsSelecting);
        });
    }

    [Fact]
    public void StopsSelectingWhenCaptureIsLost()
    {
        WithCaptionBox(box =>
        {
            box.Text = "one two three four five";
            SendMessage(box.Handle, WmLButtonDown, IntPtr.Zero, PackPoint(4, 4));
            Assert.True(box.IsSelecting);

            // Something took the mouse away without the box ever seeing the button come up.
            box.Capture = false;

            Assert.False(box.IsSelecting);
        });
    }

    [Fact]
    public void StaysReadOnlyAcrossAnEdit()
    {
        WithCaptionBox(box =>
        {
            box.Text = "the quick brown";
            Assert.True(box.TryReplaceRange(10, 5, "slow fox"));

            Assert.Equal("the quick slow fox", box.Text);
            Assert.True(box.ReadOnly);
        });
    }

    /// <summary>
    /// An edit control holds 32,767 characters unless told otherwise, which a transcript passes
    /// inside an hour. Text over the limit is dropped silently rather than refused.
    /// </summary>
    [Fact]
    public void HoldsATranscriptLongerThanTheDefaultLimit()
    {
        WithCaptionBox(box =>
        {
            var transcript = string.Join(' ', Enumerable.Repeat("a sentence of about forty characters long", 3000));
            Assert.True(transcript.Length > 100_000);

            box.ReplaceAll(transcript);
            Assert.Equal(transcript.Length, box.TextLength);

            Assert.True(box.TryReplaceRange(transcript.Length, 0, " and the end"));
            Assert.EndsWith("and the end", box.Text);
        });
    }

    [Fact]
    public void FallsBackToAPlainAssignment()
    {
        WithCaptionBox(box =>
        {
            box.ReplaceAll("a completely different caption");

            Assert.Equal("a completely different caption", box.Text);
        });
    }

    [Fact]
    public void IdleSelectionStaysOnTheSameWordsWhileCaptionsKeepArriving()
    {
        WithCaptionBox(box =>
        {
            var previous = "one two three four five";
            box.Text = previous;
            box.Select(8, 5);
            Assert.Equal("three", box.SelectedText);

            // These are the edits a running transcript actually makes: appends, then a rewrite
            // of the last word. The box must not move an idle selection while doing them.
            foreach (var next in new[]
                     {
                         "one two three four five six",
                         "one two three four five six seven",
                         "one two three four five six seven eight",
                         "one two three four five six seven ate",
                         "one two three four five six seven ate nine ten eleven twelve",
                     })
            {
                box.ApplyTranscript(previous, next);
                box.ScrollToBottom();
                Assert.Equal("three", box.SelectedText);
                previous = next;
            }
        });
    }

    [Fact]
    public void FirstSelectionStartDoesNotJumpWhenTheTailIsRewritten()
    {
        WithCaptionBox(box =>
        {
            const string before = "one two three four five six seven eight nine";
            box.Text = before;
            box.Select(8, 5);
            Assert.Equal(8, box.SelectionStart);
            Assert.Equal("three", box.SelectedText);

            var next = "one two three four five six seven ate nine";
            box.ApplyTranscript(before, next);
            Assert.Equal(8, box.SelectionStart);
            Assert.Equal("three", box.SelectedText);

            box.ApplyTranscript(next, "one two three four five six seven ate nine ten eleven");
            Assert.Equal(8, box.SelectionStart);
        });
    }

    [Fact]
    public void IdleSelectionIsNotExtendedByNewWordsAtTheEnd()
    {
        WithCaptionBox(box =>
        {
            const string before = "one two three four five";
            box.Text = before;
            box.Select(14, 4);
            Assert.Equal("four", box.SelectedText);

            box.ApplyTranscript(before, before + " six seven eight");
            box.ScrollToBottom();

            Assert.Equal("four", box.SelectedText);
        });
    }

    [Fact]
    public void ScrollToBottomDoesNotMoveAnIdleSelection()
    {
        WithCaptionBox(box =>
        {
            box.Text = string.Join(' ', Enumerable.Repeat("a line of caption text that wraps", 40));
            box.Select(8, 4);
            var selected = box.SelectedText;

            box.ScrollToBottom();
            box.ScrollToBottom();
            box.ScrollToBottom();

            Assert.Equal(selected, box.SelectedText);
        });
    }

    [Fact]
    public void AShiftSelectThroughTheEndStaysThroughTheEndAsCaptionsArrive()
    {
        WithCaptionBox(box =>
        {
            const string before = "How did you handle authentication authorization execution in the AI systems? And can you please tell me about your experience with Fastapi?";
            box.Text = before;
            box.Select(0, before.Length);
            Assert.Equal(before, box.SelectedText);

            var appended = before + " How to structure services, performance, reliability and maintain abilities?";
            box.ApplyTranscript(before, appended);
            box.ScrollToBottom();
            Assert.Equal(appended, box.SelectedText);

            var rewritten = appended[..^"abilities?".Length] + "ability? And uh, you worked with Open AI Cloud.";
            box.ApplyTranscript(appended, rewritten);
            box.ScrollToBottom();
            Assert.Equal(rewritten, box.SelectedText);
        });
    }

    [Fact]
    public void IdleSelectionSurvivesAScrollingLiveCaptionsWindow()
    {
        WithCaptionBox(box =>
        {
            var transcript = new CaptionTranscript();
            var previous = string.Empty;

            void Feed(string window)
            {
                var next = transcript.Absorb(window);
                if (previous.Length == 0)
                {
                    box.Text = next;
                }
                else
                {
                    box.ApplyTranscript(previous, next);
                }

                previous = next;
            }

            Feed("one two three four five six seven");
            var at = box.Text.IndexOf("three", StringComparison.Ordinal);
            box.Select(at, 5);
            Assert.Equal("three", box.SelectedText);

            Feed("two three four five six seven eight");
            Feed("three four five six seven eight nine");
            Feed("four five six seven eight nine ten eleven");
            Feed("five six seven eight nine ten eleven twelve thirteen");
            box.ScrollToBottom();

            Assert.Contains("one two three", box.Text, StringComparison.Ordinal);
            Assert.Equal("three", box.SelectedText);
        });
    }

    [Fact]
    public void AClickPastTheLastWordIsTheEndOfTheText()
    {
        WithCaptionBox(box =>
        {
            const string text = "one two three four five";
            box.Text = text;
            box.Font = new Font("Segoe UI", 16, FontStyle.Regular);

            var last = box.GetPositionFromCharIndex(text.Length - 1);
            var pastTheEnd = new Point(last.X + 80, last.Y);
            var below = new Point(8, last.Y + box.Font.Height * 2);

            Assert.Equal(text.Length, box.CharIndexFromClientPoint(pastTheEnd));
            Assert.Equal(text.Length, box.CharIndexFromClientPoint(below));
        });
    }

    [Fact]
    public void ShiftClickPastTheEndSelectsThroughTheMiddleToTheLastWord()
    {
        WithCaptionBox(box =>
        {
            const string text = "one two three four five";
            box.Text = text;
            box.Font = new Font("Segoe UI", 16, FontStyle.Regular);
            box.Select(0, 3);
            Assert.Equal("one", box.GetTextToCopy());

            var last = box.GetPositionFromCharIndex(text.Length - 1);
            var pastTheEnd = PackPoint(last.X + 80, last.Y);
            SendMessage(box.Handle, WmLButtonDown, new IntPtr(MkShift), pastTheEnd);
            SendMessage(box.Handle, WmLButtonUp, new IntPtr(MkShift), pastTheEnd);

            Assert.Equal(text, box.GetTextToCopy());
        });
    }

    [Fact]
    public void ShiftClickToTheEndKeepsTheWordsInTheMiddle()
    {
        WithCaptionBox(box =>
        {
            const string text = "one two three four five";
            box.Text = text;
            box.Font = new Font("Segoe UI", 16, FontStyle.Regular);
            box.Select(8, 5);
            Assert.Equal("three", box.GetTextToCopy());

            var last = box.GetPositionFromCharIndex(text.Length - 1);
            var pastTheEnd = PackPoint(last.X + 80, last.Y);
            SendMessage(box.Handle, WmLButtonDown, new IntPtr(MkShift), pastTheEnd);
            SendMessage(box.Handle, WmLButtonUp, new IntPtr(MkShift), pastTheEnd);

            Assert.Equal("three four five", box.GetTextToCopy());
        });
    }

    private const int MkShift = 0x0004;

    private static IntPtr PackPoint(int x, int y) => new((y << 16) | (x & 0xFFFF));

    /// <summary>
    /// Window handles need a desktop and an STA thread, which the test runner's thread is not.
    /// </summary>
    private static void WithCaptionBox(Action<CaptionTextBox> test)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var box = new CaptionTextBox
                {
                    WordWrap = true,
                    ReadOnly = true,
                    DetectUrls = false,
                    Width = 320,
                    Height = 200,
                };

                // Touching the handle is what creates the control and its text object.
                _ = box.Handle;
                test(box);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
