using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Narration;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;

namespace Intrf4ce.Media.Tts;

/// <summary>
/// ElevenLabs, with character timestamps, a content-addressed cache and a budget.
/// </summary>
/// <remarks>
/// The <c>with-timestamps</c> endpoint returns, beside the audio, when every character is
/// spoken. That is what lets a <c>[[focus]]</c> cue land on its word exactly rather than
/// roughly.
/// <para>
/// Clips are cached by the same contract as <c>dev/11labs</c>: sha256 of
/// <c>{"text","voiceId","modelId","settings"}</c>, first 32 hex characters. The same sentence
/// in the same voice is paid for once, ever; changing any voice setting is a new clip. The
/// two apps keep separate caches, so the shared contract is about predictability, not reuse.
/// </para>
/// <para>
/// Requests queue: a plan allows only a few at once and counts every application using the
/// key, and being told so is not a reason to hand the line to another voice, so a refusal for
/// that reason is waited out and tried again.
/// </para>
/// </remarks>
public sealed class ElevenLabsEngine : ITtsEngine
{
    private const string Api = "https://api.elevenlabs.io/v1";

    private const string Kind = "elevenlabs";
    private const int Attempts = 4;

    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly ElevenLabsSettings _settings;
    private readonly VoiceCache _clips;
    private readonly SemaphoreSlim _gate;
    private readonly VoiceBudget _budget;

    public ElevenLabsEngine(HttpClient http, string? key, ElevenLabsSettings settings, VoiceCache clips)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);

        _http = http;
        _key = key;
        _settings = settings;
        _clips = clips;
        _gate = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrent, 1, 5));
        _budget = new VoiceBudget(Path.Combine(clips.Shared, "elevenlabs-usage.json"), settings.MonthlyCap, settings.KeepInReserve, ReadSubscriptionAsync);
    }

    /// <summary>How long to wait after a refusal for being busy; overridden in tests.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(0.8 * Math.Pow(2.2, attempt));

    public string Name => "ElevenLabs";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_key);

    public VoiceBudget Budget => _budget;

    public async Task<VoiceClip> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!IsAvailable)
        {
            throw new VoiceUnavailableException("No ElevenLabs key is configured.");
        }

        var voiceSettings = new JsonObject
        {
            ["stability"] = _settings.Stability,
            ["similarity_boost"] = _settings.SimilarityBoost,
            ["style"] = _settings.Style,
            ["speed"] = _settings.Speed,
        };

        var hash = ClipHash(text, _settings.VoiceId, _settings.ModelId, voiceSettings);
        string[] names = [hash + ".mp3", hash + ".json"];

        if (_clips.Existing(Kind, names) is { } kept)
        {
            return Load(text, Path.Combine(kept, names[0]), Path.Combine(kept, names[1]), billed: 0);
        }

        await _budget.CheckAsync(text.Length, cancellationToken).ConfigureAwait(false);

        var (bytes, alignmentJson) = await SpeakAsync(text, voiceSettings, cancellationToken).ConfigureAwait(false);

        var folder = _clips.Target(Kind);
        var audio = Path.Combine(folder, names[0]);
        var alignment = Path.Combine(folder, names[1]);
        await File.WriteAllBytesAsync(audio, bytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(alignment, alignmentJson, cancellationToken).ConfigureAwait(false);
        _clips.Share(Kind, folder, names);

        _budget.Spend(text.Length);
        return Load(text, audio, alignment, billed: text.Length);
    }

    /// <summary>
    /// One synthesis: queued behind the others, and tried again when ElevenLabs says it is busy.
    /// </summary>
    /// <remarks>
    /// The limit counts every application using the key, so waiting is often all that is needed
    /// — and waiting a second is better than a presentation changing voice mid-paragraph.
    /// </remarks>
    private async Task<(byte[] Audio, string Alignment)> SpeakAsync(string text, JsonObject voiceSettings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Api}/text-to-speech/{Uri.EscapeDataString(_settings.VoiceId)}/with-timestamps?output_format=mp3_44100_128");
                request.Headers.Add("xi-api-key", _key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = JsonContent.Create(new JsonObject
                {
                    ["text"] = text,
                    ["model_id"] = _settings.ModelId,
                    ["voice_settings"] = voiceSettings.DeepClone(),
                });

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new VoiceUnavailableException("ElevenLabs could not be reached: " + ex.Message, ex);
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        if (attempt + 1 >= Attempts)
                        {
                            throw new VoiceUnavailableException(
                                $"ElevenLabs was busy after {Attempts} tries — a plan allows only a few clips at once, "
                                + "and everything using the key counts. This line is read by the next voice.");
                        }

                        var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new VoiceUnavailableException($"ElevenLabs refused ({(int)response.StatusCode}): {Detail(body)}");
                    }

                    var json = JsonNode.Parse(body) as JsonObject
                        ?? throw new VoiceUnavailableException("ElevenLabs returned something that is not JSON.");

                    var bytes = Convert.FromBase64String(json["audio_base64"]?.GetValue<string>()
                        ?? throw new VoiceUnavailableException("ElevenLabs returned no audio."));

                    return (bytes, (json["alignment"] ?? new JsonObject()).ToJsonString());
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The key shape <c>dev/11labs</c> uses: the JSON of text, voice, model and settings, hashed.</summary>
    public static string ClipHash(string text, string voiceId, string modelId, JsonObject settings)
    {
        var key = new JsonObject
        {
            ["text"] = text,
            ["voiceId"] = voiceId,
            ["modelId"] = modelId,
            ["settings"] = settings.DeepClone(),
        };

        var json = key.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();
    }

    private static VoiceClip Load(string text, string audio, string alignmentFile, int billed)
    {
        var alignment = JsonNode.Parse(File.ReadAllText(alignmentFile)) as JsonObject;
        var characters = alignment?["characters"]?.AsArray().Select(c => c?.GetValue<string>() ?? "").ToList() ?? [];
        var starts = alignment?["character_start_times_seconds"]?.AsArray().Select(s => s?.GetValue<double>() ?? 0).ToList() ?? [];
        var ends = alignment?["character_end_times_seconds"]?.AsArray().Select(s => s?.GetValue<double>() ?? 0).ToList() ?? [];

        var levels = Envelope.FromFile(audio);
        var duration = Math.Max(ends.Count > 0 ? ends[^1] + 0.15 : 0, levels.Count * VoiceClip.LevelStep);
        var timing = characters.Count > 0
            ? SpeechTiming.FromCharacters(text, characters, starts, duration)
            : SpeechTiming.Estimate(text, duration);

        return new VoiceClip(audio, timing, "ElevenLabs", billed, levels);
    }

    private async Task<(int Used, int Limit)?> ReadSubscriptionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/user/subscription");
            request.Headers.Add("xi-api-key", _key);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var used = json?["character_count"]?.GetValue<int>();
            var limit = json?["character_limit"]?.GetValue<int>();
            return used is { } u && limit is { } l ? (u, l) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            // Unknown is not a refusal: the local cap still holds.
            return null;
        }
    }

    /// <summary>Enough of a refusal to act on; the rest is in the log.</summary>
    private static string Short(string text) =>
        text.Length <= 160 ? text : text[..160].TrimEnd() + "…";

    private static string Detail(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var detail = json?["detail"]?["message"]?.GetValue<string>() ?? json?["detail"]?.ToJsonString() ?? body;
            return Short(detail);
        }
        catch (JsonException)
        {
            return Short(body);
        }
    }
}
