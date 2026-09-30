using System.Text;
using System.Text.RegularExpressions;

namespace Intrf4ce.Core.Narration;

/// <summary>
/// Stage directions for the voice, written inline: <c>[curious]</c>, <c>[whispers]</c>,
/// <c>[laughs]</c>.
/// </summary>
/// <remarks>
/// ElevenLabs v3 reads a bracketed word as how to say what follows rather than as something
/// to say, and reports it in its timing like any other characters. Nothing else does: a
/// system voice would read "curious" aloud, and a subtitle showing "[whispers]" is the stage
/// direction leaking onto the stage. So the tags stay in the narration — that is where their
/// position means something — and are taken out of what other voices are given and of
/// everything the user reads.
/// <para>
/// Single brackets only. Cues are double (<c>[[focus x]]</c>) and are removed long before
/// anything here sees the text.
/// </para>
/// </remarks>
public static partial class AudioTags
{
    /// <summary>A letter first, a few words at most, no nested brackets, and never half of a <c>[[cue]]</c>.</summary>
    [GeneratedRegex(@"(?<!\[)\[[A-Za-z][^\[\]\r\n]{0,48}\](?!\])", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    public static bool Any(string text) => !string.IsNullOrEmpty(text) && Tag().IsMatch(text);

    /// <summary>Every tag in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<TextSpan> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. Tag().Matches(text).Select(m => new TextSpan(m.Index, m.Index + m.Length))];
    }

    /// <summary>For each character, whether it belongs to a tag.</summary>
    public static bool[] Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var mask = new bool[text.Length];
        foreach (var tag in Find(text))
        {
            for (var i = tag.Start; i < tag.End; i++)
            {
                mask[i] = true;
            }
        }

        return mask;
    }

    /// <summary>
    /// The text a voice that does not understand tags should be given, and for every
    /// character of the original, where it went in that text.
    /// </summary>
    /// <remarks>
    /// A tag's characters map to wherever the next spoken character went, so they take no
    /// time: a cue written just after <c>[excited]</c> fires with the word after it.
    /// </remarks>
    /// <returns>
    /// The spoken text; for each original character, its place in the spoken text; and for
    /// each spoken character, the original it came from.
    /// </returns>
    public static (string Spoken, int[] Map, int[] Origin) Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var hidden = Hidden(text);
        var spoken = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        var map = new int[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            map[i] = spoken.Length;
            if (!hidden[i])
            {
                spoken.Append(text[i]);
                origin.Add(i);
            }
        }

        return (spoken.ToString(), map, [.. origin]);
    }

    /// <summary>
    /// What disappears when the tags go: each tag, and the one space that would otherwise be
    /// left doubled or dangling beside it.
    /// </summary>
    /// <remarks>
    /// Decided over the whole text at once, so that a caption rendered in pieces — the words
    /// spoken, the word being spoken, the words to come — joins up exactly as the whole would.
    /// </remarks>
    private static bool[] Hidden(string text)
    {
        var hidden = Mask(text);
        foreach (var tag in Find(text))
        {
            var before = tag.Start - 1;
            var after = tag.End;
            var spaceBefore = before >= 0 && text[before] == ' ';
            var spaceAfter = after < text.Length && text[after] == ' ';

            if (spaceAfter && (before < 0 || spaceBefore || hidden[before]))
            {
                hidden[after] = true;
            }
            else if (spaceBefore && after >= text.Length)
            {
                hidden[before] = true;
            }
        }

        return hidden;
    }

    /// <summary>What the user should read of <paramref name="text"/> between two offsets.</summary>
    public static string Visible(string text, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(text);

        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (!Any(text))
        {
            return text[start..end];
        }

        var hidden = Hidden(text);
        var visible = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            if (!hidden[i])
            {
                visible.Append(text[i]);
            }
        }

        return visible.ToString();
    }
}
