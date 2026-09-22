namespace Intrf4ce.Core.Narration;

/// <summary>A span of the spoken text shown as one subtitle.</summary>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;

    public bool Contains(int offset) => offset >= Start && offset < End;
}

/// <summary>Cuts spoken text into subtitles and words.</summary>
public static class Captions
{
    /// <summary>
    /// Sentences, with long ones split at a clause or, failing that, a space near the middle.
    /// </summary>
    /// <remarks>
    /// Two short lines at most is what a viewer can read while also looking at a diagram;
    /// a subtitle that fills the screen competes with the thing it describes.
    /// </remarks>
    public static IReadOnlyList<TextSpan> Split(string text, int maxLength = 96)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new List<TextSpan>();
        var start = SkipSpace(text, 0);

        while (start < text.Length)
        {
            var end = SentenceEnd(text, start);
            AddSplit(text, start, end, maxLength, result);
            start = SkipSpace(text, end);
        }

        return result;
    }

    /// <summary>Runs of non-space characters.</summary>
    public static IReadOnlyList<TextSpan> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new List<TextSpan>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > start)
            {
                result.Add(new TextSpan(start, i));
            }
        }

        return result;
    }

    private static void AddSplit(string text, int start, int end, int maxLength, List<TextSpan> result)
    {
        var trimmedEnd = end;
        while (trimmedEnd > start && char.IsWhiteSpace(text[trimmedEnd - 1]))
        {
            trimmedEnd--;
        }

        if (trimmedEnd - start <= maxLength)
        {
            if (trimmedEnd > start)
            {
                result.Add(new TextSpan(start, trimmedEnd));
            }

            return;
        }

        var cut = BestCut(text, start, trimmedEnd);
        AddSplit(text, start, cut, maxLength, result);
        AddSplit(text, SkipSpace(text, cut), trimmedEnd, maxLength, result);
    }

    /// <summary>After a comma or semicolon nearest the middle; otherwise the space nearest it.</summary>
    private static int BestCut(string text, int start, int end)
    {
        var middle = (start + end) / 2;
        var best = -1;
        var bestDistance = int.MaxValue;

        for (var i = start + 1; i < end - 1; i++)
        {
            if (text[i] is ',' or ';' or ':' or '—' && char.IsWhiteSpace(text[i + 1]))
            {
                var distance = Math.Abs(i + 1 - middle);
                if (distance < bestDistance && distance < (end - start) / 3)
                {
                    best = i + 1;
                    bestDistance = distance;
                }
            }
        }

        if (best > 0)
        {
            return best;
        }

        for (var d = 0; d < (end - start) / 2; d++)
        {
            if (middle + d < end && char.IsWhiteSpace(text[middle + d]))
            {
                return middle + d;
            }

            if (middle - d > start && char.IsWhiteSpace(text[middle - d]))
            {
                return middle - d;
            }
        }

        return middle;
    }

    private static int SentenceEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                return i + 1;
            }

            if (text[i] is '.' or '!' or '?')
            {
                var j = i + 1;

                // "e.g." and "3.5" are not sentence ends: require a space, then not a lower-case letter or digit.
                while (j < text.Length && text[j] is '.' or '!' or '?' or '"' or '\'' or ')' or '”' or '’')
                {
                    j++;
                }

                if (j >= text.Length)
                {
                    return text.Length;
                }

                if (char.IsWhiteSpace(text[j]))
                {
                    var k = SkipSpace(text, j);
                    if (k >= text.Length || !(char.IsLower(text[k]) || char.IsDigit(text[k])))
                    {
                        return j;
                    }
                }
            }
        }

        return text.Length;
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }
}
