using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Intrf4ce.Core.Narration;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;

namespace Intrf4ce.Media.Tts;

/// <summary>
/// The offline voice a Mac or a Linux box already has: macOS <c>say</c>, or <c>piper</c> or
/// <c>espeak-ng</c> on Linux, whichever is installed.
/// </summary>
/// <remarks>
/// Each writes a WAV file and says nothing about timing, so cues are timed the same way as on
/// Windows: estimated over the clip's real length, then pinned to the pauses in the audio.
/// Clips are cached by voice, rate and text, so a slide replayed costs nothing.
/// <para>
/// Piper needs a voice model: put its .onnx path in <c>system_voice.voice</c>. For
/// <c>say</c> and espeak the same setting is a voice name ("Daniel", "en-gb").
/// </para>
/// </remarks>
public sealed class CommandVoiceEngine : ITtsEngine
{
    private const string Kind = "command";

    private readonly SystemVoiceSettings _settings;
    private readonly VoiceCache _clips;
    private readonly Tool? _tool;

    public CommandVoiceEngine(SystemVoiceSettings settings, VoiceCache clips)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);

        _settings = settings;
        _clips = clips;
        _tool = Find();
    }

    public string Name => _tool?.Name ?? "system voice";

    public bool IsAvailable => _tool is not null;

    public async Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var tool = _tool ?? throw new VoiceUnavailableException("there is no system voice on this machine");
        return await SpeakAsync(text, tool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first of the platform's voices that is actually installed.</summary>
    private static Tool? Find()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Which("say") is { } say ? new Tool("say", say, Voice.Say) : null;
        }

        if (OperatingSystem.IsLinux())
        {
            // Piper sounds far better, so it wins when it is there and has a model.
            if (Which("piper") is { } piper)
            {
                return new Tool("piper", piper, Voice.Piper);
            }

            if ((Which("espeak-ng") ?? Which("espeak")) is { } espeak)
            {
                return new Tool("espeak", espeak, Voice.Espeak);
            }
        }

        return null;
    }

    private static string? Which(string program)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return paths
            .Select(folder => Path.Combine(folder.Trim(), program))
            .FirstOrDefault(File.Exists);
    }

    private async Task<VoiceClip> SpeakAsync(string text, Tool tool, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tool.Name}{_settings.Voice}{_settings.Rate}{text}")))[..32].ToLowerInvariant();
        var name = key + ".wav";
        var kept = _clips.Existing(Kind, name);
        var path = Path.Combine(kept ?? _clips.Target(Kind), name);

        if (kept is null)
        {
            var temporary = path + ".tmp";
            await RunAsync(tool, text, temporary, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(temporary) || new FileInfo(temporary).Length < 128)
            {
                throw new VoiceUnavailableException($"{tool.Name} produced no audio");
            }

            File.Move(temporary, path, overwrite: true);
            _clips.Share(Kind, Path.GetDirectoryName(path)!, name);
        }

        var levels = Envelope.FromFile(path);
        if (levels.Count == 0)
        {
            throw new VoiceUnavailableException($"{tool.Name}'s audio could not be read back");
        }

        var duration = levels.Count * VoiceClip.LevelStep;
        var (leadIn, tail) = Envelope.Edges(levels);
        var timing = SpeechTiming.Estimate(text, duration, leadIn, tail).AlignToPauses(Envelope.Pauses(levels));
        return new VoiceClip(path, timing, Name, 0, levels);
    }

    private async Task RunAsync(Tool tool, string text, string output, CancellationToken cancellationToken)
    {
        // Words per minute, from the same -10..10 scale the Windows voice uses.
        var rate = (175 + (Math.Clamp(_settings.Rate, -10, 10) * 12)).ToString(CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(tool.Path)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        switch (tool.Kind)
        {
            case Voice.Say:
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add(output);
                start.ArgumentList.Add("--data-format=LEI16@22050");
                start.ArgumentList.Add("-r");
                start.ArgumentList.Add(rate);
                if (_settings.Voice.Length > 0)
                {
                    start.ArgumentList.Add("-v");
                    start.ArgumentList.Add(_settings.Voice);
                }

                start.ArgumentList.Add("-f");
                start.ArgumentList.Add("-");
                break;

            case Voice.Espeak:
                start.ArgumentList.Add("-w");
                start.ArgumentList.Add(output);
                start.ArgumentList.Add("-s");
                start.ArgumentList.Add(rate);
                if (_settings.Voice.Length > 0)
                {
                    start.ArgumentList.Add("-v");
                    start.ArgumentList.Add(_settings.Voice);
                }

                start.ArgumentList.Add("--stdin");
                break;

            case Voice.Piper:
                if (_settings.Voice.Length == 0)
                {
                    throw new VoiceUnavailableException("piper needs a voice model: put its .onnx path in system_voice.voice");
                }

                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(_settings.Voice);
                start.ArgumentList.Add("--output_file");
                start.ArgumentList.Add(output);
                break;
        }

        using var process = Process.Start(start) ?? throw new VoiceUnavailableException($"could not start {tool.Path}");
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var errors = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new VoiceUnavailableException($"{tool.Name} failed: {errors.Trim()}");
        }
    }

    private enum Voice
    {
        Say,
        Espeak,
        Piper,
    }

    private sealed record Tool(string Name, string Path, Voice Kind);
}
