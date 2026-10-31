namespace CaptionBridge.Services;

/// <summary>
/// How the mirrored text moved between two polls. Live Captions does three things to its text:
/// it appends new words, rewrites the last few while recognition settles, and drops whole lines
/// off the front once the window is full. Describing a refresh this way is what lets the box be
/// brought up to date by touching only the words that actually changed.
/// </summary>
/// <param name="Slide">Characters dropped off the front of the old text.</param>
/// <param name="KeptLength">Characters after the slide that both texts still share.</param>
/// <param name="RemovedLength">Characters the old text had after the shared run.</param>
/// <param name="InsertedLength">Characters the new text has after the shared run.</param>
internal readonly record struct CaptionTextEdit(int Slide, int KeptLength, int RemovedLength, int InsertedLength)
{
    /// <summary>
    /// A front trim is only believed when this many characters line up, so a short caption is
    /// never mistaken for a slide of a longer one.
    /// </summary>
    private const int MinSlideOverlap = 12;

    /// <summary>
    /// How many places the leading words may be looked for before settling for the best found
    /// so far. Bounds the search over a transcript that repeats itself.
    /// </summary>
    private const int MaxSlideCandidates = 64;

    /// <summary>
    /// Compares two versions of the same growing text, where anything shared sits at the front.
    /// A front trim is only looked for when nothing shared is found, because the search costs
    /// more than the comparison and a growing text never slides.
    /// </summary>
    public static CaptionTextEdit Between(string oldText, string newText)
    {
        var kept = CommonPrefixLength(oldText, newText);
        return kept > 0 ? Make(oldText, newText, 0, kept) : Slid(oldText, newText);
    }

    /// <summary>
    /// Compares two readings of a window that scrolls.
    /// </summary>
    /// <remarks>
    /// Here a run of leading characters that happen to agree is not evidence the text stayed
    /// put: two stretches of the same conversation often start alike, and reading a scroll as a
    /// rewrite would throw away everything that came off the front. So the text is read both
    /// ways and whichever reading accounts for more of it wins.
    /// </remarks>
    public static CaptionTextEdit BetweenWindows(string oldText, string newText)
    {
        var kept = CommonPrefixLength(oldText, newText);
        var slid = Slid(oldText, newText);
        return slid.KeptLength > kept ? slid : Make(oldText, newText, 0, kept);
    }

    public static int CommonPrefixLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var max = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < max && left[length] == right[length])
        {
            length++;
        }

        return length > 0 && char.IsHighSurrogate(left[length - 1]) ? length - 1 : length;
    }

    private static CaptionTextEdit Make(string oldText, string newText, int slide, int kept) =>
        new(slide, kept, oldText.Length - slide - kept, newText.Length - kept);

    private static CaptionTextEdit Slid(string oldText, string newText)
    {
        var (slide, kept) = FindSlide(oldText, newText);
        return Make(oldText, newText, slide, kept);
    }

    /// <summary>
    /// Finds how far the text slid by looking for where the new text starts inside the old one.
    /// Speech repeats itself, so the leading words can turn up in several places and the first
    /// is not always the right one; each candidate is measured and the longest run wins.
    /// </summary>
    private static (int Slide, int KeptLength) FindSlide(string oldText, string newText)
    {
        if (newText.Length < MinSlideOverlap || oldText.Length <= MinSlideOverlap)
        {
            return (0, 0);
        }

        var probe = newText.AsSpan(0, MinSlideOverlap);
        var best = (Slide: 0, KeptLength: 0);

        // A match at the very front is the plain prefix rather than a slide, so the search
        // starts past it. The cap keeps repetitive text from turning this into a long scan.
        var from = 1;
        for (var candidate = 0; candidate < MaxSlideCandidates; candidate++)
        {
            if (from > oldText.Length - MinSlideOverlap)
            {
                break;
            }

            var at = oldText.AsSpan(from).IndexOf(probe);
            if (at < 0)
            {
                break;
            }

            var slide = from + at;
            var kept = CommonPrefixLength(oldText.AsSpan(slide), newText);
            if (kept > best.KeptLength)
            {
                best = (slide, kept);
            }

            from = slide + 1;
        }

        return best.KeptLength >= MinSlideOverlap ? best : (0, 0);
    }

    public int NewLength => KeptLength + InsertedLength;

    /// <summary>
    /// Translates an offset in the old text to the character it sits at now. Only a front
    /// scroll moves it, and then only by how much was deleted. A rewritten tail must not pull
    /// the first click backward — that is the start point the user asked to keep still.
    /// An offset at the old end stays at the new end.
    /// </summary>
    public int MapOffset(int offset, int oldLength)
    {
        if (offset >= oldLength)
        {
            return NewLength;
        }

        return Math.Clamp(offset - Slide, 0, NewLength);
    }

    public int MapOffset(int offset) => MapOffset(offset, int.MaxValue);
}
