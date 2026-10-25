using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public PluginsSettings Plugins { get; set; } = new();

    /// <summary>
    /// The user's own key bindings, over the ones intrf4ce ships: keys to a command, as typed
    /// after <c>:</c>. An empty command takes a default binding away.
    /// </summary>
    /// <remarks>
    /// Keys are written as in Fjord and vim — <c>gg</c>, <c>G</c>, <c>&lt;Ctrl+d&gt;</c>,
    /// <c>&lt;Space&gt;</c> — and quoted in the file, since several of them mean something
    /// to YAML. <c>?</c> or <c>:help</c> lists every command and where it is bound.
    /// </remarks>
    public Dictionary<string, string> Keys
    {
        get => _keys;
        set => _keys = value ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private Dictionary<string, string> _keys = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The voice is silenced: subtitles run at reading speed and nothing is sent to a voice.
    /// </summary>
    /// <remarks>
    /// Kept in the file, not just on the button, because its point is to spend nothing: a
    /// presentation built while muted is not narrated behind the user's back, and neither is
    /// one opened after a restart. Unmuting speaks the slide on screen, and then each as it
    /// comes.
    /// </remarks>
    public bool Muted { get; set; }
}

public sealed class ElevenLabsSettings
{
    public string VoiceId { get; set; } = "auq43ws1oslv0tO4BDa7";

    /// <summary>
    /// v3 by default: the most natural and expressive model, and the one that takes audio
    /// tags — <c>[curious]</c>, <c>[whispers]</c> — as directions rather than words.
    /// </summary>
    public string ModelId { get; set; } = "eleven_v3";

    /// <summary>The key itself. Read first; the file, secrets.yaml and the environment are fallbacks.</summary>
    /// <remarks>
    /// Never shown in the settings panel, never given to the agent, never written to the log.
    /// </remarks>
    public string ApiKey { get; set; } = "";

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

    /// <summary>
    /// How many clips intrf4ce asks for at once. Plans allow only a few requests in parallel
    /// (three on Starter) and count every application using the key, so this stays below it.
    /// </summary>
    public int MaxConcurrent { get; set; } = 2;

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

    /// <summary>
    /// Which agent the app runs: <c>claude</c>, Claude Code on a Claude account, or
    /// <c>codex</c>, OpenAI's Codex on a ChatGPT account.
    /// </summary>
    public string Provider { get; set; } = "claude";

    /// <summary>The command that runs the agent; empty for the provider's own, <c>claude</c> or <c>codex</c>.</summary>
    public string Command { get; set; } = "";

    /// <summary>Model for the hosted agent; empty for the provider's own default.</summary>
    public string Model { get; set; } = "";

    [YamlIgnore]
    public bool IsCodex => string.Equals(Provider, "codex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What is run: <see cref="Command"/>, unless it is empty or names the other provider's own
    /// command — which is what a settings file written before there was a choice says, and the
    /// provider is the line that was changed.
    /// </summary>
    [YamlIgnore]
    public string CommandToRun =>
        Command.Trim() is { Length: > 0 } command
        && !string.Equals(command, IsCodex ? "claude" : "codex", StringComparison.OrdinalIgnoreCase)
            ? command
            : IsCodex ? "codex" : "claude";

    /// <summary>
    /// How the agent's permission questions are answered: <c>auto</c> sends them to intrf4ce,
    /// which allows them and shows the tool call in the chat; <c>dontAsk</c> never asks.
    /// </summary>
    public string PermissionMode { get; set; } = "auto";

    /// <summary>
    /// The tools the agent may use; empty means everything the permission mode allows.
    /// </summary>
    /// <remarks>
    /// Empty by default. A presenter that cannot read a file, search the web or run a command
    /// spends its turn explaining what it could not do, which is worse than useless on a stage
    /// someone is watching. Name tools here to narrow it — <c>[mcp__intrf4ce, WebSearch]</c>
    /// makes a presenter that can only present.
    /// </remarks>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Where the hosted agent runs; empty for intrf4ce's own agent folder.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Give the hosted agent only intrf4ce's tools, not every MCP server you have configured.</summary>
    public bool StrictMcp { get; set; } = true;

    /// <summary>
    /// Let the agent use an API key from the environment — <c>ANTHROPIC_API_KEY</c> for Claude
    /// Code, <c>OPENAI_API_KEY</c> for Codex — which bills the presentation to API credit rather
    /// than to the subscription the agent is signed in with.
    /// </summary>
    /// <remarks>
    /// Off by default, and the variable is hidden from the agent, because inheriting a key
    /// left in the environment for something else is how a presentation quietly starts
    /// costing money — or fails, when that account has no credit and the subscription did.
    /// </remarks>
    public bool UseApiKey { get; set; }

    /// <summary>Extra arguments appended to the command line.</summary>
    public List<string> ExtraArgs { get; set; } = [];
}

public sealed class StageSettings
{
    /// <summary>Multiplies every animation's duration; 0 turns animation off.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    /// <summary>Jump to a new slide as soon as the agent adds it, even mid-narration.</summary>
    public bool FollowAgent { get; set; }

