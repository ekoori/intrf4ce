using Intrf4ce.Core.Narration;

namespace Intrf4ce.Media.Tts;

/// <summary>Speech ready to play: the audio file and when each character is heard.</summary>
/// <param name="AudioPath">An MP3 or WAV file, or null when narration is subtitles only.</param>
/// <param name="Engine">Which voice made it, for the status line and get_state.</param>
/// <param name="Billed">Characters this synthesis cost (0 for a cache hit or a free voice).</param>
/// <param name="Levels">Loudness envelope, one value per <see cref="LevelStep"/>: where the voice starts, stops and pauses.</param>
public sealed record VoiceClip(
    string? AudioPath,
    SpeechTiming Timing,
    string Engine,
    int Billed,
    IReadOnlyList<float> Levels)
{
    public const double LevelStep = 0.05;

    public double Duration => Timing.Duration;
}

/// <summary>Turns text into a <see cref="VoiceClip"/>.</summary>
public interface ITtsEngine
{
    string Name { get; }

    /// <summary>Whether it can work at all here (a key is set, the platform has the voice).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether <c>[whispers]</c> and its kind are directions to it rather than words to read.
    /// </summary>
    /// <remarks>False for every voice but ElevenLabs v3; the others are given the text without them.</remarks>
    bool UnderstandsTags => false;

    /// <exception cref="VoiceUnavailableException">It cannot speak this now (budget, network, no voice).</exception>
    Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

/// <summary>A voice declined; the caller falls back to the next one.</summary>
public sealed class VoiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
