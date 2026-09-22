using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Intrf4ce.Core.Settings;

/// <summary>
/// Everything the user decides and the agent never sees: look, voice, pacing, which agent.
/// </summary>
/// <remarks>
/// The design's first principle, "the agent says what, the app decides how", lives here.
/// Mutable classes rather than records only because YamlDotNet deserialises into setters.
/// </remarks>
public sealed class AppSettings
{
    public NarrationSettings Narration { get; set; } = new();

    /// <summary>
    /// Spelled as one word in YAML: the naming convention would otherwise make it
    /// <c>eleven_labs</c>, and a file that says <c>elevenlabs:</c> was silently ignored.
    /// </summary>
    [YamlMember(Alias = "elevenlabs")]
    public ElevenLabsSettings ElevenLabs { get; set; } = new();

    public SystemVoiceSettings SystemVoice { get; set; } = new();

    public AgentSettings Agent { get; set; } = new();

    public StageSettings Stage { get; set; } = new();

    public LibrarySettings Library { get; set; } = new();
}

public sealed class NarrationSettings
{
    /// <summary>auto (ElevenLabs when a key is set, else the system voice), elevenlabs, system or none.</summary>
    public string Engine { get; set; } = "auto";

    /// <summary>What a slide without its own advance does when the narration ends: auto or manual.</summary>
    public string Advance { get; set; } = "auto";

    /// <summary>Seconds of quiet after the narration before an auto slide moves on.</summary>
    public double PauseAfter { get; set; } = 1.4;

    /// <summary>Characters per second for subtitles when nothing is spoken.</summary>
    public double ReadingSpeed { get; set; } = 16;

    public bool Subtitles { get; set; } = true;

    /// <summary>0 to 1.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Start speaking a slide as soon as it is shown.</summary>
    public bool Autoplay { get; set; } = true;
}

public sealed class ElevenLabsSettings
{
    public string VoiceId { get; set; } = "auq43ws1oslv0tO4BDa7";

    public string ModelId { get; set; } = "eleven_multilingual_v2";

    /// <summary>
    /// A file holding the API key, bare or as KEY=value (another project's .env works).
    /// </summary>
    /// <remarks>
    /// A path rather than a copy of the key, so one key file can serve several tools and
    /// rotating it means editing one file.
    /// </remarks>
    public string KeyFile { get; set; } = "";

    /// <summary>Characters intrf4ce may spend per month; checked against the subscription before each clip.</summary>
    public int MonthlyCap { get; set; } = 30000;

    /// <summary>Stop using ElevenLabs when the account has fewer characters left than this.</summary>
    public int KeepInReserve { get; set; } = 2000;

    public double Stability { get; set; } = 0.5;

    public double SimilarityBoost { get; set; } = 0.75;

    public double Style { get; set; }

    public double Speed { get; set; } = 1.0;
}

public sealed class SystemVoiceSettings
{
    /// <summary>A voice name as the platform lists it, e.g. "Microsoft Catherine"; empty for the default.</summary>
    public string Voice { get; set; } = "";

    /// <summary>-10 (slow) to 10 (fast).</summary>
    public int Rate { get; set; }
}

public sealed class AgentSettings
{
    /// <summary>hosted (the app runs the agent) or attached (you run it and connect it).</summary>
    public string Mode { get; set; } = "hosted";

    public string Command { get; set; } = "claude";

    /// <summary>Model for the hosted agent; empty for Claude Code's default.</summary>
    public string Model { get; set; } = "";

    public string PermissionMode { get; set; } = "dontAsk";

    public List<string> AllowedTools { get; set; } =
        ["mcp__intrf4ce", "WebSearch", "WebFetch", "Read", "Glob", "Grep"];

    /// <summary>Where the hosted agent runs; empty for intrf4ce's own agent folder.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Give the hosted agent only intrf4ce's tools, not every MCP server you have configured.</summary>
    public bool StrictMcp { get; set; } = true;

    /// <summary>Extra arguments appended to the command line.</summary>
    public List<string> ExtraArgs { get; set; } = [];
}

public sealed class StageSettings
{
    /// <summary>Multiplies every animation's duration; 0 turns animation off.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    /// <summary>Jump to a new slide as soon as the agent adds it, even mid-narration.</summary>
    public bool FollowAgent { get; set; }
}