    /// <summary>
    /// The title, the progress dots and the buttons along the bottom, drawn over the stage.
    /// Off leaves only the slides — and the subtitles, which are content, not controls.
    /// </summary>
    public bool ShowControls { get; set; } = true;
}

public sealed class LibrarySettings
{
    /// <summary>Where sessions are kept; empty for <c>sessions</c> beside the executable.</summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// Copy images from elsewhere on this machine into the session, rather than pointing at
    /// where they live.
    /// </summary>
    /// <remarks>
    /// Off, because a folder of holiday photographs is the user's, in one place, and a
    /// presentation about it has no business duplicating gigabytes. Turn it on when a session
    /// has to travel: it then holds every picture it shows. Images from the web are downloaded
    /// either way — there is nowhere else for them to live.
    /// </remarks>
    public bool CopyMedia { get; set; }
}

/// <param name="Error">Why the file could not be used at all (defaults are in force).</param>
/// <param name="Warnings">Settings that were not understood and so had no effect, by line.</param>
public sealed record SettingsLoad(AppSettings Settings, string? Error, IReadOnlyList<string>? Warnings = null);

/// <summary>Which plugins load, and what each falls back on when the agent does not say.</summary>
public sealed class PluginsSettings
{
    /// <summary>Ids of plugins not to load, built in or installed. Takes effect on the next start.</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>Plugin files to load from where they are, without installing them: for working on one.</summary>
    public List<string> Load { get; set; } = [];

    /// <summary>
    /// Each plugin's own settings, under its id: <c>weather: { place: Zagreb }</c>. What they
    /// mean is the plugin's business; its prompt tells the agent.
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// One plugin's settings as a JSON object, which is how a plugin reads them; empty when it
    /// has none.
    /// </summary>
    /// <remarks>
    /// YAML says nothing about whether <c>3</c> is a number or <c>true</c> a flag, so a scalar
    /// that reads as one is given as one.
    /// </remarks>
    public JsonElement OptionsFor(string pluginId)
    {
        var options = Options.GetValueOrDefault(pluginId);
        var json = options is null ? new JsonObject() : Convert(options);
        return JsonSerializer.SerializeToElement(json);

        static JsonNode? Convert(object? value) => value switch
        {
            null => null,
            IDictionary<string, object> map => new JsonObject(map.Select(p => KeyValuePair.Create(p.Key, Convert(p.Value)))),
            IDictionary<object, object> map => new JsonObject(map.Select(p => KeyValuePair.Create(p.Key.ToString() ?? "", Convert(p.Value)))),
            IEnumerable<object> list => new JsonArray([.. list.Select(Convert)]),
            string text when bool.TryParse(text, out var flag) => JsonValue.Create(flag),
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(value.ToString()),
        };
    }
}

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
        live.Narration.Muted = loaded.Narration.Muted;

        if (live.ElevenLabs.KeyFile != loaded.ElevenLabs.KeyFile)
        {
            later.Add("elevenlabs.key_file");
        }

        if (live.ElevenLabs.ApiKey != loaded.ElevenLabs.ApiKey)
        {
            later.Add("elevenlabs.api_key");
        }

        if (live.ElevenLabs.MaxConcurrent != loaded.ElevenLabs.MaxConcurrent)
        {
            later.Add("elevenlabs.max_concurrent");
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
        live.Stage.ShowControls = loaded.Stage.ShowControls;

        // The agent is a process started at launch; the library is the folder decks came from.
        if (live.Agent.Mode != loaded.Agent.Mode || live.Agent.Provider != loaded.Agent.Provider || live.Agent.Command != loaded.Agent.Command
            || live.Agent.Model != loaded.Agent.Model || live.Agent.PermissionMode != loaded.Agent.PermissionMode
            || live.Agent.WorkingDirectory != loaded.Agent.WorkingDirectory || live.Agent.StrictMcp != loaded.Agent.StrictMcp
            || live.Agent.UseApiKey != loaded.Agent.UseApiKey
            || !live.Agent.AllowedTools.SequenceEqual(loaded.Agent.AllowedTools) || !live.Agent.ExtraArgs.SequenceEqual(loaded.Agent.ExtraArgs))
        {
            later.Add("agent");
        }

        if (live.Library.Folder != loaded.Library.Folder)
        {
            later.Add("library.folder");
        }

        live.Library.CopyMedia = loaded.Library.CopyMedia;
        live.Keys = new Dictionary<string, string>(loaded.Keys, StringComparer.Ordinal);

        // A plugin's code is loaded once and stays; its settings it reads afresh each time.
        if (!live.Plugins.Disabled.SequenceEqual(loaded.Plugins.Disabled) || !live.Plugins.Load.SequenceEqual(loaded.Plugins.Load))
        {
            later.Add("plugins");
        }

        live.Plugins.Options = loaded.Plugins.Options;

        return later;
    }

