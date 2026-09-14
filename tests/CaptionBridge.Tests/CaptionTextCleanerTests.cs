using CaptionBridge.Services;

namespace CaptionBridge.Tests;

public class CaptionTextCleanerTests
{
    [Fact]
    public void ReturnsEmptyForNoFragments()
    {
        Assert.Equal(string.Empty, CaptionTextCleaner.Clean([]));
    }

    [Fact]
    public void ReturnsEmptyWhenEveryFragmentIsBlank()
    {
        Assert.Equal(string.Empty, CaptionTextCleaner.Clean(["", "   ", "\r\n"]));
    }

    [Fact]
    public void KeepsCaptionTextVerbatim()
    {
        Assert.Equal(
            "tell me about a time you disagreed",
            CaptionTextCleaner.Clean(["tell me about a time you disagreed"]));
    }

    [Fact]
    public void CollapsesInternalWhitespace()
    {
        Assert.Equal("so what happened next", CaptionTextCleaner.Clean(["  so   what \t happened  next  "]));
    }

    [Fact]
    public void JoinsWrappedLinesIntoOneRun()
    {
        Assert.Equal(
            "the first part of the sentence and the rest of it",
            CaptionTextCleaner.Clean(["the first part of the sentence\r\nand the rest of it"]));
    }

    [Fact]
    public void DropsWindowChrome()
    {
        Assert.Equal(
            "hello there",
            CaptionTextCleaner.Clean(["Live captions", "Settings", "hello there", "Pause"]));
    }

    [Fact]
    public void DropsTheReadyBannerWhenItStandsAlone()
    {
        Assert.Equal(
            string.Empty,
            CaptionTextCleaner.Clean(["Ready to show live captions in English (United States)"]));
    }

    [Fact]
    public void KeepsSpeechGluedToTheReadyBanner()
    {
        Assert.Equal(
            "good morning everyone",
            CaptionTextCleaner.Clean(["Ready to show live captions in English (United States) good morning everyone"]));
    }

    [Fact]
    public void DeduplicatesIdenticalFragments()
    {
        Assert.Equal("same line", CaptionTextCleaner.Clean(["same line", "SAME LINE", "same line"]));
    }

    [Fact]
    public void PreservesFragmentOrder()
    {
        Assert.Equal("first second third", CaptionTextCleaner.Clean(["first", "second", "third"]));
    }

    [Fact]
    public void DoesNotReorderOrRewriteOverlappingText()
    {
        // The mirror must show what Live Captions shows; picking a "better" alternative
        // is the guessing behaviour this app deliberately leaves out.
        Assert.Equal(
            "i think the i think the answer",
            CaptionTextCleaner.Clean(["i think the", "i think the answer"]));
    }
}
