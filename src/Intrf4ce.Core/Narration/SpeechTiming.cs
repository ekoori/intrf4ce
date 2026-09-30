namespace Intrf4ce.Core.Narration;

/// <summary>
/// When each character of spoken text is heard, so cues and subtitles follow the voice.
/// </summary>
/// <remarks>
/// Three sources, from best to worst: ElevenLabs' character alignment, which is exact; an
/// estimate spread over the measured length of the clip, refined by the pauses found in the
/// audio; and a plain estimate from reading speed when nothing is spoken at all. Everything
/// downstream only ever asks "what time is character n", so it does not care which it got.
/// </remarks>
public sealed class SpeechTiming
{
    private readonly double[] _starts;

    private SpeechTiming(string text, double[] starts, double duration, bool exact)
    {
        Text = text;
        _starts = starts;
        Duration = duration;
        IsExact = exact;
    }

    public string Text { get; }

    /// <summary>Seconds from the start of the clip to its end.</summary>
    public double Duration { get; }

    /// <summary>True when the times came from the voice itself rather than an estimate.</summary>
    public bool IsExact { get; }

    /// <summary>When the character at <paramref name="offset"/> starts; the end of the clip past the text.</summary>
    public double TimeAt(int offset)
    {
        if (_starts.Length == 0 || offset >= _starts.Length)
        {
            return Duration;
        }

        return _starts[Math.Max(0, offset)];
    }

