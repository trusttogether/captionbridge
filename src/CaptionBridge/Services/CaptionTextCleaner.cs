namespace CaptionBridge.Services;

/// <summary>
/// Turns the raw text fragments read out of the Live Captions window into the single
/// line of speech the window is currently showing. This is deliberately a mirror: it
/// never merges history, re-orders text, or guesses at a "better" transcript.
/// </summary>
internal static class CaptionTextCleaner
{
    private const string ReadyPrefix = "Ready to show live captions";

    private static readonly HashSet<string> ChromeLines = new(StringComparer.OrdinalIgnoreCase)
    {
        "Live captions",
        "Caption style",
        "Position",
        "Language",
        "Settings",
        "Pause",
        "Resume",
        "Close",
        "Minimize",
        "Maximize",
        "Restore",
        "Dock",
        "Undock",
        "Filter profanity",
        "Include microphone audio",
    };

    public static string Clean(IReadOnlyList<string> fragments)
    {
        if (fragments.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>(fragments.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            foreach (var rawLine in fragment.Split('\r', '\n'))
            {
                var line = StripReadyBanner(CollapseWhitespace(rawLine));
                if (line.Length == 0 || ChromeLines.Contains(line))
                {
                    continue;
                }

                if (seen.Add(line))
                {
                    lines.Add(line);
                }
            }
        }

        return string.Join(' ', lines);
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Live Captions prefixes its text area with "Ready to show live captions in &lt;language&gt;"
    /// until speech starts, and briefly keeps it glued to the first words that arrive.
    /// </summary>
    private static string StripReadyBanner(string line)
    {
        if (!line.StartsWith(ReadyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var languageEnd = line.IndexOf(')');
        return languageEnd < 0 ? string.Empty : line[(languageEnd + 1)..].Trim();
    }
}
