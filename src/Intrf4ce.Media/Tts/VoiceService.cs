using System.Collections.Concurrent;
using Intrf4ce.Core;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;

namespace Intrf4ce.Media.Tts;

/// <summary>
/// Chooses a voice and falls back gracefully: ElevenLabs, then the system voice, then subtitles.
/// </summary>
/// <remarks>
/// A voice that fails (no network, budget spent, a 401) never costs the user a slide: the next
/// voice down speaks it, and the reason is reported once so the user and agent know. Clips in
/// flight are shared, so a slide being prepared ahead and then shown does not synthesise twice.
/// </remarks>
public sealed class VoiceService
{
    private readonly IReadOnlyList<ITtsEngine> _engines;
    private readonly ConcurrentDictionary<string, Task<VoiceClip>> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public VoiceService(IReadOnlyList<ITtsEngine> engines, ElevenLabsEngine? elevenLabs, VoiceCache clips)
    {
        _engines = engines;
        ElevenLabs = elevenLabs;
        Clips = clips;
    }

    /// <summary>Where clips are kept; point <see cref="VoiceCache.Session"/> at the open deck.</summary>
    public VoiceCache Clips { get; }

    /// <summary>A voice fell back, with the reason, reported once per distinct reason.</summary>
    public event EventHandler<string>? Notice;

    public ElevenLabsEngine? ElevenLabs { get; }

    /// <summary>The voice that will be tried first.</summary>
    public string Primary => _engines.FirstOrDefault(e => e.IsAvailable)?.Name ?? "none";

    /// <summary>One line for get_state: which voice, and what is left of the budget.</summary>
    public string Describe()
    {
        if (ElevenLabs is { IsAvailable: true } eleven && Primary == eleven.Name)
        {
            var budget = eleven.Budget;
            var account = budget.AccountRemaining is { } left ? $", account has {left:N0} left" : "";
            return $"ElevenLabs ({budget.UsedThisMonth:N0} of {budget.MonthlyCap:N0} characters used this month{account})";
        }

        return Primary;
    }

    public static VoiceService Create(AppSettings settings, AppPaths paths, HttpClient http, AudioSystem audio)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        Envelope.Audio = audio;
        var clips = new VoiceCache(paths.VoiceCache);
        var engines = new List<ITtsEngine>();
        ElevenLabsEngine? eleven = null;
        var choice = settings.Narration.Engine.ToLowerInvariant();

        if (choice is "auto" or "elevenlabs")
        {
            eleven = new ElevenLabsEngine(http, SettingsFile.ElevenLabsKey(paths, settings.ElevenLabs), settings.ElevenLabs, clips);
            engines.Add(eleven);
        }

        if (choice is not "none" && OperatingSystem.IsWindows())
        {
            engines.Add(new SystemVoiceEngine(settings.SystemVoice, clips));
        }
        else if (choice is not "none" && new CommandVoiceEngine(settings.SystemVoice, clips) is { IsAvailable: true } command)
        {
            // macOS and Linux speak through whatever they have: say, piper or espeak.
            engines.Add(command);
        }

        engines.Add(new SilentEngine(settings.Narration.ReadingSpeed));
        return new VoiceService(engines, eleven, clips);
    }

    /// <summary>Speech for <paramref name="text"/>, from the best voice that will do it.</summary>
    public Task<VoiceClip> PrepareAsync(string text, CancellationToken cancellationToken) =>
        _inFlight.GetOrAdd(text, t => SynthesizeAsync(t, cancellationToken)).ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                _inFlight.TryRemove(text, out _);
            }

            return task;
        }, TaskScheduler.Default).Unwrap();

    private async Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var engine in _engines.Where(e => e.IsAvailable))
        {
            try
            {
                return await engine.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);
            }
            catch (VoiceUnavailableException ex)
            {
                Report(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                Report($"{engine.Name} failed: {ex.Message}");
            }
        }

        // The silent engine never throws; this is unreachable unless the list was emptied.
        throw new InvalidOperationException("no voice is available");
    }

    private void Report(string reason)
    {
        lock (_reported)
        {
            if (!_reported.Add(reason))
            {
                return;
            }
        }

        Notice?.Invoke(this, reason);
    }
}
