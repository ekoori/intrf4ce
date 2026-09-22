using System.Diagnostics;
using Avalonia.Threading;
using Intrf4ce.App.Hud;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Narration;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;
using Intrf4ce.Media.Tts;

namespace Intrf4ce.App.Stage;

/// <summary>
/// Performs narration: speaks it, subtitles it, and fires its cues as the voice reaches them.
/// </summary>
/// <remarks>
/// The agent is not in the loop for any of this. It wrote the script (narration with cues) in
/// one tool call; timing is resolved here against the voice's own clock, which is the only way
/// a zoom can land on the word that names the thing.
/// <para>
/// Cues fire a quarter of a second early: the camera takes that long to get going, so a
/// focus cued on "ligament" arrives while "ligament" is being said rather than after it.
/// </para>
/// </remarks>
public sealed class Narrator
{
    private const double CueLead = 0.25;

    private readonly VoiceService _voices;
    private readonly AudioSystem _audio;
    private readonly StageController _stage;
    private readonly SubtitleBar _subtitles;
    private readonly AgentOrb _orb;
    private readonly NarrationSettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly Queue<string> _asides = new();
    private Performance? _now;
    private CancellationTokenSource? _preparing;
    private IDisposable? _advance;
    private bool _waitingForNext;
    private bool _muted;
    private (string SlideId, string Narration)? _performed;

