using CaptionBridge.Services;

namespace CaptionBridge.Tests;

public class CaptionTranscriptTests
{
    private static int CountOf(string text, string word) =>
        text.Split(' ').Count(part => part.Trim('.', ',', '?').Equals(word, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void StartsEmpty()
    {
        Assert.Equal(string.Empty, new CaptionTranscript().Text);
    }

    [Fact]
    public void ShowsTheFirstWindowAsItIs()
    {
        var transcript = new CaptionTranscript();

        Assert.Equal("hello there", transcript.Absorb("hello there"));
    }

    [Fact]
    public void FollowsWordsBeingAppended()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("hello there");

        Assert.Equal("hello there everyone", transcript.Absorb("hello there everyone"));
    }

    [Fact]
    public void TakesTheLatestVersionOfARewrittenTail()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("i think the");

        // Recognition settled on a different last word; the old one is not history, it is wrong.
        Assert.Equal("i think that", transcript.Absorb("i think that"));
    }

    [Fact]
    public void KeepsWhatScrollsOutOfTheWindow()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("one two three four five six seven");

        var text = transcript.Absorb("three four five six seven eight nine");

        Assert.Equal("one two three four five six seven eight nine", text);
    }

    [Fact]
    public void KeepsScrollingWithoutLosingTheStart()
    {
        var transcript = new CaptionTranscript();
        var window = string.Empty;
        var text = string.Empty;

        // A window three segments wide, walked across nine of them.
        var segments = Enumerable.Range(1, 9).Select(i => $"segment number {i}").ToArray();
        for (var i = 0; i < segments.Length; i++)
        {
            window = string.Join(' ', segments.Skip(Math.Max(0, i - 2)).Take(Math.Min(i + 1, 3)));
            text = transcript.Absorb(window);
        }

        Assert.Equal(string.Join(' ', segments), text);
        Assert.EndsWith(window, text);
    }

    [Fact]
    public void KeepsTheOldSentenceWhenLiveCaptionsStartsOver()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("what a lovely afternoon it is");

        // Speech stopped and picked up again on something unrelated.
        var text = transcript.Absorb("completely different topic now");

        Assert.Equal("what a lovely afternoon it is completely different topic now", text);
    }

    /// <summary>
    /// Taken from the live bug: chrome leaked into the start of the reading, then opening the
    /// gear menu left only that chrome. A shared prefix made the rest of the speech look like
    /// a rewritten tail and the transcript went blank.
    /// </summary>
    [Fact]
    public void DoesNotDropSpeechWhenTheSettingsFlyoutCoversTheWindow()
    {
        var transcript = new CaptionTranscript();
        var speech = "can you walk me through your background and explain how you experience a Salesforce and Amazon prepared you for senior level AI and background engineering work";
        var withChrome = "Change language Preferences Learn more " + speech;

        transcript.Absorb(withChrome);
        var text = transcript.Absorb("Change language Preferences Learn more Change language Preferences Learn more");

        Assert.Equal(withChrome, text);
        Assert.Contains("Salesforce", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that makes a naive merge duplicate whole sentences: the words did not change,
    /// only the capital letter and the comma, so the text differs from its first character.
    /// </summary>
    [Fact]
    public void DoesNotDuplicateAWindowThatWasOnlyRepunctuated()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("hello there how are you");

        var text = transcript.Absorb("Hello there, how are you?");

        Assert.Equal("Hello there, how are you?", text);
    }

    [Fact]
    public void DoesNotDuplicateWhenPunctuationArrivesMidStream()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("so walk me through the design");
        transcript.Absorb("So, walk me through the design");

        var text = transcript.Absorb("So, walk me through the design of a rate limiter");

        Assert.Equal("So, walk me through the design of a rate limiter", text);
    }

    /// <summary>
    /// Taken from a real run. Live Captions had heard "worked" and settled on "walked", which
    /// leaves two readings of one sentence with nothing in common from the fifth character on.
    /// Reading that as new speech is what puts the sentence in the transcript twice.
    /// </summary>
    [Fact]
    public void DoesNotRepeatASentenceThatWasRecognisedAgainDifferently()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("yeah I recently worked on production rep AI agent that helped users");

        var text = transcript.Absorb("Yeah, I recently walked on production rep AI agent that helped users find information");

        Assert.Equal("Yeah, I recently walked on production rep AI agent that helped users find information", text);
    }

    [Fact]
    public void KeepsTheStartWhenAScrollAndARewriteLandTogether()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("so we built a retrieval argument to generate a pipeline so the model could answer");

        // Scrolled on, and the opening words came back recognised differently.
        var text = transcript.Absorb("we begin to retrieval augmented generation pipeline so the model could answer using sources");

        Assert.StartsWith("so we built ", text);
        Assert.EndsWith("pipeline so the model could answer using sources", text);

        // The revised reading replaces the old one rather than being appended after it.
        Assert.DoesNotContain("could answer using sources pipeline", text);
        Assert.Equal(1, CountOf(text, "pipeline"));
    }

    [Fact]
    public void DoesNotRepeatAWindowThatScrolledAndWasRepunctuated()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("when a user submitted a question the system first analyzed request");

        var text = transcript.Absorb("When a user submitted a question, the system first analyzed request. It converted the question");

        Assert.Equal(1, CountOf(text, "submitted"));
        Assert.EndsWith("It converted the question", text);
    }

    [Fact]
    public void KeepsTheTranscriptWhenLiveCaptionsCloses()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("this was said before it closed");

        Assert.Equal("this was said before it closed", transcript.Absorb(string.Empty));
    }

    [Fact]
    public void CarriesOnAfterLiveCaptionsComesBack()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("said before it closed");
        transcript.Absorb(string.Empty);

        var text = transcript.Absorb("said after it came back");

        Assert.Equal("said before it closed said after it came back", text);
    }

    [Fact]
    public void ForgetsEverythingOnlyWhenAsked()
    {
        var transcript = new CaptionTranscript();
        transcript.Absorb("one two three four five");
        transcript.Clear();

        Assert.Equal(string.Empty, transcript.Text);
        Assert.Equal("a fresh start", transcript.Absorb("a fresh start"));
    }

    [Fact]
    public void StopsGrowingAtTheCeiling()
    {
        var transcript = new CaptionTranscript();
        var text = string.Empty;
        var window = string.Empty;

        // Tagging every word, with a rotating letter in front so that consecutive windows
        // differ from their very first character, is what makes each window share nothing at
        // all with the one before it. So every one is committed whole and the transcript grows.
        for (var i = 0; i < 12000; i++)
        {
            var tag = $"{(char)('a' + (i % 26))}{i}";
            window = $"{tag}alpha {tag}beta {tag}gamma {tag}delta {tag}epsilon {tag}zeta {tag}eta";
            text = transcript.Absorb(window);
        }

        Assert.True(text.Length <= CaptionTranscript.MaxLength, $"length was {text.Length}");
        Assert.True(text.Length > CaptionTranscript.MaxLength / 2, $"length was {text.Length}");

        // Trimming comes off the front, and never mid-word.
        Assert.EndsWith(window, text);
        Assert.False(char.IsWhiteSpace(text[0]), $"started with '{text[..20]}'");
    }
}
