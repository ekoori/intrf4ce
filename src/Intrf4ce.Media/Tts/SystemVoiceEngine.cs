using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using Intrf4ce.Core.Narration;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;

namespace Intrf4ce.Media.Tts;

/// <summary>
/// The free, offline voice every Windows machine has (SAPI).
/// </summary>
/// <remarks>
/// SAPI reports word positions, but for file output they are the write position at the moment
/// a chunk was flushed, not when the word starts, so they are not used. Timing is estimated
/// over the clip's real length and then pinned to the pauses found in the audio, which keeps
/// cues within a word or two. Other platforms get their own engines in M4.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemVoiceEngine(SystemVoiceSettings settings, VoiceCache clips) : ITtsEngine
{
    private const string Kind = "system";

    public string Name => "system voice";

    public bool IsAvailable => true;

    /// <summary>Installed voices, for settings and diagnostics.</summary>
    public static IReadOnlyList<string> Voices()
    {
        using var synth = new SpeechSynthesizer();
        return [.. synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)];
    }

    public Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
        Task.Run(() => Synthesize(text), cancellationToken);

    private VoiceClip Synthesize(string text)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Voice}{settings.Rate}{text}")))[..32].ToLowerInvariant();
        var name = key + ".wav";
        var kept = clips.Existing(Kind, name);
        var path = Path.Combine(kept ?? clips.Target(Kind), name);

        if (kept is null)
        {
            var temporary = path + ".tmp";

            using (var synth = new SpeechSynthesizer())
            {
                if (settings.Voice.Length > 0)
                {
                    try
                    {
                        synth.SelectVoice(settings.Voice);
                    }
                    catch (ArgumentException)
                    {
                        // An unknown voice name falls back to the default rather than failing.
                    }
                }

                synth.Rate = Math.Clamp(settings.Rate, -10, 10);
                synth.SetOutputToWaveFile(temporary, new SpeechAudioFormatInfo(44100, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synth.Speak(text);
                synth.SetOutputToNull();
            }

            File.Move(temporary, path, overwrite: true);
            clips.Share(Kind, Path.GetDirectoryName(path)!, name);
        }

        var levels = Envelope.FromFile(path);
        var duration = Math.Max(0.5, (new FileInfo(path).Length - 44) / (44100.0 * 2));
        var (leadIn, tail) = Envelope.Edges(levels);
        var timing = SpeechTiming.Estimate(text, duration, leadIn, tail)
            .AlignToPauses(Envelope.Pauses(levels));

        return new VoiceClip(path, timing, Name, 0, levels);
    }
}

/// <summary>No voice: subtitles timed at reading speed.</summary>
public sealed class SilentEngine(double charactersPerSecond) : ITtsEngine
{
    public string Name => "subtitles only";

    public bool IsAvailable => true;

    public Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var duration = SpeechTiming.ReadingDuration(text, charactersPerSecond);
        return Task.FromResult(new VoiceClip(null, SpeechTiming.Estimate(text, duration, 0, 0.3), Name, 0, []));
    }
}