    public Narrator(
        VoiceService voices,
        AudioSystem audio,
        StageController stage,
        SubtitleBar subtitles,
        AgentOrb orb,
        NarrationSettings settings)
    {
        _voices = voices;
        _audio = audio;
        _stage = stage;
        _subtitles = subtitles;
        _orb = orb;
        _settings = settings;
        _audio.Volume = (float)settings.Volume;
        _subtitles.Enabled = settings.Subtitles;

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Tick());
        _audio.Ended += (_, generation) => Dispatcher.UIThread.Post(() =>
        {
            if (_now is { } now && now.Generation == generation)
            {
                Finish(now);
            }
        });
    }

    /// <summary>Speaking started or stopped; for the orb and get_state.</summary>
    public event EventHandler? StateChanged;

    /// <summary>idle, preparing, speaking, paused or waiting.</summary>
    public string State => _waitingForNext ? "waiting for the next slide"
        : _preparing is not null && _now is null ? "preparing the voice"
        : _now is null ? "idle"
        : _now.Paused ? "paused"
        : "speaking";

    public bool IsSpeaking => _now is { Paused: false };

    /// <summary>A slide came into view: speak it if autoplay is on.</summary>
    public void SlideShown(Slide? slide)
    {
        Stop();
        _waitingForNext = false;
        _performed = null;

        if (slide is null)
        {
            return;
        }

        if (_settings.Autoplay && !string.IsNullOrWhiteSpace(slide.Narration))
        {
            _ = PerformAsync(slide.Narration!, slide);
        }
    }

    /// <summary>
    /// The slide on screen was changed by the agent. If it now has narration that has not been
    /// spoken and nothing else is playing, speak it; a narration already under way carries on.
    /// </summary>
    public void SlideUpdated(Slide slide)
    {
        if (_now is not null || _preparing is not null || !_settings.Autoplay || string.IsNullOrWhiteSpace(slide.Narration))
        {
            return;
        }

        if (_performed != (slide.Id, slide.Narration))
        {
            _ = PerformAsync(slide.Narration!, slide);
        }
    }

    /// <summary>Space: pause, resume, or play the slide again once it has finished.</summary>
    public void Toggle(Slide? current)
    {
        if (_now is { } now)
        {
            now.Paused = !now.Paused;
            if (now.Paused)
            {
                now.Clock.Stop();
                _audio.Pause();
            }
            else
            {
                now.Clock.Start();
                _audio.Resume();
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (current?.Narration is { Length: > 0 } narration)
        {
            _ = PerformAsync(narration, current);
        }
    }

    public void Mute()
    {
        _muted = !_muted;
        _audio.Volume = _muted ? 0 : (float)_settings.Volume;
    }

    public void ToggleSubtitles()
    {
        _subtitles.Enabled = !_subtitles.Enabled;
        if (!_subtitles.Enabled)
        {
            _subtitles.Hide();
        }
    }

    /// <summary>The agent's <c>say</c>: spoken after whatever is being said now, not over it.</summary>
    public void Say(string text)
    {
        if (_now is null && _preparing is null)
        {
            _ = PerformAsync(text, slide: null);
        }
        else
        {
            _asides.Enqueue(text);
        }
    }

    /// <summary>Synthesises a slide ahead of time so it starts the moment it is shown.</summary>
    public void Prepare(Slide? slide)
    {
        if (slide?.Narration is { Length: > 0 } narration)
        {
            var text = CueParser.Parse(narration).Text;
            if (text.Length > 0)
            {
                _ = _voices.PrepareAsync(text, CancellationToken.None).ContinueWith(
                    t => Log.Write("preparing narration failed: " + t.Exception?.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>A slide arrived; if the last one finished and was waiting for it, move on.</summary>
    public void SlidesChanged()
    {
        if (_waitingForNext && _stage.Deck is { } deck && _stage.CurrentIndex < deck.Count - 1)
        {
            _waitingForNext = false;
            _stage.Next();
        }
    }

    public void Stop()
    {
        _preparing?.Cancel();
        _preparing = null;
        _advance?.Dispose();
        _advance = null;
        _asides.Clear();

        if (_now is not null)
        {
            _now = null;
            _audio.Stop();
            _tick.Stop();
            _subtitles.Hide();
            _orb.Level = 0;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PerformAsync(string narration, Slide? slide)
    {
        var parsed = CueParser.Parse(narration);
        if (parsed.Text.Length == 0)
        {
            return;
        }

        _preparing?.Cancel();
        var preparing = new CancellationTokenSource();
        _preparing = preparing;
        if (slide is not null)
        {
            _performed = (slide.Id, narration);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        VoiceClip clip;
        try
        {
            clip = await _voices.PrepareAsync(parsed.Text, preparing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("narration", ex);
            return;
        }

        // The user moved on while the voice was being made.
        if (preparing.IsCancellationRequested || !ReferenceEquals(_preparing, preparing))
        {
            return;
        }

        var performance = new Performance(slide, parsed, clip, Captions.Split(parsed.Text), Captions.Words(parsed.Text));

        if (clip.AudioPath is not null && _audio.IsAvailable)
        {
            try
            {
                performance.Generation = _audio.Play(clip.AudioPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Log.Error("playing narration", ex);
                performance.Generation = -1;
            }
        }
        else
        {
            performance.Generation = -1;
        }

        performance.Clock.Start();

        // Swapped in one step, so get_state never sees an idle moment between the two.
        _now = performance;
        _preparing = null;
        _tick.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);

        // While this slide speaks, get the next one's voice ready.
        if (slide is not null && _stage.Deck is { } deck && deck.IndexOf(slide.Id) is var index and >= 0 && index + 1 < deck.Count)
        {
            Prepare(deck.Slides[index + 1]);
        }
    }

    private void Tick()
    {
        if (_now is not { } now)
        {
            _tick.Stop();
            return;
        }

        if (now.Paused)
        {
            return;
        }

        var t = now.Generation >= 0 ? _audio.Position : now.Clock.Elapsed.TotalSeconds;
        var timing = now.Clip.Timing;

        while (now.NextCue < now.Parsed.Cues.Count && timing.TimeAt(now.Parsed.Cues[now.NextCue].Offset) - CueLead <= t)
        {
            Execute(now.Parsed.Cues[now.NextCue++]);
        }

        var offset = Math.Max(0, timing.OffsetAt(t));
        _subtitles.Show(now.Parsed.Text, now.Captions, now.Words, offset);
        _orb.Level = now.Generation >= 0 ? now.Clip.LevelAt(t) : 0.6;

        // Without audio there is no end event; the clock decides.
        if (now.Generation < 0 && t >= timing.Duration)
        {
            Finish(now);
        }
    }

    private void Execute(Cue cue)
    {
        switch (cue.Kind)
        {
            case CueKind.Focus:
                Report(_stage.Focus(cue.Target, null));
                break;

            case CueKind.Zoom:
                Report(_stage.Focus(cue.ElementId, cue.Region));
                break;

            case CueKind.Show:
                _stage.CurrentView?.SetVisible(cue.ElementId!, true);
                break;

            case CueKind.Hide:
                _stage.CurrentView?.SetVisible(cue.ElementId!, false);
                break;

            case CueKind.Clear:
                _stage.Focus(null, null);
                break;
        }

        static void Report(string? error)
        {
            if (error is not null)
            {
                Log.Write("cue failed: " + error);
            }
        }
    }

    private void Finish(Performance now)
    {
        if (!ReferenceEquals(_now, now))
        {
            return;
        }

        // Every cue that did not get its moment (a short clip, a late tick) still happens.
        while (now.NextCue < now.Parsed.Cues.Count)
        {
            Execute(now.Parsed.Cues[now.NextCue++]);
        }

        _now = null;
        _tick.Stop();
        _orb.Level = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (_now is null)
            {
                _subtitles.Hide();
            }
        }, TimeSpan.FromMilliseconds(900));
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (_asides.TryDequeue(out var aside))
        {
            _ = PerformAsync(aside, slide: null);
            return;
        }

        if (now.Slide is { } slide && (slide.Advance ?? _settings.Advance) == "auto")
        {
            _advance?.Dispose();
            _advance = DispatcherTimer.RunOnce(() => AdvanceFrom(slide), TimeSpan.FromSeconds(Math.Max(0, _settings.PauseAfter)));
        }
    }

    private void AdvanceFrom(Slide slide)
    {
        _advance = null;

        // Only if the user is still on the slide that finished.
        if (_stage.Deck is not { } deck || _stage.CurrentIndex < 0 || deck.Slides[_stage.CurrentIndex].Id != slide.Id)
        {
            return;
        }

        if (_stage.CurrentIndex < deck.Count - 1)
        {
            _stage.Next();
        }
        else
        {
            _waitingForNext = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Performance(
        Slide? slide, ParsedNarration parsed, VoiceClip clip, IReadOnlyList<TextSpan> captions, IReadOnlyList<TextSpan> words)
    {
        public Slide? Slide { get; } = slide;

        public ParsedNarration Parsed { get; } = parsed;

        public VoiceClip Clip { get; } = clip;

        public IReadOnlyList<TextSpan> Captions { get; } = captions;

        public IReadOnlyList<TextSpan> Words { get; } = words;

        public Stopwatch Clock { get; } = new();

        public int Generation { get; set; }

        public int NextCue { get; set; }

        public bool Paused { get; set; }
    }
}
