namespace Intrf4ce.Core;

/// <summary>Where intrf4ce keeps things, per platform convention.</summary>
/// <param name="Config">settings.yaml and secrets.yaml.</param>
/// <param name="Cache">Voice clips, render cache, logs: all rebuildable.</param>
/// <param name="Library">Sessions: a folder per presentation, with the media it uses.</param>
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

    /// <summary>The folder a deck made before sessions lived beside the executable would be in.</summary>
    public static string LegacyLibrary
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
            {
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            }

            return Path.Combine(documents, "intrf4ce");
        }
    }

    /// <summary>
    /// <c>sessions</c> beside the executable, so a presentation and the media it uses travel
    /// together and a copied folder still works; the user's documents when that folder cannot be
    /// written, which is what happens when the app is installed somewhere read-only.
    /// </summary>
    private static string SessionsFolder(string documents)
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } beside)
        {
            var sessions = Path.Combine(beside, "sessions");
            try
            {
                Directory.CreateDirectory(sessions);
                var probe = Path.Combine(sessions, ".writable");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return sessions;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Program Files, a read-only mount, a network share without write: fall back.
            }
        }

        return Path.Combine(documents, "intrf4ce");
    }

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

        var library = SessionsFolder(documents);

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
