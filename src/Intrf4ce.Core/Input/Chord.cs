using System.Text;

namespace Intrf4ce.Core.Input;

[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>
/// One key as a binding names it: a character, or a named key, with modifiers.
/// </summary>
/// <remarks>
/// The notation is Fjord's, which is vim's and qutebrowser's: a printable character is
/// written as itself — <c>g</c>, <c>G</c>, <c>:</c> — with Shift folded into it, and
/// anything else in angle brackets — <c>&lt;Space&gt;</c>, <c>&lt;Ctrl+d&gt;</c>,
/// <c>&lt;Shift+Tab&gt;</c>. A sequence is chords one after another: <c>gg</c> is two.
/// Written fresh for intrf4ce; only the notation is shared, so a binding means the same in
/// both apps.
/// </remarks>
public readonly record struct Chord(string Name, Modifiers Modifiers = Modifiers.None)
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space",
        ["esc"] = "Esc",
        ["escape"] = "Esc",
        ["enter"] = "Enter",
        ["return"] = "Enter",
        ["cr"] = "Enter",
        ["tab"] = "Tab",
        ["bs"] = "Backspace",
        ["backspace"] = "Backspace",
        ["del"] = "Del",
        ["delete"] = "Del",
        ["ins"] = "Ins",
        ["insert"] = "Ins",
        ["left"] = "Left",
        ["right"] = "Right",
        ["up"] = "Up",
        ["down"] = "Down",
        ["home"] = "Home",
        ["end"] = "End",
        ["pgup"] = "PgUp",
        ["pageup"] = "PgUp",
        ["pgdn"] = "PgDn",
        ["pagedown"] = "PgDn",
        ["lt"] = "<",
        ["gt"] = ">",
        ["bar"] = "|",
    };

    /// <summary>A named key, or a single character.</summary>
    public bool IsCharacter => Name.Length == 1;

    public override string ToString()
    {
        if (Modifiers == Modifiers.None)
        {
            return Name switch
            {
                "<" => "<lt>",
                ">" => "<gt>",
                _ when IsCharacter => Name,
                _ => $"<{Name}>",
            };
        }

        var text = new StringBuilder("<");
        foreach (var (flag, word) in new[] { (Modifiers.Ctrl, "Ctrl"), (Modifiers.Alt, "Alt"), (Modifiers.Shift, "Shift"), (Modifiers.Meta, "Meta") })
        {
            if (Modifiers.HasFlag(flag))
            {
                text.Append(word).Append('+');
            }
        }

        return text.Append(Name).Append('>').ToString();
    }

    /// <summary>
    /// The chords of a written sequence: <c>gg</c>, <c>&lt;Ctrl+d&gt;</c>, <c>G</c>.
    /// </summary>
    /// <exception cref="FormatException">A bracketed name that is not a key.</exception>
    public static IReadOnlyList<Chord> Parse(string keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(keys);

        var chords = new List<Chord>();
        var i = 0;
        while (i < keys.Length)
        {
            var close = keys[i] == '<' ? keys.IndexOf('>', i + 1) : -1;
            if (close <= i + 1)
            {
                chords.Add(Character(keys[i]));
                i++;
                continue;
            }

            chords.Add(Bracketed(keys[(i + 1)..close], keys));
            i = close + 1;
        }

        return chords;
    }

    /// <summary>A sequence written back the way a binding would be.</summary>
    public static string Format(IEnumerable<Chord> chords) => string.Concat(chords.Select(c => c.ToString()));

    /// <summary>A typed character, with Shift already in it.</summary>
    public static Chord Character(char c) => new(c.ToString(), Modifiers.None);

    private static Chord Bracketed(string inside, string whole)
    {
        var parts = inside.Split('+');
        var modifiers = Modifiers.None;

        // "<Ctrl++>" names the plus key: the last part is empty.
        var name = parts[^1].Length == 0 && parts.Length > 1 ? "+" : parts[^1];
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "c" => Modifiers.Ctrl,
                "alt" or "a" or "m" => Modifiers.Alt,
                "shift" or "s" => Modifiers.Shift,
                "meta" or "win" or "super" or "cmd" => Modifiers.Meta,
                "" => Modifiers.None,
                _ => throw new FormatException($"'{part}' in '{whole}' is not a modifier; use Ctrl, Alt, Shift or Meta"),
            };
        }

        if (Names.TryGetValue(name, out var named))
        {
            name = named;
        }
        else if (name.Length > 1 && !(name[0] is 'F' or 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24))
        {
            throw new FormatException($"'<{inside}>' in '{whole}' is not a key");
        }
        else if (name.Length > 1)
        {
            name = "F" + name[1..];
        }

        // Shift is part of a character: <Shift+g> is G.
        if (name.Length == 1 && modifiers.HasFlag(Modifiers.Shift) && char.IsLetter(name[0]))
        {
            name = name.ToUpperInvariant();
            modifiers &= ~Modifiers.Shift;
        }

        return new Chord(name, modifiers);
    }
}
