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
    private readonly NarrationSettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly Queue<string> _asides = new();
    private Performance? _now;
    private CancellationTokenSource? _preparing;
    private IDisposable? _advance;
    private bool _waitingForNext;
    private bool _muted;
    private string _advanceAsLoaded;
    private (string SlideId, string Narration)? _performed;

    public Narrator(
        VoiceService voices,
        AudioSystem audio,
        StageController stage,
        SubtitleBar subtitles,
        NarrationSettings settings)
    {
        _voices = voices;
        _audio = audio;
        _stage = stage;
        _subtitles = subtitles;
        _settings = settings;
        _muted = settings.Muted;
        _audio.Volume = _muted ? 0 : (float)settings.Volume;
        _subtitles.Enabled = settings.Subtitles;
        _advanceAsLoaded = settings.Advance;
        Continuous = string.Equals(settings.Advance, "auto", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>A slide was performed to the user; the deck remembers, so it is not repeated.</summary>
    public event EventHandler<string>? SlideSpoken;

    /// <summary>stopped, idle, preparing, speaking, paused or waiting.</summary>
    public string State => _now is { Paused: true } ? "paused"
        : Held ? "stopped by the user"
        : _waitingForNext ? "waiting for the next slide"
        : _preparing is not null && _now is null ? "preparing the voice"
        : _now is null ? "idle"
        : "speaking";

    public bool IsSpeaking => _now is { Paused: false };

    /// <summary>
    /// Whether the presentation runs on through the deck by itself, or stops at the end of the
    /// slide it is on.
    /// </summary>
    /// <remarks>
    /// The tick beside the play button. It starts from <c>narration.advance</c> and follows
    /// that setting when the file changes, but the tick wins in between: someone who wants one
    /// answer read out and then quiet should not have to edit a file to get it.
    /// </remarks>
    public bool Continuous { get; private set; }

    /// <summary>The voice is silenced; slides still change and subtitles still show.</summary>
    public bool Muted => _muted;

    public bool SubtitlesOn => _subtitles.Enabled;

    /// <summary>
    /// The last slide to play before stopping, for an agent that added three slides and wants
    /// those three heard and nothing else.
    /// </summary>
    public string? StopAfter { get; set; }

    /// <summary>The user stopped the presentation: nothing speaks and no slide moves on by itself.</summary>
    /// <remarks>
    /// Distinct from a pause, which holds one narration where it is. Held survives moving
    /// between slides, so the user can read through a deck at their own pace with the voice off
    /// and nothing running away from them.
    /// </remarks>
    public bool Held { get; private set; }

    /// <summary>Stop: silence, and no slide advances until the user presses play.</summary>
    public void Hold()
    {
        Held = true;
        Stop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The play button: speak this slide, resuming whatever was paused or stopped.</summary>
    public void Play(Slide? current)
    {
        Held = false;
        Toggle(current);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A slide came into view: speak it if autoplay is on.</summary>
    public void SlideShown(Slide? slide, bool play = true)
    {
        Stop();
        _waitingForNext = false;
        _performed = null;

        if (slide is null)
        {
            return;
        }

        if (play && !Held && _settings.Autoplay && !string.IsNullOrWhiteSpace(slide.Narration))
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
        if (Held || _now is not null || _preparing is not null || !_settings.Autoplay || string.IsNullOrWhiteSpace(slide.Narration))
        {
            return;
        }

        if (_performed != (slide.Id, slide.Narration))
        {
            _ = PerformAsync(slide.Narration!, slide);
        }
    }

    /// <summary>The settings file changed: take the volume and subtitles from it now.</summary>
    /// <remarks>
    /// Subtitles and continuous play follow the file only when the file itself changed its
    /// mind about them; otherwise a save of some unrelated setting would undo what the user
    /// just pressed on the transport bar.
    /// </remarks>
    public void SettingsChanged()
    {
        SetMuted(_settings.Muted);
        _audio.Volume = _muted ? 0 : (float)_settings.Volume;
        _subtitles.Enabled = _settings.Subtitles;
        if (!_settings.Subtitles)
        {
            _subtitles.Hide();
        }

        if (!string.Equals(_advanceAsLoaded, _settings.Advance, StringComparison.OrdinalIgnoreCase))
        {
            _advanceAsLoaded = _settings.Advance;
            Continuous = string.Equals(_settings.Advance, "auto", StringComparison.OrdinalIgnoreCase);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Play, pause or stop, each said outright rather than toggled.
    /// </summary>
    /// <remarks>
    /// The keys and the buttons toggle, because that is what a person pressing them means. An
    /// agent asking for "play" means play, whatever is happening now, and must not silence a
    /// slide by asking twice.
    /// </remarks>
    public void Perform(string action, Slide? current)
    {
        switch (action)
        {
            case "play":
                Held = false;
                if (_now is { Paused: true } || (_now is null && _preparing is null))
                {
                    Toggle(current);
                }

                break;

            case "pause":
                if (_now is { Paused: false })
                {
                    Toggle(current);
                }

                break;

            case "stop":
                Hold();
                return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Space: pause, resume, or play the slide again once it has finished.</summary>
    public void Toggle(Slide? current)
    {
        if (_now is { } now)
        {
            now.Paused = !now.Paused;

            // A pause someone asked for holds the presentation, as stop does: the next slide,
            // reached by hand, waits for play rather than starting to talk over the decision.
            Held = now.Paused;
            if (now.Paused)
            {
                now.Clock.Stop();
                _audio.Pause();
                _advance?.Dispose();
                _advance = null;
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

    public void Mute() => SetMuted(!_muted);

    /// <summary>
    /// Silences the voice, or lets it speak again.
    /// </summary>
    /// <remarks>
    /// While muted nothing is asked of a voice at all — subtitles run at reading speed — so a
    /// presentation built with the sound off costs no characters. Unmuting part-way through a
    /// slide that was being read silently speaks that slide from the top, which is what someone
    /// reaching for the sound wants to hear.
    /// </remarks>
    public void SetMuted(bool muted)
    {
        if (_muted == muted)
        {
            return;
        }

        _muted = muted;
        _audio.Volume = _muted ? 0 : (float)_settings.Volume;

        if (!muted && _now is { Clip.AudioPath: null, Slide: { } slide } && slide.Narration is { Length: > 0 } narration)
        {
            Stop();
            _ = PerformAsync(narration, slide);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Speaks a slide again in the current voice, rather than the recording it already has.
    /// </summary>
    /// <remarks>
    /// Changing the voice never re-records what has been spoken — that would be paid for — so
    /// this is how a slide, or every slide, is asked for again on purpose.
    /// </remarks>
    /// <returns>How many recordings were discarded.</returns>
    public int Revoice(IEnumerable<Slide> slides, Slide? current)
    {
        ArgumentNullException.ThrowIfNull(slides);

        var discarded = 0;
        foreach (var slide in slides)
        {
            if (slide.Narration is { Length: > 0 } narration)
            {
                discarded += _voices.Forget(CueParser.Parse(narration).Text);
            }
        }

        if (current?.Narration is { Length: > 0 } spoken && !Held)
        {
            Stop();
            _ = PerformAsync(spoken, current);
        }

        return discarded;
    }

    public void ToggleSubtitles() => SetSubtitles(!_subtitles.Enabled);

    public void SetSubtitles(bool on)
    {
        _subtitles.Enabled = on;
        if (!on)
        {
            _subtitles.Hide();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keep going through the deck when a slide's narration ends, or stop there.</summary>
    public void SetContinuous(bool on)
    {
        Continuous = on;
        if (!on)
        {
            _advance?.Dispose();
            _advance = null;
            _waitingForNext = false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
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
        // Muted spends nothing, and that includes getting ahead.
        if (_muted)
        {
            return;
        }

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
        if (!Held && Continuous && _waitingForNext && _stage.Deck is { } deck && _stage.CurrentIndex < deck.Count - 1)
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
            clip = _muted ? _voices.Silent(parsed.Text) : await _voices.PrepareAsync(parsed.Text, preparing.Token);
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

        // Counted as performed once it starts, not once it ends: a user who has heard the first
        // half and moved on does not want it read to them again.
        if (slide is not null)
        {
            SlideSpoken?.Invoke(this, slide.Id);
        }

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

        if (now.Slide is not { } slide)
        {
            return;
        }

        // The end of what the agent asked to be played: stop here rather than reading on into
        // slides that answer a question nobody asked twice.
        if (StopAfter == slide.Id)
        {
            StopAfter = null;
            return;
        }

        // A quiz waits for its answer unless the agent said otherwise.
        if (!Held && Continuous
            && (slide.Advance ?? (slide.Elements.Any(e => e is QuizElement) ? "manual" : "auto")) == "auto")
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
