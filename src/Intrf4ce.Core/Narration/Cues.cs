using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Intrf4ce.Core.Narration;

public enum CueKind
{
    /// <summary>Zoom to an element or one of its parts and dim the rest.</summary>
    Focus,

    /// <summary>Zoom to an arbitrary region of an element, given as fractions.</summary>
    Zoom,

    /// <summary>Reveal an element that started hidden.</summary>
    Show,

    /// <summary>Hide an element again.</summary>
    Hide,

    /// <summary>Drop any focus and return every element to its resting view.</summary>
    Clear,
}

/// <summary>An action timed to a point in the narration.</summary>
/// <param name="Offset">Index into the spoken text where the cue fires: the start of the word that follows it.</param>
/// <param name="Target">The element id, or element.part, the cue acts on.</param>
/// <param name="Region">Zoom only: [x, y, w, h] as fractions of the element.</param>
public sealed record Cue(CueKind Kind, int Offset, string? Target = null, double[]? Region = null)
{
    /// <summary>The element part of <see cref="Target"/>.</summary>
    public string? ElementId => Target is null ? null : CueTarget.Split(Target).Element;

    /// <summary>The part after the dot in <see cref="Target"/>, if any.</summary>
    public string? PartId => Target is null ? null : CueTarget.Split(Target).Part;
}

/// <summary>Narration with its cues taken out: the text to speak and when to act.</summary>
public sealed record ParsedNarration(string Text, IReadOnlyList<Cue> Cues, IReadOnlyList<string> Errors)
{
    public static ParsedNarration Empty { get; } = new("", [], []);
}

public static class CueTarget
{
    /// <summary>Splits <c>knee.acl</c> into the element and the part. The part may itself contain dots.</summary>
    public static (string Element, string? Part) Split(string target)
    {
        var dot = target.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (target, null) : (target[..dot], target[(dot + 1)..]);
    }
}

/// <summary>
/// Parses <c>[[cue]]</c> markers out of narration.
/// </summary>
/// <remarks>
/// The markers are the agent's way of choreographing without being present: a tool call
/// takes seconds, so "zoom to the ACL now" can never land on the word "ACL" if sent live.
/// Written inline, the cue is bound to a character, and the character to a time once the
/// voice is synthesised.
/// <para>
/// Grammar: <c>focus T</c>, <c>zoom T x y w h</c>, <c>show T</c>, <c>hide T</c>, <c>clear</c>,
/// and a bare <c>T</c> as shorthand for focus. T is <c>element</c> or <c>element.part</c>.
/// </para>
/// </remarks>
public static partial class CueParser
{
    [GeneratedRegex(@"\[\[(.*?)\]\]", RegexOptions.Singleline)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[^\s\[\]]+)?$")]
    private static partial Regex TargetPattern();

    public static ParsedNarration Parse(string? narration)
    {
        if (string.IsNullOrWhiteSpace(narration))
        {
            return ParsedNarration.Empty;
        }

        var text = new StringBuilder(narration.Length);
        var cues = new List<Cue>();
        var errors = new List<string>();
        var position = 0;

        foreach (Match match in Marker().Matches(narration))
        {
            AppendSpoken(text, narration.AsSpan(position, match.Index - position));
            position = match.Index + match.Length;

            // A marker between two words would leave two spaces; drop the one after it.
            if (position < narration.Length && narration[position] == ' ' && EndsWithSpaceOrEmpty(text))
            {
                position++;
            }

            var cue = ParseOne(match.Groups[1].Value, text.Length, out var error);
            if (cue is not null)
            {
                cues.Add(cue);
            }
            else
            {
                errors.Add(error!);
            }
        }

        AppendSpoken(text, narration.AsSpan(position));

        var spoken = text.ToString().TrimEnd();

        // A cue after the last word fires at the end.
        var fixedCues = cues
            .Select(c => c.Offset > spoken.Length ? c with { Offset = spoken.Length } : c)
            .ToList();

        return new ParsedNarration(spoken, fixedCues, errors);
    }

    private static void AppendSpoken(StringBuilder text, ReadOnlySpan<char> span)
    {
        // No leading space at the very start: the first cue often precedes the first word.
        if (text.Length == 0)
        {
            span = span.TrimStart();
        }

        text.Append(span);
    }

    private static bool EndsWithSpaceOrEmpty(StringBuilder text) =>
        text.Length == 0 || char.IsWhiteSpace(text[^1]);

    private static Cue? ParseOne(string body, int offset, out string? error)
    {
        error = null;
        var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            error = "empty cue [[ ]]";
            return null;
        }

        var verb = words[0].ToLowerInvariant();
        switch (verb)
        {
            case "clear" when words.Length == 1:
                return new Cue(CueKind.Clear, offset);

            case "focus" or "show" or "hide" when words.Length == 2:
                if (!TargetPattern().IsMatch(words[1]))
                {
                    error = $"[[{body}]]: '{words[1]}' is not an element id or element.part";
                    return null;
                }

                var kind = verb switch
                {
                    "focus" => CueKind.Focus,
                    "show" => CueKind.Show,
                    _ => CueKind.Hide,
                };

                if (kind is CueKind.Show or CueKind.Hide && words[1].Contains('.', StringComparison.Ordinal))
                {
                    error = $"[[{body}]]: show and hide take an element id, not a part";
                    return null;
                }

                return new Cue(kind, offset, words[1]);

            case "zoom" when words.Length == 6:
                var region = new double[4];
                for (var i = 0; i < 4; i++)
                {
                    if (!double.TryParse(words[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out region[i])
                        || region[i] < 0 || region[i] > 1)
                    {
                        error = $"[[{body}]]: zoom takes an element and x y w h between 0 and 1";
                        return null;
                    }
                }

                if (region[2] <= 0 || region[3] <= 0)
                {
                    error = $"[[{body}]]: zoom width and height must be greater than 0";
                    return null;
                }

                return new Cue(CueKind.Zoom, offset, words[1], region);

            case "zoom":
                error = $"[[{body}]]: expected [[zoom element x y w h]] with fractions between 0 and 1";
                return null;
        }

        // Shorthand: [[knee.acl]] means focus.
        if (words.Length == 1 && TargetPattern().IsMatch(words[0]) && verb is not ("clear" or "focus" or "show" or "hide"))
        {
            return new Cue(CueKind.Focus, offset, words[0]);
        }

        error = $"[[{body}]]: unknown cue. Use [[focus el.part]], [[zoom el x y w h]], [[show el]], [[hide el]] or [[clear]]";
        return null;
    }
}
