namespace Intrf4ce.Media.Tts;

/// <summary>
/// Where spoken clips are kept: with the presentation that speaks them, and in a shared cache
/// behind it.
/// </summary>
/// <remarks>
/// A session folder holds its own audio, so a presentation that has been narrated once is
/// narrated for ever — reopened, or copied to another machine, it plays without a network, a
/// key, or another character billed. The machine-wide cache sits behind it so the same sentence
/// in a second presentation is not paid for twice either; a clip found there is copied into the
/// session, which is what makes the folder self-contained.
/// <para>
/// Clips are named by content, so a file that exists is the right file and never needs
/// invalidating: different text, voice or settings is a different name.
/// </para>
/// </remarks>
public sealed class VoiceCache(string shared)
{
    /// <summary>Where this presentation keeps its audio; null when no deck is open.</summary>
    public Func<string?>? Session { get; set; }

    /// <summary>The machine-wide cache, which also holds the usage ledger.</summary>
    public string Shared { get; } = shared;

    /// <summary>
    /// The folder holding all of <paramref name="names"/>, bringing them into the session when
    /// they were only in the shared cache; null when they have never been made.
    /// </summary>
    public string? Existing(string engine, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var session = Folder(engine, SessionRoot());
        if (session is not null && names.All(n => File.Exists(Path.Combine(session, n))))
        {
            return session;
        }

        var shared = Folder(engine, Shared)!;
        if (!names.All(n => File.Exists(Path.Combine(shared, n))))
        {
            return null;
        }

        if (session is null)
        {
            return shared;
        }

        try
        {
            Directory.CreateDirectory(session);
            foreach (var name in names)
            {
                File.Copy(Path.Combine(shared, name), Path.Combine(session, name), overwrite: true);
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session could not take a copy; playing from the shared cache still works.
            return shared;
        }
    }

    /// <summary>Where a new clip goes: the session when there is one, the shared cache otherwise.</summary>
    public string Target(string engine)
    {
        var folder = Folder(engine, SessionRoot()) ?? Folder(engine, Shared)!;
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Keeps a copy of a new clip in the shared cache, so the next presentation that says the
    /// same thing does not pay for it again.
    /// </summary>
    public void Share(string engine, string from, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var shared = Folder(engine, Shared)!;
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(shared);
            foreach (var name in names)
            {
                File.Copy(Path.Combine(from, name), Path.Combine(shared, name), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to keep a spare copy is not worth failing a presentation over.
        }
    }

    private string? SessionRoot()
    {
        try
        {
            return Session?.Invoke();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Folder(string engine, string? root) => root is null ? null : Path.Combine(root, engine);
}
