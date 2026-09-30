using Intrf4ce.Core.Narration;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// Directions for the voice ride in the narration, where their position means something, and
/// are kept out of everything the user reads and every voice that would read them aloud.
/// </summary>
public class AudioTagsTests
{
    [Fact]
    public void AVoiceThatCannotTakeTagsIsGivenTheWordsAlone()
    {
        var (spoken, map, origin) = AudioTags.Strip("[curious] Is this the river? [excited] It is!");

        Assert.Equal("Is this the river? It is!", spoken);

        // Every character of the original knows where it went, and every spoken one where it came from.
        Assert.Equal(45, map.Length);
        Assert.Equal(spoken.Length, origin.Length);
        Assert.Equal('I', "[curious] Is this the river? [excited] It is!"[origin[0]]);
    }

    [Fact]
    public void CuesAreNotMistakenForTags()
    {
        Assert.False(AudioTags.Any("Here [[focus knee.acl]] is the ligament."));
        Assert.True(AudioTags.Any("[whispers] Here it is."));
        Assert.False(AudioTags.Any("An array [0] and a footnote [1]."));
    }

    [Fact]
    public void TheSubtitleShowsOnlyWhatIsSaid()
    {
        const string Text = "[curious] Is this the river? [excited] It is!";

        Assert.Equal("Is this the river? It is!", AudioTags.Visible(Text, 0, Text.Length));
        Assert.Equal(["Is", "this", "the", "river?", "It", "is!"], Captions.Words(Text).Select(w => Text[w.Start..w.End]));
    }

    /// <summary>A tag takes no time: a cue just after it fires with the word it colours.</summary>
    [Fact]
    public void TimingSpreadOverTheWordsLeavesTagsWithoutDuration()
    {
        const string Text = "[whispers] Look here.";
        var (spoken, map, _) = AudioTags.Strip(Text);
        var timing = SpeechTiming.Spread(Text, SpeechTiming.Estimate(spoken, 2.0), map);

        var look = Text.IndexOf("Look", StringComparison.Ordinal);
        Assert.Equal(timing.TimeAt(0), timing.TimeAt(look));
        Assert.Equal(2.0, timing.Duration);
        Assert.True(timing.TimeAt(Text.IndexOf("here", StringComparison.Ordinal)) > timing.TimeAt(look));
    }
}
