namespace Intrf4ce.Core;

/// <summary>Where intrf4ce keeps things, per platform convention.</summary>
/// <param name="Config">settings.yaml and secrets.yaml.</param>
/// <param name="Cache">Voice clips, render cache, logs: all rebuildable.</param>
/// <param name="Library">Decks. Under Documents, because they are the user's work.</param>
public sealed record AppPaths(string Config, string Cache, string Library)
{
    /// <summary>Set to a folder to keep everything under it: tests, and a second instance beside a real one.</summary>
    public const string HomeVariable = "INTRF4CE_HOME";

    public string SettingsFile => Path.Combine(Config, "settings.yaml");

    public string SecretsFile => Path.Combine(Config, "secrets.yaml");

    public string Logs => Path.Combine(Cache, "logs");

    /// <summary>Synthesised narration, shared across decks so the same sentence is never paid for twice.</summary>
    public string VoiceCache => Path.Combine(Cache, "voice");

    /// <summary>Where the hosted agent runs; its own folder so it never picks up a project's CLAUDE.md.</summary>
    public string AgentHome => Path.Combine(Cache, "agent");

    public static AppPaths Default { get; } = Resolve();

    public static AppPaths Under(string root) =>
        new(Path.Combine(root, "config"), Path.Combine(root, "cache"), Path.Combine(root, "library"));

    private static AppPaths Resolve()
    {
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home)
        {
            return Under(home);
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }

        var library = Path.Combine(documents, "intrf4ce");

        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "intrf4ce"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "intrf4ce"),
                library);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return new AppPaths(
                Path.Combine(profile, "Library", "Application Support", "intrf4ce"),
                Path.Combine(profile, "Library", "Caches", "intrf4ce"),
                library);
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xc ? xc : Path.Combine(profile, ".config");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xk ? xk : Path.Combine(profile, ".cache");
        return new AppPaths(Path.Combine(config, "intrf4ce"), Path.Combine(cache, "intrf4ce"), library);
    }
}
