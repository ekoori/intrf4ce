using Intrf4ce.Core.Commands;
using Intrf4ce.Core.Input;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// Keys become commands the way they do in vim and Fjord: sequences, counts, a pause that
/// forgets a stray key, and every command reachable from the prompt as well.
/// </summary>
public class KeyboardTests
{
    [Theory]
    [InlineData("gg", "gg")]
    [InlineData("G", "G")]
    [InlineData("<ctrl+d>", "<Ctrl+d>")]
    [InlineData("<Shift+g>", "G")]
    [InlineData("<space>", "<Space>")]
    [InlineData("<Esc>", "<Esc>")]
    [InlineData("<Shift+Tab>", "<Shift+Tab>")]
    [InlineData(":", ":")]
    [InlineData("<lt>", "<lt>")]
    [InlineData("<F11>", "<F11>")]
    public void KeysAreWrittenOneCanonicalWay(string written, string canonical) =>
        Assert.Equal(canonical, KeyMap.Canonical(written));

    [Fact]
    public void ANameThatIsNotAKeyIsRefusedInWords()
    {
        var ex = Assert.Throws<FormatException>(() => Chord.Parse("<Hyper+q>"));
        Assert.Contains("Hyper", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASequenceWaitsForItsNextKeyAndThenRuns()
    {
        var map = new KeyMap();
        map.AddDefault("gg", "first");
        map.AddDefault("G", "last");
        var reader = new KeyReader(map);

        Assert.Equal((true, null, null), reader.Feed(Chord.Character('g')));
        Assert.Equal("g", reader.Pending);
        Assert.Equal((true, "first", null), reader.Feed(Chord.Character('g')));
        Assert.Equal((true, "last", null), reader.Feed(Chord.Character('G')));
    }

    [Fact]
    public void ACountInFrontIsHowManyTimes()
    {
        var map = new KeyMap();
        map.AddDefault("l", "next");
        map.AddDefault("0", "reframe");
        var reader = new KeyReader(map);

        reader.Feed(Chord.Character('1'));
        reader.Feed(Chord.Character('0'));
        Assert.Equal((true, "next", 10), reader.Feed(Chord.Character('l')));

        // With nothing typed before it, 0 is a key of its own.
        Assert.Equal((true, "reframe", null), reader.Feed(Chord.Character('0')));
    }

    [Fact]
    public void AStrayKeyIsForgottenAfterAPause()
    {
        var now = DateTimeOffset.UnixEpoch;
        var map = new KeyMap();
        map.AddDefault("gg", "first");
        var reader = new KeyReader(map, () => now);

        reader.Feed(Chord.Character('g'));
        now += TimeSpan.FromSeconds(5);

        // Not "first": the g typed a while ago has gone.
        Assert.Equal((true, null, null), reader.Feed(Chord.Character('g')));
    }

    [Fact]
    public void AnUnboundKeyIsLeftForWhoeverElseWantsIt()
    {
        var reader = new KeyReader(new KeyMap());

        Assert.Equal((false, null, null), reader.Feed(Chord.Character('z')));
    }

    [Fact]
    public void TheUsersBindingsGoOverTheDefaultsAndEmptyUnbinds()
    {
        var map = new KeyMap();
        map.AddDefault("h", "prev");
        map.AddDefault("x", "stop");

        map.Bind("h", "first");
        map.Bind("x", "");

        Assert.Equal((KeyOutcome.Run, "first"), map.Match(Chord.Parse("h")));
        Assert.Equal(KeyOutcome.None, map.Match(Chord.Parse("x")).Outcome);

        map.ClearUser();
        Assert.Equal((KeyOutcome.Run, "stop"), map.Match(Chord.Parse("x")));
    }

    [Fact]
    public void AfterHalfASequenceTheWaysToFinishItAreKnown()
    {
        var map = new KeyMap();
        map.AddDefault("gg", "first");
        map.AddDefault("gv", "revoice");
        map.AddDefault("G", "last");

        Assert.Equal(["gg", "gv"], map.Continuations(Chord.Parse("g")).Select(c => c.Keys));
    }

    // ------------------------------------------------------------------ commands

    private sealed class Commands
    {
        public List<string> Ran { get; } = [];

        [Command("next", Aliases = ["n"], Description = "the next slide")]
        public string Next(int count = 1)
        {
            Ran.Add($"next {count}");
            return $"moved {count}";
        }

        [Command("go", Description = "a slide by number")]
        public void Go(int slide) => Ran.Add($"go {slide}");

        [Command("ask", Description = "ask the agent")]
        public void Ask([Rest] string question) => Ran.Add($"ask {question}");

        [Command("group", Description = "file the session")]
        public void Group([Choices("groups")] string name) => Ran.Add($"group {name}");

        [Command("fail")]
        public void Fail() => throw new CommandException("it did not work");
    }

    [Fact]
    public void ALineRunsTheCommandItNames()
    {
        var commands = new Commands();
        var registry = new CommandRegistry();
        registry.Register(commands);

        Assert.Equal("moved 3", registry.Run("next", count: 3));
        registry.Run(":n");
        registry.Run("ask how deep   is it?");
        registry.Run("group \"Rivers of Africa\"");
        registry.Run(":12");

        Assert.Equal(["next 3", "next 1", "ask how deep   is it?", "group Rivers of Africa", "go 12"], commands.Ran);
    }

    [Fact]
    public void AWrongLineSaysWhatWouldHaveWorked()
    {
        var registry = new CommandRegistry();
        registry.Register(new Commands());

        Assert.Contains("did you mean next", Assert.Throws<CommandException>(() => registry.Run("nxt")).Message, StringComparison.Ordinal);
        Assert.Contains(":go <slide>", Assert.Throws<CommandException>(() => registry.Run("go")).Message, StringComparison.Ordinal);
        Assert.Contains("a whole number", Assert.Throws<CommandException>(() => registry.Run("go five")).Message, StringComparison.Ordinal);
        Assert.Contains("too many", Assert.Throws<CommandException>(() => registry.Run("go 1 2")).Message, StringComparison.Ordinal);
        Assert.Equal("it did not work", Assert.Throws<CommandException>(() => registry.Run("fail")).Message);
    }

    [Fact]
    public void CompletionOffersCommandsThenTheirArguments()
    {
        var registry = new CommandRegistry();
        registry.Register(new Commands());
        registry.Choices["groups"] = () => [new("Rivers", "3 sessions"), new("Rome", "1 session")];

        Assert.Equal(["go", "group"], registry.Complete(":g").Select(c => c.Text));
        Assert.Equal(["Rivers", "Rome"], registry.Complete("group ").Select(c => c.Text));
        Assert.Equal(["Rome"], registry.Complete("group Ro").Select(c => c.Text));
        Assert.Empty(registry.Complete("go "));
    }
}
