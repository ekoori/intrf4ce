using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Intrf4ce.Core.Narration;

namespace Intrf4ce.Tests.Core;

public class NarrationTests
{
    [Fact]
    public void CuesAreRemovedAndBoundToTheFollowingWord()
    {
        var parsed = CueParser.Parse("Four ligaments. [[focus knee.acl]] Inside the joint, [[knee.pcl]]the PCL.");

        Assert.Equal("Four ligaments. Inside the joint, the PCL.", parsed.Text);
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, parsed.Cues.Count);
        Assert.Equal("Inside", parsed.Text[parsed.Cues[0].Offset..].Split(' ')[0]);
        Assert.Equal("knee", parsed.Cues[0].ElementId);
        Assert.Equal("acl", parsed.Cues[0].PartId);
        Assert.Equal(CueKind.Focus, parsed.Cues[1].Kind);
        Assert.StartsWith("the PCL", parsed.Text[parsed.Cues[1].Offset..], StringComparison.Ordinal);
    }

    [Fact]
    public void ACueBeforeTheFirstWordFiresAtZero()
    {
        var parsed = CueParser.Parse("[[show chart]] Sales rose.");

        Assert.Equal("Sales rose.", parsed.Text);
        Assert.Equal(new Cue(CueKind.Show, 0, "chart"), parsed.Cues[0]);
    }

    [Fact]
    public void ACueBeforePunctuationLeavesNoStraySpace()
    {
        var parsed = CueParser.Parse("the patella, your kneecap [[zoom knee 0.4 0.1 0.3 0.3]], which protects it.");

        Assert.Equal("the patella, your kneecap, which protects it.", parsed.Text);
        Assert.StartsWith(", which", parsed.Text[parsed.Cues[0].Offset..], StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomTakesARegion()
    {
        var parsed = CueParser.Parse("Look here [[zoom map 0.1 0.2 0.3 0.4]] closely.");

        var cue = Assert.Single(parsed.Cues);
        Assert.Equal(CueKind.Zoom, cue.Kind);
        Assert.Equal([0.1, 0.2, 0.3, 0.4], cue.Region!);
    }

    [Theory]
    [InlineData("[[jump x]]")]
    [InlineData("[[zoom x 2 0 1 1]]")]
    [InlineData("[[show knee.acl]]")]
    [InlineData("[[ ]]")]
    public void MalformedCuesReportAnError(string narration)
    {
        var parsed = CueParser.Parse(narration);

        Assert.Single(parsed.Errors);
        Assert.Empty(parsed.Cues);
    }

    [Property]
    public Property StrippingCuesKeepsEveryWordAndOffsetsStayInBounds(NonEmptyString[] words, bool[] cueAfter)
    {
        var clean = words.Select(w => new string(w.Get.Where(char.IsLetter).ToArray())).Where(w => w.Length > 0).ToArray();
        var withCues = string.Join(" ", clean.Select((w, i) =>
            i < cueAfter.Length && cueAfter[i] ? $"{w} [[focus e.p{i}]]" : w));

        var parsed = CueParser.Parse(withCues);

        var sameWords = parsed.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(clean);
        var ordered = parsed.Cues.Zip(parsed.Cues.Skip(1)).All(p => p.First.Offset <= p.Second.Offset);
        var inBounds = parsed.Cues.All(c => c.Offset >= 0 && c.Offset <= parsed.Text.Length);
        var noDoubleSpaces = !parsed.Text.Contains("  ", StringComparison.Ordinal);

        return (sameWords && ordered && inBounds && noDoubleSpaces).ToProperty();
    }

    [Fact]
    public void EstimatedTimingIsMonotonicAndSpansTheClip()
    {
        var timing = SpeechTiming.Estimate("Hello there. How are you today?", 3.0);

        Assert.True(timing.TimeAt(0) < 0.2);
        Assert.True(timing.TimeAt(timing.Text.Length - 1) < 3.0);
        Assert.Equal(3.0, timing.TimeAt(timing.Text.Length));
        for (var i = 1; i < timing.Text.Length; i++)
        {
            Assert.True(timing.TimeAt(i) >= timing.TimeAt(i - 1));
        }

        // The full stop costs more time than a letter.
        var stop = timing.Text.IndexOf('.', StringComparison.Ordinal);
        Assert.True(timing.TimeAt(stop + 1) - timing.TimeAt(stop) > timing.TimeAt(2) - timing.TimeAt(1));
    }

    [Fact]
    public void CharacterAlignmentIsUsedWhereItMatches()
    {
        const string text = "Hi you";
        var characters = new[] { "H", "i", " ", "y", "o", "u" };
        var starts = new[] { 0.0, 0.1, 0.2, 0.5, 0.6, 0.7 };

        var timing = SpeechTiming.FromCharacters(text, characters, starts, 1.0);

        Assert.True(timing.IsExact);
        Assert.Equal(0.5, timing.TimeAt(3));
        Assert.Equal(3, timing.OffsetAt(0.55));
        Assert.Equal(-1, timing.OffsetAt(-0.1));
    }

    [Fact]
    public void AlignmentSurvivesAVoiceThatNormalisedTheText()
    {
        // The voice spelled out "2" as "two"; the rest still lines up.
        const string text = "Page 2 now";
        var characters = "Page two now".Select(c => c.ToString()).ToArray();
        var starts = Enumerable.Range(0, characters.Length).Select(i => i * 0.1).ToArray();

        var timing = SpeechTiming.FromCharacters(text, characters, starts, 1.5);

        Assert.Equal(0.9, timing.TimeAt(text.IndexOf("now", StringComparison.Ordinal)), 3);
    }

    [Fact]
    public void PausesPullSentenceStartsOntoTheSilences()
    {
        var timing = SpeechTiming.Estimate("First sentence here. Second one.", 4.0);
        var second = "First sentence here. ".Length;

        var aligned = timing.AlignToPauses([(1.9, 2.4)]);

        Assert.Equal(2.4, aligned.TimeAt(second), 3);
        Assert.True(aligned.TimeAt(second - 3) < 2.4);
    }

    [Fact]
    public void CaptionsSplitAtSentencesAndLongSentencesAtClauses()
    {
        const string text = "Short one. This sentence is rather long, and it keeps going with more words so it has to be split somewhere sensible. Done.";

        var spans = Captions.Split(text, maxLength: 60);

        Assert.Equal("Short one.", text[spans[0].Start..spans[0].End]);
        Assert.True(spans.All(s => s.Length <= 60));
        Assert.Equal("Done.", text[spans[^1].Start..spans[^1].End]);
        Assert.Contains(spans, s => text[s.Start..s.End].EndsWith("long,", StringComparison.Ordinal));
    }

    [Fact]
    public void DecimalsAndAbbreviationsDoNotEndSentences()
    {
        const string text = "It weighs 3.5 kg, e.g. a laptop. Next.";

        var spans = Captions.Split(text);

        Assert.Equal(2, spans.Count);
    }
}
