using Avalonia.Input;
using Intrf4ce.Core.Input;
using AvaloniaModifiers = Avalonia.Input.KeyModifiers;

namespace Intrf4ce.App.Input;

/// <summary>
/// A key press as a <see cref="Chord"/>, the form bindings are written in.
/// </summary>
/// <remarks>
/// Characters come from the symbol the layout produced rather than from the physical key, so
/// <c>:</c> is <c>:</c> however a keyboard makes it and <c>G</c> is Shift+g without anyone
/// having to say so. Named keys, and anything held with Ctrl or Alt, go by the key itself.
/// </remarks>
public static class AvaloniaChords
{
    private static readonly Dictionary<Key, string> Named = new()
    {
        [Key.Space] = "Space",
        [Key.Escape] = "Esc",
        [Key.Enter] = "Enter",
        [Key.Tab] = "Tab",
        [Key.Back] = "Backspace",
        [Key.Delete] = "Del",
        [Key.Insert] = "Ins",
        [Key.Left] = "Left",
        [Key.Right] = "Right",
        [Key.Up] = "Up",
        [Key.Down] = "Down",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "PgUp",
        [Key.PageDown] = "PgDn",
    };

    /// <summary>The chord a key press makes; null for a modifier pressed on its own.</summary>
    public static Chord? From(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }

        var modifiers = Modifiers.None;
        if (e.KeyModifiers.HasFlag(AvaloniaModifiers.Control))
        {
            modifiers |= Modifiers.Ctrl;
        }

        if (e.KeyModifiers.HasFlag(AvaloniaModifiers.Alt))
        {
            modifiers |= Modifiers.Alt;
        }

        if (e.KeyModifiers.HasFlag(AvaloniaModifiers.Meta))
        {
            modifiers |= Modifiers.Meta;
        }

        var shift = e.KeyModifiers.HasFlag(AvaloniaModifiers.Shift);

        if (Named.TryGetValue(e.Key, out var name))
        {
            return new Chord(name, shift ? modifiers | Modifiers.Shift : modifiers);
        }

        if (e.Key is >= Key.F1 and <= Key.F24)
        {
            return new Chord("F" + (e.Key - Key.F1 + 1), shift ? modifiers | Modifiers.Shift : modifiers);
        }

        // A character: what the layout made of the key, Shift already in it.
        if (modifiers is Modifiers.None && e.KeySymbol is { Length: 1 } symbol && !char.IsControl(symbol[0]))
        {
            return Chord.Character(symbol[0]);
        }

        // With Ctrl or Alt the symbol is unreliable (Ctrl+d arrives as a control character);
        // the key names it instead.
        var letter = e.Key switch
        {
            >= Key.A and <= Key.Z => ((char)('a' + (e.Key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (e.Key - Key.NumPad0))).ToString(),
            Key.OemPlus or Key.Add => "+",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 or Key.Divide => "/",
            Key.OemTilde => "`",
            _ => null,
        };

        if (letter is null)
        {
            return null;
        }

        // Shift with a letter is the capital, as a binding would write it.
        if (shift && char.IsLetter(letter[0]))
        {
            return new Chord(letter.ToUpperInvariant(), modifiers);
        }

        return new Chord(letter, shift ? modifiers | Modifiers.Shift : modifiers);
    }
}
