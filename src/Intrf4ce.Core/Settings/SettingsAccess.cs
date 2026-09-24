namespace Intrf4ce.Core.Settings;

/// <summary>One setting as the agent is shown it: what it is, what it says, what it may say.</summary>
public sealed record SettingView(SettingEntry Entry, string Value);

/// <summary>
/// The user's settings, as far as an agent is allowed to see and change them.
/// </summary>
/// <remarks>
/// An interface so the tools can be tested without a window, and so that what an agent may
/// touch is decided in one place rather than by whatever it can reach.
/// </remarks>
public interface ISettingsAccess
{
    /// <summary>Every setting that can be changed, with its current value.</summary>
    IReadOnlyList<SettingView> Read();

    /// <summary>Changes one; returns why it could not be, or null.</summary>
    string? Write(string key, string value);

    /// <summary>The file it all lives in, so the agent can tell the user where to look.</summary>
    string File { get; }
}

/// <summary>The real thing: reads the live settings, writes the file the watcher is watching.</summary>
/// <remarks>
/// Writing to the file rather than to the objects in memory is the whole point: there is one
/// way settings change, whoever changes them, and what the app is doing always matches what
/// the user's file says.
/// </remarks>
public sealed class SettingsAccess(AppSettings settings, string file) : ISettingsAccess
{
    public string File { get; } = file;

    public IReadOnlyList<SettingView> Read() =>
        [.. SettingsSchema.All.Select(entry => new SettingView(entry, SettingsSchema.Read(settings, entry.Key)))];

    public string? Write(string key, string value) => SettingsFile.Write(File, key, value);
}
