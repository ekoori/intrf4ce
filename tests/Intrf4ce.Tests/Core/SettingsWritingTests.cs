using Intrf4ce.Core.Settings;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// Changing a setting from the panel, or from the agent: one line of settings.yaml, and
/// nothing else about the file disturbed.
/// </summary>
public class SettingsWritingTests
{
    [Fact]
    public void ChangingOneSettingLeavesTheRestOfTheFileAlone()
    {
        var before = SettingsFile.DefaultContent;
        var after = SettingsFile.Set(before, "narration.subtitles", "false");

        Assert.Contains("subtitles: false", after, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitles: true", after, StringComparison.Ordinal);

        // The comments that explain the file are what make it worth editing by hand.
        Assert.Contains("# auto | manual: what a slide does when its narration ends", after, StringComparison.Ordinal);
        Assert.Equal(Lines(before), Lines(after));

        var path = Written(after);
        try
        {
            var load = SettingsFile.Load(path);
            Assert.Null(load.Error);
            Assert.Empty(load.Warnings ?? []);
            Assert.False(load.Settings.Narration.Subtitles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheCommentAfterAValueSurvivesTheChange()
    {
        var before = Line(SettingsFile.DefaultContent, "advance:");
        var after = Line(SettingsFile.Set(SettingsFile.DefaultContent, "narration.advance", "manual"), "advance:");

        Assert.StartsWith("  advance: manual", after, StringComparison.Ordinal);
        Assert.EndsWith("# auto | manual: what a slide does when its narration ends", after, StringComparison.Ordinal);

        // And in the column it was in, so the file does not go ragged one edit at a time.
        Assert.Equal(before.IndexOf('#', StringComparison.Ordinal), after.IndexOf('#', StringComparison.Ordinal));
    }

    [Fact]
    public void ASettingTheFileNeverMentionedIsAddedToItsSection()
    {
        var text = SettingsFile.Set(Yaml("narration:", "  volume: 0.5"), "narration.autoplay", "false");

        Assert.Equal(Yaml("narration:", "  volume: 0.5", "  autoplay: false"), Unix(text));
    }

    [Fact]
    public void ASectionTheFileNeverMentionedIsAddedToTheEnd()
    {
        var text = SettingsFile.Set(Yaml("narration:", "  volume: 0.5"), "stage.follow_agent", "true");

        Assert.Equal(Yaml("narration:", "  volume: 0.5", "", "stage:", "  follow_agent: true"), Unix(text));
    }

    /// <summary>
    /// The agent writes settings through the same door as the panel, so a value that would
    /// make no sense comes back as words rather than being written and left to break the app.
    /// </summary>
    [Fact]
    public void AValueThatWillNotDoIsRefusedWithWhatWouldHave()
    {
        var path = Path.Combine(Path.GetTempPath(), "intrf4ce-settings-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            Assert.Contains("auto, manual", SettingsFile.Write(path, "narration.advance", "sideways") ?? "", StringComparison.Ordinal);
            Assert.Contains("between 0 and 1", SettingsFile.Write(path, "narration.volume", "11") ?? "", StringComparison.Ordinal);
            Assert.Contains("true or false", SettingsFile.Write(path, "narration.subtitles", "yes") ?? "", StringComparison.Ordinal);
            Assert.Contains("narration.volume", SettingsFile.Write(path, "narration.loudness", "1") ?? "", StringComparison.Ordinal);

            // None of those wrote anything.
            Assert.False(File.Exists(path));

            Assert.Null(SettingsFile.Write(path, "narration.volume", "0.4"));
            Assert.Equal(0.4, SettingsFile.Load(path).Settings.Narration.Volume);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Every setting the panel draws and the agent may set must be one the file understands,
    /// and must read back as a value it would itself accept.
    /// </summary>
    [Fact]
    public void EverySettingInTheSchemaIsOneTheFileUnderstands()
    {
        var settings = new AppSettings();

        foreach (var entry in SettingsSchema.All)
        {
            var value = SettingsSchema.Read(settings, entry.Key);
            var (written, error) = SettingsSchema.Parse(entry, value);

            Assert.Null(error);
            Assert.NotEqual("", entry.Label);
            Assert.Empty(SettingsFile.UnknownKeys(SettingsFile.Set(SettingsFile.DefaultContent, entry.Key, written!)));
        }
    }

    /// <summary>What the app is running with is what the panel shows.</summary>
    [Fact]
    public void WhatTheSchemaReadsIsWhatTheSettingsSay()
    {
        var settings = new AppSettings();
        settings.Narration.Volume = 0.25;
        settings.Narration.Subtitles = false;
        settings.Stage.FollowAgent = true;
        settings.Agent.Model = "opus";

        Assert.Equal("0.25", SettingsSchema.Read(settings, "narration.volume"));
        Assert.Equal("false", SettingsSchema.Read(settings, "narration.subtitles"));
        Assert.Equal("true", SettingsSchema.Read(settings, "stage.follow_agent"));
        Assert.Equal("opus", SettingsSchema.Read(settings, "agent.model"));
    }

    private static int Lines(string text) => text.Split('\n').Length;

    private static string Line(string text, string starting) =>
        text.Split('\n').First(l => l.TrimStart().StartsWith(starting, StringComparison.Ordinal));

    private static string Unix(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Yaml(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string Written(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "intrf4ce-settings-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, text);
        return path;
    }
}