    /// <summary>
    /// Writes one setting into settings.yaml, leaving the rest of the file — comments, order,
    /// spacing — exactly as the user left it.
    /// </summary>
    /// <remarks>
    /// The file is edited as text rather than reserialised from the model, because
    /// reserialising would throw away every comment in it, and those comments are how anyone
    /// opening the file learns what the settings mean.
    /// </remarks>
    /// <returns>An error in words, or null when it was written.</returns>
    public static string? Write(string path, string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var entry = SettingsSchema.Find(key);
        if (entry is null)
        {
            return $"There is no setting '{key}'. There is: {string.Join(", ", SettingsSchema.All.Select(s => s.Key))}.";
        }

        var (written, error) = SettingsSchema.Parse(entry, value ?? "");
        if (error is not null)
        {
            return error;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : DefaultText;
            File.WriteAllText(path, Set(text, entry.Key, written!));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"settings.yaml could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// The same edit on the text itself: the line keeps its indentation and its comment, and a
    /// setting the file never mentioned is added under its section.
    /// </summary>
    public static string Set(string text, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            throw new ArgumentException("a setting key reads section.name", nameof(key));
        }

        var section = key[..dot];
        var name = key[(dot + 1)..];
        var written = Quote(value);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var start = lines.FindIndex(l => l.StartsWith(section + ":", StringComparison.Ordinal));
        if (start < 0)
        {
            // A file that never mentioned this section: give it one at the end, above whatever
            // blank line the file ends with, so the file still ends the way it did.
            var at = lines.Count > 0 && lines[^1].Trim().Length == 0 ? lines.Count - 1 : lines.Count;
            if (at > 0 && lines[at - 1].Trim().Length > 0)
            {
                lines.Insert(at++, "");
            }

            lines.Insert(at++, section + ":");
            lines.Insert(at, $"  {name}: {written}");
            return string.Join("\n", lines);
        }

        // The section runs until the next line that starts in column one.
        var end = start + 1;
        while (end < lines.Count && (lines[end].Trim().Length == 0 || char.IsWhiteSpace(lines[end][0])))
        {
            end++;
        }

        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(name + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = line[..(line.Length - trimmed.Length)];
            var changed = $"{indent}{name}: {written}";
            var comment = CommentStart(line);

            // The explanation stays in the column it was in, so a file of them does not go ragged.
            lines[i] = comment < 0
                ? changed
                : changed.PadRight(Math.Max(comment, changed.Length + 2)) + line[comment..].TrimEnd();

            return string.Join("\n", lines);
        }

        // The section exists but has never said anything about this setting.
        var last = end;
        while (last > start + 1 && lines[last - 1].Trim().Length == 0)
        {
            last--;
        }

        lines.Insert(last, $"  {name}: {written}");
        return string.Join("\n", lines);
    }

    /// <summary>Where the explanation after a value starts, or -1 when there is none.</summary>
    private static int CommentStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }

