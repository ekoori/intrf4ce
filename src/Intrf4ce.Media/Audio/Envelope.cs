namespace Intrf4ce.Media.Audio;

/// <summary>Loudness over time and the silences in a clip, from its samples.</summary>
public static class Envelope
{
    /// <summary>Set once at startup; decoding MP3 needs the audio engine.</summary>
    public static AudioSystem? Audio { get; set; }

    /// <summary>RMS per 50 ms window, normalised so the loudest window is 1.</summary>
    public static IReadOnlyList<float> FromFile(string path)
    {
        try
        {
            var samples = Audio?.Decode(path);
            return samples is null ? [] : Levels(samples, AudioSystem.Format.SampleRate);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    public static IReadOnlyList<float> Levels(ReadOnlySpan<float> samples, int rate, double step = 0.05)
    {
        var window = Math.Max(1, (int)(rate * step));
        var count = samples.Length / window;
        var levels = new float[count];
        var peak = 1e-6f;

        for (var w = 0; w < count; w++)
        {
            double sum = 0;
            var slice = samples.Slice(w * window, window);
            foreach (var s in slice)
            {
                sum += s * s;
            }

            levels[w] = (float)Math.Sqrt(sum / window);
            peak = Math.Max(peak, levels[w]);
        }

        for (var w = 0; w < count; w++)
        {
            levels[w] /= peak;
        }

        return levels;
    }

    /// <summary>
    /// Stretches of near-silence at least <paramref name="minimum"/> long, in seconds.
    /// </summary>
    /// <remarks>
    /// Relative to the clip's own peak rather than an absolute level, so a quiet voice and a
    /// loud one pause the same way. Used to pin estimated word timings to where the voice
    /// actually breathes.
    /// </remarks>
    public static IReadOnlyList<(double Start, double End)> Pauses(IReadOnlyList<float> levels, double step = 0.05, double minimum = 0.15, float threshold = 0.08f)
    {
        var pauses = new List<(double, double)>();
        var start = -1;

        for (var i = 0; i <= levels.Count; i++)
        {
            var quiet = i < levels.Count && levels[i] < threshold;
            if (quiet && start < 0)
            {
                start = i;
            }
            else if (!quiet && start >= 0)
            {
                if ((i - start) * step >= minimum)
                {
                    pauses.Add((start * step, i * step));
                }

                start = -1;
            }
        }

        return pauses;
    }

    /// <summary>Silence before the first sound and after the last, in seconds.</summary>
    public static (double LeadIn, double Tail) Edges(IReadOnlyList<float> levels, double step = 0.05, float threshold = 0.08f)
    {
        var first = 0;
        while (first < levels.Count && levels[first] < threshold)
        {
            first++;
        }

        var last = levels.Count - 1;
        while (last > first && levels[last] < threshold)
        {
            last--;
        }

        return (first * step, Math.Max(0, (levels.Count - 1 - last) * step));
    }
}
