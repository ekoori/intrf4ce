namespace Intrf4ce.Core.Input;

/// <summary>What typing a sequence of keys has come to.</summary>
public enum KeyOutcome
{
    /// <summary>No binding starts like this; the keys are dropped.</summary>
    None,

    /// <summary>Some binding starts like this; keep reading.</summary>
    Pending,

    /// <summary>A binding matched; run its command.</summary>
    Run,
}

/// <summary>
/// Which keys run which commands: the shipped defaults, with the user's own layered on top.
/// </summary>
/// <remarks>
/// Two layers so that settings.yaml holds only what the user changed, and binding a key to
/// nothing hides a default without deleting it. A command is the text of a <c>:</c> command —
/// <c>next</c>, <c>go 1</c>, <c>set subtitles!</c> — so anything that can be typed at the
/// prompt can be put on a key, and the prompt is how every binding is tried out.
/// </remarks>
public sealed class KeyMap
{
    private readonly Dictionary<string, string> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _user = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _effective;

    /// <summary>Installs a shipped binding.</summary>
    public void AddDefault(string keys, string command)
    {
        _defaults[Canonical(keys)] = command;
        _effective = null;
    }

    /// <summary>Binds keys for the user; an empty command unbinds, hiding any default.</summary>
    public void Bind(string keys, string command)
    {
        _user[Canonical(keys)] = command?.Trim() ?? "";
        _effective = null;
    }

    /// <summary>Forgets every user binding, as re-reading settings.yaml does before applying it again.</summary>
    public void ClearUser()
    {
        _user.Clear();
        _effective = null;
    }

    /// <summary>Every binding in force, keys as written canonically.</summary>
    public IReadOnlyDictionary<string, string> Bindings => Effective();

    /// <summary>What a typed sequence comes to.</summary>
    public (KeyOutcome Outcome, string? Command) Match(IReadOnlyList<Chord> typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        if (typed.Count == 0)
        {
            return (KeyOutcome.None, null);
        }

        var written = Chord.Format(typed);
        var effective = Effective();
        if (effective.TryGetValue(written, out var command))
        {
            return (KeyOutcome.Run, command);
        }

        return effective.Keys.Any(k => k.Length > written.Length && k.StartsWith(written, StringComparison.Ordinal) && Continues(k, typed))
            ? (KeyOutcome.Pending, null)
            : (KeyOutcome.None, null);
    }

    /// <summary>The bindings that could still finish what has been typed — the options after a <c>g</c>.</summary>
    public IReadOnlyList<(string Keys, string Command)> Continuations(IReadOnlyList<Chord> typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var written = Chord.Format(typed);
        return
        [
            .. Effective()
                .Where(b => b.Key.StartsWith(written, StringComparison.Ordinal) && b.Key != written && Continues(b.Key, typed))
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => (b.Key, b.Value)),
        ];
    }

    /// <summary>The keys that run a command, for hints and help; empty when none do.</summary>
    public IReadOnlyList<string> KeysFor(string command) =>
        [.. Effective().Where(b => string.Equals(b.Value, command, StringComparison.Ordinal)).Select(b => b.Key).OrderBy(k => k.Length)];

    /// <summary>A sequence as its chords, written back the one way it is stored.</summary>
    public static string Canonical(string keys) => Chord.Format(Chord.Parse(keys));

    /// <summary>
    /// Whether a binding's chords really begin with the typed ones, not merely its text:
    /// <c>&lt;</c> typed must not look like the start of <c>&lt;Space&gt;</c>.
    /// </summary>
    private static bool Continues(string keys, IReadOnlyList<Chord> typed)
    {
        var chords = Chord.Parse(keys);
        if (chords.Count <= typed.Count)
        {
            return false;
        }

        for (var i = 0; i < typed.Count; i++)
        {
            if (chords[i] != typed[i])
            {
                return false;
            }
        }

        return true;
    }

    private Dictionary<string, string> Effective()
    {
        if (_effective is not null)
        {
            return _effective;
        }

        var merged = new Dictionary<string, string>(_defaults, StringComparer.Ordinal);
        foreach (var (keys, command) in _user)
        {
            if (command.Length == 0)
            {
                merged.Remove(keys);
            }
            else
            {
                merged[keys] = command;
            }
        }

        _effective = merged;
        return merged;
    }
}

/// <summary>
/// Turns keys as they are pressed into commands: sequences such as <c>gg</c>, and a count in
/// front — <c>3l</c> is three slides on.
/// </summary>
/// <remarks>
/// A half-typed sequence waits for its next key, but not for ever: after a pause it is
/// dropped, the way vim's <c>timeoutlen</c> does, so a stray <c>g</c> does not lie in wait to
/// turn the next <c>g</c> into "go to the first slide". A <c>0</c> with no count before it is
/// a key of its own, as in vim, so it can still be bound.
/// </remarks>
public sealed class KeyReader(KeyMap map, Func<DateTimeOffset>? clock = null)
{
    private readonly List<Chord> _typed = [];
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private int? _count;
    private DateTimeOffset _last;

    /// <summary>How long a half-typed sequence waits for its next key.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>What has been typed towards a command so far, for the title bar; empty when nothing.</summary>
    public string Pending => (_count is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + Chord.Format(_typed);

    /// <summary>Forgets anything half-typed.</summary>
    public void Reset()
    {
        _typed.Clear();
        _count = null;
    }

    /// <summary>
    /// One key.
    /// </summary>
    /// <returns>
    /// The command to run and how many times, when a binding completed; otherwise whether the
    /// key was taken (part of a sequence or a count) or can be left to anything else.
    /// </returns>
    public (bool Taken, string? Command, int? Count) Feed(Chord chord)
    {
        var now = _clock();
        if ((_typed.Count > 0 || _count is not null) && now - _last > Timeout)
        {
            Reset();
        }

        _last = now;

        // A count, before any key of the sequence: 1-9 start one, 0 only continues one.
        if (_typed.Count == 0 && chord is { IsCharacter: true, Modifiers: Modifiers.None } && char.IsAsciiDigit(chord.Name[0])
            && (chord.Name[0] != '0' || _count is not null))
        {
            _count = Math.Min(9999, ((_count ?? 0) * 10) + (chord.Name[0] - '0'));
            return (true, null, null);
        }

        _typed.Add(chord);
        var (outcome, command) = map.Match(_typed);

        switch (outcome)
        {
            case KeyOutcome.Run:
                var count = _count;
                Reset();
                return (true, command, count);

            case KeyOutcome.Pending:
                return (true, null, null);

            default:
                // Nothing starts like this. A single unbound key is left to whoever else wants
                // it; the tail of a failed sequence is simply dropped.
                var alone = _typed.Count == 1 && _count is null;
                Reset();
                return (!alone, null, null);
        }
    }
}