public sealed class LibrarySettings
{
    /// <summary>Where decks are kept; empty for Documents/intrf4ce.</summary>
    public string Folder { get; set; } = "";
}

/// <param name="Error">Why the file could not be used at all (defaults are in force).</param>
/// <param name="Warnings">Settings that were not understood and so had no effect, by line.</param>
public sealed record SettingsLoad(AppSettings Settings, string? Error, IReadOnlyList<string>? Warnings = null);

public static class SettingsFile
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The same, but an unknown key is an error: used only to find typos to report.</summary>
    private static readonly IDeserializer StrictReader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Loads settings, falling back to defaults (and saying why) when the file is broken.</summary>
    public static SettingsLoad Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsLoad(new AppSettings(), null);
        }

        try
        {
            var text = File.ReadAllText(path);
            var settings = Reader.Deserialize<AppSettings>(text) ?? new AppSettings();
            return new SettingsLoad(settings, null, UnknownKeys(text));
        }
        catch (Exception ex) when (ex is YamlException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoad(new AppSettings(), ex.Message);
        }
    }

    /// <summary>
    /// Keys the model does not have, one message each, so a typo is reported rather than
    /// quietly doing nothing. The strict reader stops at the first, so each is removed and
    /// the text re-read until it parses.
    /// </summary>
    public static IReadOnlyList<string> UnknownKeys(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var warnings = new List<string>();
        var lines = text.Split('\n').ToList();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                StrictReader.Deserialize<AppSettings>(string.Join('\n', lines));
                break;
            }
            catch (YamlException ex) when (ex.Start.Line > 0 && ex.Start.Line <= lines.Count && ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                var line = (int)ex.Start.Line;
                warnings.Add($"line {line}: '{lines[line - 1].Trim()}' is not a setting intrf4ce knows; it has no effect");

                // Comment it out and look for the next.
                lines[line - 1] = "#" + lines[line - 1];
            }
            catch (YamlException)
            {
                break;
            }
        }

        return warnings;
    }

    /// <summary>
    /// Copies everything that can change while the app is running into the objects it already
    /// holds, so the voice, the pacing and the stage follow the file without a restart.
    /// </summary>
    /// <returns>The settings that were left alone because they only take effect on startup.</returns>
    public static IReadOnlyList<string> ApplyLive(AppSettings live, AppSettings loaded)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(loaded);

        var later = new List<string>();

        // The engines were chosen and the key read when the app started.
        if (!string.Equals(live.Narration.Engine, loaded.Narration.Engine, StringComparison.OrdinalIgnoreCase))
        {
            later.Add("narration.engine");
        }

        live.Narration.Advance = loaded.Narration.Advance;
        live.Narration.PauseAfter = loaded.Narration.PauseAfter;
        live.Narration.ReadingSpeed = loaded.Narration.ReadingSpeed;
        live.Narration.Subtitles = loaded.Narration.Subtitles;
        live.Narration.Volume = loaded.Narration.Volume;
        live.Narration.Autoplay = loaded.Narration.Autoplay;

        if (live.ElevenLabs.KeyFile != loaded.ElevenLabs.KeyFile)
        {
            later.Add("elevenlabs.key_file");
        }

        live.ElevenLabs.VoiceId = loaded.ElevenLabs.VoiceId;
        live.ElevenLabs.ModelId = loaded.ElevenLabs.ModelId;
        live.ElevenLabs.MonthlyCap = loaded.ElevenLabs.MonthlyCap;
        live.ElevenLabs.KeepInReserve = loaded.ElevenLabs.KeepInReserve;
        live.ElevenLabs.Stability = loaded.ElevenLabs.Stability;
        live.ElevenLabs.SimilarityBoost = loaded.ElevenLabs.SimilarityBoost;
        live.ElevenLabs.Style = loaded.ElevenLabs.Style;
        live.ElevenLabs.Speed = loaded.ElevenLabs.Speed;

        live.SystemVoice.Voice = loaded.SystemVoice.Voice;
        live.SystemVoice.Rate = loaded.SystemVoice.Rate;

        live.Stage.AnimationSpeed = loaded.Stage.AnimationSpeed;
        live.Stage.FollowAgent = loaded.Stage.FollowAgent;

        // The agent is a process started at launch; the library is the folder decks came from.
        if (live.Agent.Mode != loaded.Agent.Mode || live.Agent.Command != loaded.Agent.Command
            || live.Agent.Model != loaded.Agent.Model || live.Agent.PermissionMode != loaded.Agent.PermissionMode
            || live.Agent.WorkingDirectory != loaded.Agent.WorkingDirectory || live.Agent.StrictMcp != loaded.Agent.StrictMcp
            || !live.Agent.AllowedTools.SequenceEqual(loaded.Agent.AllowedTools) || !live.Agent.ExtraArgs.SequenceEqual(loaded.Agent.ExtraArgs))
        {
            later.Add("agent");
        }

        if (live.Library.Folder != loaded.Library.Folder)
        {
            later.Add("library.folder");
        }

        return later;
    }

    /// <summary>The documented default file, as written on first run.</summary>
    public static string DefaultContent => DefaultText;

    public static void WriteDefaultIfMissing(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DefaultText);
    }

    /// <summary>Reads a key from secrets.yaml, a key file, or the environment, in that order.</summary>
    public static string? ElevenLabsKey(AppPaths paths, ElevenLabsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);

        if (Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment.Trim();
        }

        if (File.Exists(paths.SecretsFile))
        {
            try
            {
                var secrets = Reader.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.SecretsFile));
                if (secrets is not null && secrets.TryGetValue("elevenlabs_api_key", out var key) && key.Length > 0)
                {
                    return key.Trim();
                }
            }
            catch (Exception ex) when (ex is YamlException or IOException or UnauthorizedAccessException)
            {
                // A broken secrets file is the same as none; the settings error says which file.
            }
        }

        if (settings.KeyFile.Length > 0 && File.Exists(settings.KeyFile))
        {
            return KeyFromFile(File.ReadAllText(settings.KeyFile));
        }

        return null;
    }

    /// <summary>A key file may hold the bare key or KEY=value lines (the dev/11labs .env format).</summary>
    public static string? KeyFromFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var value = (eq < 0 ? line : line[(eq + 1)..]).Trim().Trim('"', '\'');
            if (value.StartsWith("sk_", StringComparison.Ordinal) || value.Length > 24)
            {
                return value;
            }
        }

        return null;
    }

    private const string DefaultText = """
        # intrf4ce settings. Everything here is yours to decide; the agent never sees this file.
        # Delete a line to get its default back. Saving applies most changes at once; the few that
        # need a restart (the agent, the narration engine) say so when you save.

        narration:
          engine: auto            # auto | elevenlabs | system | none  (auto = ElevenLabs when a key is set)
          advance: auto           # auto | manual: what a slide does when its narration ends
          pause_after: 1.4        # seconds of quiet before an auto slide moves on
          reading_speed: 16       # characters per second for subtitles when nothing is spoken
          subtitles: true
          volume: 1.0
          autoplay: true          # start speaking a slide as soon as it is shown

        elevenlabs:
          voice_id: auq43ws1oslv0tO4BDa7
          model_id: eleven_multilingual_v2
          key_file: ""            # a file holding the key (bare, or KEY=value); or use secrets.yaml,
                                  # or the ELEVENLABS_API_KEY environment variable
          monthly_cap: 30000      # characters intrf4ce may spend per month
          keep_in_reserve: 2000   # stop when the account has fewer than this left
          stability: 0.5
          similarity_boost: 0.75
          style: 0
          speed: 1.0

        system_voice:
          voice: ""               # e.g. Microsoft Catherine; empty for the default voice
          rate: 0                 # -10 to 10

        agent:
          mode: hosted            # hosted: intrf4ce runs Claude Code for you. attached: you connect your own.
          command: claude
          model: ""               # empty for Claude Code's default
          permission_mode: dontAsk
          allowed_tools: [mcp__intrf4ce, WebSearch, WebFetch, Read, Glob, Grep]
          working_directory: ""   # empty for intrf4ce's own agent folder
          strict_mcp: true        # only intrf4ce's tools, not every MCP server you have configured
          extra_args: []

        stage:
          animation_speed: 1.0    # 0 turns animation off
          follow_agent: false     # jump to each new slide as the agent adds it

        library:
          folder: ""              # where decks live; empty for Documents/intrf4ce
        """;
}
