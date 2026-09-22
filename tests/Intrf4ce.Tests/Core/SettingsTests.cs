using Intrf4ce.Core.Settings;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Intrf4ce.Tests.Core;

public class SettingsTests
{
    /// <summary>
    /// The documented default file and the settings model must describe the same settings.
    /// </summary>
    /// <remarks>
    /// They drifted once already: the file said <c>elevenlabs:</c>, the model expected
    /// <c>eleven_labs:</c>, unknown keys were ignored, and the ElevenLabs voice could never be
    /// switched on. Both directions are checked: nothing in the file the model ignores, and
    /// nothing in the model the file does not document.
    /// </remarks>
    [Fact]
    public void TheDefaultFileAndTheModelCoverEachOther()
    {
        Assert.Empty(SettingsFile.UnknownKeys(SettingsFile.DefaultContent));

        var serializer = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
        var modelKeys = Keys(serializer.Serialize(new AppSettings()));
        var documentedKeys = Keys(SettingsFile.DefaultContent);

        Assert.Empty(modelKeys.Except(documentedKeys));
    }

    [Fact]
    public void TheDefaultFileParsesToTheDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "intrf4ce-settings-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            SettingsFile.WriteDefaultIfMissing(path);
            var load = SettingsFile.Load(path);

            Assert.Null(load.Error);
            Assert.Empty(load.Warnings ?? []);
            Assert.Equal("auq43ws1oslv0tO4BDa7", load.Settings.ElevenLabs.VoiceId);
            Assert.Equal(30000, load.Settings.ElevenLabs.MonthlyCap);
            Assert.Equal("hosted", load.Settings.Agent.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TyposAreReportedWithTheirLine()
    {
        const string text = "narration:\n  engin: none\n  volume: 0.5\nelevenlabs:\n  key_file: x\n";

        var warnings = SettingsFile.UnknownKeys(text);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("line 2", warning, StringComparison.Ordinal);
        Assert.Contains("engin", warning, StringComparison.Ordinal);
    }

    /// <summary>Every "section.key" in a YAML document, ignoring comments.</summary>
    private static HashSet<string> Keys(string yaml)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? section = null;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Split('#')[0].TrimEnd();
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('-'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            if (!char.IsWhiteSpace(line[0]))
            {
                section = name;
            }
            else if (section is not null)
            {
                keys.Add(section + "." + name);
            }
        }

        return keys;
    }
}
