using System.Text;

namespace CaptionBridge.Services;

/// <summary>
/// The running transcript.
/// </summary>
/// <remarks>
/// Live Captions only ever shows the last few lines and throws the rest away. This keeps them.
/// What it shows is treated as the live tail of something longer: as words scroll out of its
/// window they are committed to the front of the transcript rather than dropped, and the tail
/// is free to keep being rewritten while recognition settles.
/// </remarks>
internal sealed class CaptionTranscript
{
    /// <summary>
    /// A ceiling on what is kept, so a window left open for days cannot grow without bound. At
    /// conversational speed this is upwards of ten hours of speech.
    /// </summary>
    public const int MaxLength = 500_000;

    /// <summary>How much comes off when the ceiling is reached, so trimming stays rare.</summary>
    private const int TrimLength = 50_000;

    /// <summary>
    /// How many words two readings must put in the same places before one is taken to be a
    /// revision of the other rather than something new being said.
    /// </summary>
    private const int MinAlignedWords = 3;

    /// <summary>
    /// How much of the live window may be dropped with nothing inserted before the reading is
    /// treated as an overlay rather than a rewrite. A few tentative words is normal; a whole
    /// sentence vanishing is the gear menu covering the captions.
    /// </summary>
    private const int MinOverlayDrop = 40;

    private readonly StringBuilder _committed = new();

    /// <summary>The part of the transcript Live Captions is still showing and may rewrite.</summary>
    private string _window = string.Empty;

    private bool _owesSeparator;

    public string Text { get; private set; } = string.Empty;

    /// <summary>Folds what Live Captions is showing now into the transcript.</summary>
    public string Absorb(string window)
    {
        var edit = CaptionTextEdit.BetweenWindows(_window, window);

        if (IsTransientOverlay(edit, window))
        {
            // The Live Captions flyout (or any other overlay) can share a short prefix with
            // the last reading and look like a rewritten tail. Believing that would throw
            // away the rest of the live window, which is how the transcript went blank.
            return Text;
        }

        if (edit.KeptLength > 0)
        {
            // The windows share a run of text, so this is the same stretch of speech: whatever
            // slid off the front is never coming back from Live Captions and is now history,
            // and the rest is still live.
            _committed.Append(_window, 0, edit.Slide);
        }
        else if (_window.Length > 0)
        {
            if (TryAlignWords(_window, window, out var scrolledOut))
            {
                // Not a character in common, but the words still line up: this is the same
                // speech recognised differently, which Live Captions does constantly as it
                // grows more confident. Only what the alignment leaves behind the new window
                // is history; committing the rest would say it all twice.
                _committed.Append(_window, 0, scrolledOut);
            }
            else
            {
                // The words do not line up either, so Live Captions started over — which is
                // what it does when speech stops. All of the old window is history.
                _committed.Append(_window);
                _owesSeparator = true;
            }
        }

        _window = window;

        // The space that keeps the last word clear of the next one is only owed once something
        // follows it. Adding it at the break would leave the transcript ending in a space for
        // however long the room stays quiet.
        if (_owesSeparator && _window.Length > 0)
        {
            _committed.Append(' ');
            _owesSeparator = false;
        }

        Trim();

        Text = _committed.Length == 0 ? _window : _committed.ToString() + _window;
        return Text;
    }

    public void Clear()
    {
        _committed.Clear();
        _window = string.Empty;
        _owesSeparator = false;
        Text = string.Empty;
    }

    /// <summary>
    /// Live Captions never silently deletes a long run of already-shown words without putting
    /// anything in their place. A reading that only truncates a large tail is the gear menu
    /// (or similar chrome) covering the text, not a rewrite.
    /// </summary>
    private static bool IsTransientOverlay(CaptionTextEdit edit, string newWindow)
    {
        if (newWindow.Length == 0)
        {
            return false;
        }

        return edit.Slide == 0
            && edit.KeptLength > 0
            && edit.RemovedLength >= MinOverlayDrop
            && edit.InsertedLength * 2 < edit.RemovedLength;
    }

    /// <summary>
    /// Works out how far the window scrolled by lining the two readings up word by word.
    /// </summary>
    /// <remarks>
    /// Comparing characters is not enough here. Live Captions revises what it is showing as it
    /// grows more confident — "worked" becomes "walked", a comma appears, a word is capitalised
    /// — and a single such change early on leaves two readings of the same sentence with
    /// nothing in common at the front. Words survive that, so the reading that puts the most of
    /// them in the same places is taken to be the truth.
    /// </remarks>
    /// <param name="scrolledOut">Characters of the old window that are now history.</param>
    private static bool TryAlignWords(string oldWindow, string newWindow, out int scrolledOut)
    {
        scrolledOut = 0;

        var (oldWords, offsets) = SplitWords(oldWindow);
        var (newWords, _) = SplitWords(newWindow);
        if (oldWords.Length == 0 || newWords.Length == 0)
        {
            return false;
        }

        var bestSlide = -1;
        var bestMatched = 0;

        for (var slide = 0; slide < oldWords.Length; slide++)
        {
            var overlap = Math.Min(oldWords.Length - slide, newWords.Length);
            var matched = 0;
            for (var i = 0; i < overlap; i++)
            {
                if (string.Equals(oldWords[slide + i], newWords[i], StringComparison.Ordinal))
                {
                    matched++;
                }
            }

            // Half the overlapping words have to agree, so that a couple of coincidences in
            // otherwise unrelated speech is not read as an alignment.
            if (matched > bestMatched && matched * 2 >= overlap)
            {
                bestSlide = slide;
                bestMatched = matched;
            }
        }

        if (bestSlide < 0 || bestMatched < Math.Min(MinAlignedWords, oldWords.Length))
        {
            return false;
        }

        scrolledOut = offsets[bestSlide];
        return true;
    }

    /// <summary>
    /// The words of a window, stripped to their letters and digits so that punctuation and
    /// capitalisation cannot make one reading look unlike another, with where each one starts.
    /// </summary>
    private static (string[] Words, int[] Offsets) SplitWords(string text)
    {
        var words = new List<string>();
        var offsets = new List<int>();
        var at = 0;

        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            if (at == text.Length)
            {
                break;
            }

            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            offsets.Add(start);
            words.Add(Letters(text.AsSpan(start, at - start)));
        }

        return (words.ToArray(), offsets.ToArray());
    }

    private static string Letters(ReadOnlySpan<char> word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var character in word)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private void Trim()
    {
        if (_committed.Length + _window.Length <= MaxLength)
        {
            return;
        }

        var cut = Math.Min(
            _committed.Length + _window.Length - MaxLength + TrimLength,
            _committed.Length);

        // Cutting mid-word would leave half of one at the top of the transcript, so the rest of
        // it goes, and then the space after it so the text starts on a word.
        while (cut < _committed.Length && !char.IsWhiteSpace(_committed[cut]))
        {
            cut++;
        }

        while (cut < _committed.Length && char.IsWhiteSpace(_committed[cut]))
        {
            cut++;
        }

        _committed.Remove(0, cut);
    }
}