    /// <summary>The last character that has started by <paramref name="seconds"/>, or -1 before the first.</summary>
    public int OffsetAt(double seconds)
    {
        var lo = 0;
        var hi = _starts.Length - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_starts[mid] <= seconds)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// The timing of narration that carries audio tags, from the timing of the same words
    /// spoken without them.
    /// </summary>
    /// <remarks>
    /// For a voice that cannot take the tags: it speaks the stripped text, and every character
    /// of the original — a tag's included — gets the time of the character it became, so cue
    /// and caption offsets written against the original still land where they should.
    /// </remarks>
    public static SpeechTiming Spread(string text, SpeechTiming spoken, IReadOnlyList<int> map)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spoken);
        ArgumentNullException.ThrowIfNull(map);

        var starts = new double[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            starts[i] = spoken.TimeAt(i < map.Count ? map[i] : int.MaxValue);
        }

        MakeMonotonic(starts);
        return new SpeechTiming(text, starts, spoken.Duration, spoken.IsExact);
    }

    /// <summary>
    /// From a voice that reports when each character starts.
    /// </summary>
    /// <remarks>
    /// The voice may normalise the text (numbers spelled out, quotes straightened), so the
    /// reported characters are matched back to ours by walking both strings; where they
    /// disagree the estimate fills in. A mismatch costs sync precision, never a crash.
    /// </remarks>
    public static SpeechTiming FromCharacters(
        string text,
        IReadOnlyList<string> characters,
        IReadOnlyList<double> startSeconds,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(startSeconds);

        var fallback = Estimate(text, duration);
        var starts = new double[text.Length];
        var matched = new bool[text.Length];
        var j = 0;

        for (var i = 0; i < text.Length && j < characters.Count; i++)
        {
            // Look a short way ahead in the voice's characters for ours.
            var hit = -1;
            for (var k = j; k < Math.Min(characters.Count, j + 8); k++)
            {
                if (characters[k].Length > 0 && characters[k][0] == text[i])
                {
                    hit = k;
                    break;
                }
            }

            if (hit >= 0 && hit < startSeconds.Count)
            {
                starts[i] = startSeconds[hit];
                matched[i] = true;
                j = hit + 1;
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!matched[i])
            {
                starts[i] = fallback.TimeAt(i);
            }
        }

        MakeMonotonic(starts);
        return new SpeechTiming(text, starts, duration, exact: matched.Count(m => m) > text.Length * 0.9);
    }

    /// <summary>Spreads the clip over the text, weighted so punctuation takes the time a pause does.</summary>
    /// <param name="leadIn">Silence before the first word, if known.</param>
    /// <param name="tail">Silence after the last word, if known.</param>
    public static SpeechTiming Estimate(string text, double duration, double leadIn = 0.05, double tail = 0.15)
    {
        ArgumentNullException.ThrowIfNull(text);

        var weights = new double[text.Length];
        var total = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            weights[i] = Weight(text[i]);
            total += weights[i];
        }

        var speaking = Math.Max(0.1, duration - leadIn - tail);
        var starts = new double[text.Length];
        var clock = leadIn;

        for (var i = 0; i < text.Length; i++)
        {
            starts[i] = clock;
            clock += total <= 0 ? 0 : speaking * weights[i] / total;
        }

        return new SpeechTiming(text, starts, duration, exact: false);
    }

    /// <summary>How long a clip of <paramref name="text"/> would take read aloud at <paramref name="charactersPerSecond"/>.</summary>
    public static double ReadingDuration(string text, double charactersPerSecond)
    {
        ArgumentNullException.ThrowIfNull(text);

        var weight = text.Sum(Weight);
        return Math.Max(1.2, weight / Math.Max(1, charactersPerSecond));
    }

    /// <summary>
    /// Pins sentence and clause ends to the pauses actually found in the audio.
    /// </summary>
    /// <remarks>
    /// A proportional estimate drifts within a long sentence, but a voice always pauses at a
    /// full stop and usually at a comma. Matching those punctuation marks to the silences in
    /// the clip, in order, and interpolating between them keeps each cue within a word or two.
    /// </remarks>
    public SpeechTiming AlignToPauses(IReadOnlyList<(double Start, double End)> pauses)
    {
        ArgumentNullException.ThrowIfNull(pauses);

        if (pauses.Count == 0 || IsExact || Text.Length == 0)
        {
            return this;
        }

        // Anchor points: (character offset, time).
        var anchors = new List<(int Offset, double Time)> { (0, _starts[0]) };
        var pauseIndex = 0;

        for (var i = 0; i < Text.Length && pauseIndex < pauses.Count; i++)
        {
            if (!IsBreak(Text[i]))
            {
                continue;
            }

            var next = NextWordStart(i);
            if (next >= Text.Length)
            {
                break;
            }

            var estimate = _starts[next];

            // The pause nearest the estimate that is not behind the previous anchor.
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var p = pauseIndex; p < pauses.Count; p++)
            {
                var distance = Math.Abs(pauses[p].End - estimate);
                if (distance < bestDistance)
                {
                    best = p;
                    bestDistance = distance;
                }
            }

            // Only trust a pause that is plausibly this one.
            if (best >= 0 && bestDistance < 1.5 && pauses[best].End > anchors[^1].Time)
            {
                anchors.Add((next, pauses[best].End));
                pauseIndex = best + 1;
            }
        }

        anchors.Add((Text.Length, _starts.Length > 0 ? Math.Max(_starts[^1], anchors[^1].Time) : Duration));

        var starts = new double[Text.Length];
        for (var a = 0; a < anchors.Count - 1; a++)
        {
            var (from, fromTime) = anchors[a];
            var (to, toTime) = anchors[a + 1];
            var originalFrom = _starts[Math.Min(from, _starts.Length - 1)];
            var originalTo = to < _starts.Length ? _starts[to] : Duration;
            var span = originalTo - originalFrom;

            for (var i = from; i < to && i < starts.Length; i++)
            {
                var t = span <= 0 ? 0 : (_starts[i] - originalFrom) / span;
                starts[i] = fromTime + t * (toTime - fromTime);
            }
        }

        MakeMonotonic(starts);
        return new SpeechTiming(Text, starts, Duration, exact: false);
    }

    private int NextWordStart(int index)
    {
        var i = index + 1;
        while (i < Text.Length && char.IsWhiteSpace(Text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsBreak(char c) => c is '.' or '!' or '?' or ',' or ';' or ':' or '—' or '\n';

    private static double Weight(char c) => c switch
    {
        '.' or '!' or '?' or '\n' => 7,
        ',' or ';' or ':' or '—' or '–' => 3.5,
        ' ' => 0.7,
        _ when char.IsLetterOrDigit(c) => 1,
        _ => 0.5,
    };

    private static void MakeMonotonic(double[] starts)
    {
        for (var i = 1; i < starts.Length; i++)
        {
            if (starts[i] < starts[i - 1])
            {
                starts[i] = starts[i - 1];
            }
        }
    }
}
