using System.Globalization;

namespace Intrf4ce.Core.Settings;

public enum SettingKind
{
    /// <summary>true or false.</summary>
    Toggle,

    Number,

    /// <summary>One of a fixed list of words.</summary>
    Choice,

    Text,
}

/// <summary>One setting a person, or an agent, can change without opening the file.</summary>
/// <param name="Key">Dotted, as it reads in settings.yaml: <c>narration.subtitles</c>.</param>
/// <param name="Label">What it is called in the settings panel.</param>
/// <param name="NeedsRestart">Changing it does nothing until intrf4ce is started again.</param>
public sealed record SettingEntry(
    string Key,
    string Label,
    SettingKind Kind,
    string Description,
    IReadOnlyList<string>? Choices = null,
    double Minimum = 0,
    double Maximum = 1,
    double Step = 0.1,
    bool NeedsRestart = false);

/// <summary>
/// The settings worth changing while the app is running, described once.
/// </summary>
/// <remarks>
/// settings.yaml remains the file of record — it is the user's, comments and all — but a
/// presentation is watched, not administered, and nobody should have to find a YAML file to
/// turn subtitles off. This list is what the settings panel draws and what the agent's
/// <c>set_setting</c> will accept, so the two can never disagree about what exists or what a
/// legal value is.
/// <para>
/// Deliberately not everything: keys that would strand the app (the library folder, the agent
/// command) stay in the file, where changing them is a considered act.
/// </para>
/// </remarks>
public static class SettingsSchema
{
    public static IReadOnlyList<SettingEntry> All { get; } =
    [
        new("narration.subtitles", "Subtitles", SettingKind.Toggle, "Show what is being said under the stage."),
        new("narration.autoplay", "Speak each slide", SettingKind.Toggle, "Start narrating a slide as soon as it appears."),
        new("narration.muted", "Silence the voice", SettingKind.Toggle,
            "Subtitles only, and nothing sent to a voice until you unmute — which costs nothing."),
        new("narration.advance", "When a slide ends", SettingKind.Choice, "Move on by itself, or wait for you.",
            Choices: ["auto", "manual"]),
        new("narration.volume", "Volume", SettingKind.Number, "0 is silent, 1 is full.", Minimum: 0, Maximum: 1, Step: 0.05),
        new("narration.pause_after", "Pause between slides", SettingKind.Number, "Seconds of quiet before the next slide.",
            Minimum: 0, Maximum: 10, Step: 0.2),
        new("narration.engine", "Voice", SettingKind.Choice, "Which voice reads the narration.",
            Choices: ["auto", "elevenlabs", "system", "none"], NeedsRestart: true),
        new("elevenlabs.model_id", "ElevenLabs model", SettingKind.Choice,
            "v3 is the most natural and expressive. Slides already spoken keep their voice.",
            Choices: ["eleven_v3", "eleven_multilingual_v2", "eleven_flash_v2_5", "eleven_turbo_v2_5"]),
        new("elevenlabs.voice_id", "ElevenLabs voice", SettingKind.Text,
            "A voice id from your ElevenLabs library. Slides already spoken keep their voice; :revoice to redo one."),
        new("elevenlabs.stability", "Steadiness", SettingKind.Number,
            "Lower is more expressive and varied, higher more even.", Minimum: 0, Maximum: 1, Step: 0.05),
        new("stage.animation_speed", "Camera speed", SettingKind.Number, "Multiplies every move; 0 turns animation off.",
            Minimum: 0, Maximum: 3, Step: 0.1),
        new("stage.follow_agent", "Follow the agent", SettingKind.Toggle, "Jump to each new slide the moment it is built."),
        new("stage.show_controls", "Titles and controls on the stage", SettingKind.Toggle,
            "The title, progress and buttons over the slides. Off leaves just the slides (z)."),
        new("library.copy_media", "Copy pictures into the session", SettingKind.Toggle,
            "Off points at pictures where they live; on copies them in, so the session can travel."),
        new("agent.provider", "Agent", SettingKind.Choice,
            "claude runs Claude Code on your Claude account; codex runs OpenAI's Codex on your ChatGPT account.",
            Choices: ["claude", "codex"], NeedsRestart: true),
        new("agent.permission_mode", "Agent permissions", SettingKind.Choice,
            "auto lets intrf4ce answer the agent's permission questions; dontAsk never asks at all.",
            Choices: ["auto", "dontAsk", "acceptEdits", "bypassPermissions", "plan", "manual"], NeedsRestart: true),
        new("agent.model", "Agent model", SettingKind.Text, "Empty for the agent's own default.", NeedsRestart: true),
    ];

    public static SettingEntry? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a setting says right now, as it would be written in the file.</summary>
    public static string Read(AppSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return key switch
        {
            "narration.subtitles" => Text(settings.Narration.Subtitles),
            "narration.autoplay" => Text(settings.Narration.Autoplay),
            "narration.muted" => Text(settings.Narration.Muted),
            "elevenlabs.model_id" => settings.ElevenLabs.ModelId,
            "elevenlabs.voice_id" => settings.ElevenLabs.VoiceId,
            "elevenlabs.stability" => Text(settings.ElevenLabs.Stability),
            "narration.advance" => settings.Narration.Advance,
            "narration.volume" => Text(settings.Narration.Volume),
            "narration.pause_after" => Text(settings.Narration.PauseAfter),
            "narration.engine" => settings.Narration.Engine,
            "stage.animation_speed" => Text(settings.Stage.AnimationSpeed),
            "stage.follow_agent" => Text(settings.Stage.FollowAgent),
            "stage.show_controls" => Text(settings.Stage.ShowControls),
            "library.copy_media" => Text(settings.Library.CopyMedia),
            "agent.provider" => settings.Agent.Provider,
            "agent.permission_mode" => settings.Agent.PermissionMode,
            "agent.model" => settings.Agent.Model,
            _ => "",
        };
    }

    /// <summary>
    /// Checks a value against its setting and returns it as it should be written, or says why
    /// it will not do.
    /// </summary>
    public static (string? Written, string? Error) Parse(SettingEntry entry, string value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();

        switch (entry.Kind)
        {
            case SettingKind.Toggle:
                return bool.TryParse(trimmed, out var flag)
                    ? (Text(flag), null)
                    : (null, $"{entry.Key} is true or false, not '{trimmed}'.");

            case SettingKind.Number:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return (null, $"{entry.Key} is a number, not '{trimmed}'.");
                }

                return number < entry.Minimum || number > entry.Maximum
                    ? (null, $"{entry.Key} is between {entry.Minimum:0.##} and {entry.Maximum:0.##}.")
                    : (Text(number), null);

            case SettingKind.Choice:
                var choice = entry.Choices!.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
                return choice is null
                    ? (null, $"{entry.Key} is one of {string.Join(", ", entry.Choices!)}; '{trimmed}' is not.")
                    : (choice, null);

            default:
                return (trimmed, null);
        }
    }

    private static string Text(bool value) => value ? "true" : "false";

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