            // A hash inside the value itself is not a comment; one after a space is.
            else if (line[i] == '#' && !quoted && i > 0 && char.IsWhiteSpace(line[i - 1]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Puts a command on keys in the <c>keys:</c> section, or with an empty command takes the
    /// keys' default away; the rest of the file is left as it was.
    /// </summary>
    /// <returns>An error in words, or null when it was written.</returns>
    public static string? WriteBinding(string path, string keys, string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(keys);
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : DefaultText;
            File.WriteAllText(path, Bind(text, keys, command));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"settings.yaml could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// Writes the plugins not to load, as one line: <c>disabled: [weather, holidays]</c>. Not a
    /// setting the panel or the agent can change, so it is not in the schema; <c>:plugin</c>
    /// writes it, through the same line edit that leaves the rest of the file alone.
    /// </summary>
    /// <returns>An error in words, or null when it was written.</returns>
    public static string? WriteDisabledPlugins(string path, IEnumerable<string> ids)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(ids);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : DefaultText;
            File.WriteAllText(path, Set(text, "plugins.disabled", "[" + string.Join(", ", ids) + "]"));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"settings.yaml could not be written: {ex.Message}";
        }
    }

    /// <summary>The same edit on the text: the binding's line replaced, or added to the section.</summary>
    public static string Bind(string text, string keys, string command)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Always quoted: ":", "`" and "<" all mean something to YAML at the start of a key.
        var key = "\"" + keys.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var entry = $"  {key}: {Quote(command)}";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var start = lines.FindIndex(l => l.StartsWith("keys:", StringComparison.Ordinal));
        if (start < 0)
        {
            var at = lines.Count > 0 && lines[^1].Trim().Length == 0 ? lines.Count - 1 : lines.Count;
            if (at > 0 && lines[at - 1].Trim().Length > 0)
            {
                lines.Insert(at++, "");
            }

            lines.Insert(at++, "keys:");
            lines.Insert(at, entry);
            return string.Join("\n", lines);
        }

        var end = start + 1;
        while (end < lines.Count && (lines[end].Trim().Length == 0 || char.IsWhiteSpace(lines[end][0])))
        {
            end++;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + ":", StringComparison.Ordinal))
            {
                lines[i] = entry;
                return string.Join("\n", lines);
            }
        }

        var last = end;
        while (last > start + 1 && lines[last - 1].Trim().Length == 0)
        {
            last--;
        }

        lines.Insert(last, entry);
        return string.Join("\n", lines);
    }

    /// <summary>Quotes a value only where YAML would otherwise read it as something else.</summary>
    private static string Quote(string value) =>
        value.Length == 0 || value.StartsWith(' ') || value.EndsWith(' ')
            || value.Contains('#', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;

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

        // The file first: it is where the user said the key lives.
        if (settings.ApiKey.Trim() is { Length: > 0 } fromSettings)
        {
            return fromSettings;
        }

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
          muted: false            # true: subtitles only, and nothing is sent to a voice

        elevenlabs:
          api_key: ""             # your ElevenLabs key; never shown in the app or to the agent
          voice_id: auq43ws1oslv0tO4BDa7
          model_id: eleven_v3     # v3 is the most expressive, and reads [whispers]-style tags as
                                  # directions; eleven_multilingual_v2 and eleven_flash_v2_5 too
          key_file: ""            # or: a file holding the key (bare, or KEY=value), secrets.yaml,
                                  # or the ELEVENLABS_API_KEY environment variable
          monthly_cap: 30000      # characters intrf4ce may spend per month
          keep_in_reserve: 2000   # stop when the account has fewer than this left
          max_concurrent: 2       # clips asked for at once; plans allow only a few (3 on Starter)
          stability: 0.5          # lower is more expressive and varied, higher more even
          similarity_boost: 0.75
          style: 0
          speed: 1.0

        system_voice:
          voice: ""               # e.g. Microsoft Catherine; empty for the default voice
          rate: 0                 # -10 to 10

        agent:
          mode: hosted            # hosted: intrf4ce runs the agent for you. attached: you connect your own.
          provider: claude        # claude: Claude Code, on your Claude account. codex: OpenAI's Codex,
                                  # on your ChatGPT account. Either signs in from the conversation.
          use_api_key: false      # true lets ANTHROPIC_API_KEY (claude) or OPENAI_API_KEY (codex) from
                                  # your environment bill the agent to API credit instead of its account
          command: ""             # empty for the provider's own: claude, or codex
          model: ""               # empty for the provider's default
          permission_mode: auto    # claude: auto lets intrf4ce answer the agent's permission questions
                                  # and show what it ran. dontAsk never asks. Also acceptEdits,
                                  # bypassPermissions, plan, manual. codex: plan only reads,
                                  # bypassPermissions has no sandbox, anything else writes in its folder.
          allowed_tools: []       # claude: empty for every tool the permission mode allows. Narrow it with
                                  # e.g. [mcp__intrf4ce, WebSearch] for a presenter that only presents
          working_directory: ""   # empty for intrf4ce's own agent folder
          strict_mcp: true        # only intrf4ce's tools, not every MCP server or plugin you have set up
          extra_args: []          # claude: appended to its command line

        stage:
          animation_speed: 1.0    # 0 turns animation off
          follow_agent: false     # jump to each new slide as the agent adds it
          show_controls: true     # false: only the slides; no title, progress or buttons over them (z)

        keys:                     # your own key bindings over the defaults: ? or :help lists them.
                                  # Keys as in vim: gg, G, <Ctrl+d>, <Space>. "" takes a key away.
          # "gt": next
          # "<Ctrl+d>": next 5

        library:
          folder: ""              # where sessions live; empty for "sessions" beside intrf4ce
          copy_media: false       # true copies images from this machine into the session, so it
                                  # can be moved elsewhere; off keeps a reference to where they are

        plugins:                  # :plugins lists them; :plugin install file.i4p adds one
          disabled: []            # ids not to load, built in or installed, e.g. [holidays]; needs a restart
          load: []                # .i4p files to load from where they are, while you work on one
          options:                # each plugin's own settings: what it falls back on when the agent does not say
            # timezones: { zones: [Australia/Melbourne, Europe/Zagreb] }
            # weather: { place: Melbourne }
            # holidays: { country: AU, region: VIC }
        """;
}
