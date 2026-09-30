using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Intrf4ce.Core;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Tts;

namespace Intrf4ce.Tests.Media;

/// <summary>
/// What is spoken, what is paid for, and what is never sent at all: a changed voice does not
/// re-record a presentation, a muted one asks for nothing, and tags reach only a voice that
/// understands them.
/// </summary>
public sealed class VoiceChoicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Changing the voice after a slide was spoken keeps its recording: new words get the new
    /// voice, words already spoken in this presentation are not paid for again.
    /// </summary>
    [Fact]
    public async Task AChangedVoiceDoesNotReRecordWhatWasAlreadySaid()
    {
        var session = Folder("session");
        var clips = new VoiceCache(Folder("cache")) { Session = () => session };
        var settings = new ElevenLabsSettings { VoiceId = "first-voice", ModelId = "eleven_multilingual_v2" };
        var sent = new List<string>();

        using var http = new HttpClient(new Speaker(sent));
        var engine = new ElevenLabsEngine(http, "key", settings, clips);

        var first = await engine.SynthesizeAsync("The river runs north.", CancellationToken.None);
        Assert.Equal(21, first.Billed);

        settings.VoiceId = "second-voice";
        settings.ModelId = "eleven_v3";

        var again = await engine.SynthesizeAsync("The river runs north.", CancellationToken.None);
        Assert.Equal(0, again.Billed);
        Assert.Single(sent);

        // New words are new: they get the voice the user has now.
        await engine.SynthesizeAsync("It floods every summer.", CancellationToken.None);
        Assert.Equal(2, sent.Count);
    }

    /// <summary>The words match with or without their tags, and the timing follows the new text.</summary>
    [Fact]
    public async Task NarrationGivenTagsLaterStillUsesItsRecording()
    {
        var session = Folder("session");
        var clips = new VoiceCache(Folder("cache")) { Session = () => session };
        var settings = new ElevenLabsSettings { ModelId = "eleven_v3" };
        var sent = new List<string>();

        using var http = new HttpClient(new Speaker(sent));
        var engine = new ElevenLabsEngine(http, "key", settings, clips);

        await engine.SynthesizeAsync("Is this the river?", CancellationToken.None);
        var tagged = await engine.SynthesizeAsync("[curious] Is this the river?", CancellationToken.None);

        Assert.Single(sent);
        Assert.Equal("[curious] Is this the river?", tagged.Timing.Text);
    }

    [Fact]
    public async Task RevoicingASlideAsksForItAgain()
    {
        var session = Folder("session");
        var clips = new VoiceCache(Folder("cache")) { Session = () => session };
        var settings = new ElevenLabsSettings { VoiceId = "first-voice" };
        var sent = new List<string>();

        using var http = new HttpClient(new Speaker(sent));
        var engine = new ElevenLabsEngine(http, "key", settings, clips);
        var voices = new VoiceService([engine, new SilentEngine(16)], engine, clips);

        await voices.PrepareAsync("The river runs north.", CancellationToken.None);
        settings.VoiceId = "second-voice";

        Assert.Equal(1, voices.Forget("The river runs north."));
        await voices.PrepareAsync("The river runs north.", CancellationToken.None);

        Assert.Equal(2, sent.Count);
    }

    /// <summary>An older model would read "[curious]" aloud, so it is given the words alone.</summary>
    [Fact]
    public async Task OnlyAVoiceThatUnderstandsTagsIsGivenThem()
    {
        var clips = new VoiceCache(Folder("cache")) { Session = () => Folder("session") };
        var sent = new List<string>();

        using var http = new HttpClient(new Speaker(sent));
        var older = new ElevenLabsEngine(http, "key", new ElevenLabsSettings { ModelId = "eleven_multilingual_v2" }, clips);
        var voices = new VoiceService([older, new SilentEngine(16)], older, clips);

        var clip = await voices.PrepareAsync("[curious] Is this the river?", CancellationToken.None);

        Assert.Equal("Is this the river?", Assert.Single(sent));
        Assert.Equal("[curious] Is this the river?", clip.Timing.Text);

        var v3 = new ElevenLabsEngine(http, "key", new ElevenLabsSettings { ModelId = "eleven_v3" }, clips);
        var expressive = new VoiceService([v3, new SilentEngine(16)], v3, clips);
        await expressive.PrepareAsync("[excited] It is!", CancellationToken.None);

        Assert.Equal("[excited] It is!", sent[^1]);
    }

    /// <summary>Muted costs nothing: no voice is asked, and the subtitles still have their timing.</summary>
    [Fact]
    public void ASilentClipAsksNoVoiceForAnything()
    {
        var clips = new VoiceCache(Folder("cache"));
        var sent = new List<string>();

        using var http = new HttpClient(new Speaker(sent));
        var engine = new ElevenLabsEngine(http, "key", new ElevenLabsSettings(), clips);
        var voices = new VoiceService([engine, new SilentEngine(16)], engine, clips);

        var clip = voices.Silent("[curious] Is this the river? It is.");

        Assert.Empty(sent);
        Assert.Null(clip.AudioPath);
        Assert.True(clip.Duration > 1);
    }

    [Fact]
    public void TheKeyInTheSettingsFileIsTheOneUsed()
    {
        var paths = AppPaths.Under(Folder("home"));
        var settings = new ElevenLabsSettings { ApiKey = "  from-the-file  ", KeyFile = Path.Combine(_root, "missing.env") };

        Assert.Equal("from-the-file", SettingsFile.ElevenLabsKey(paths, settings));
    }

    /// <summary>ElevenLabs answering every request with speech and an alignment of what it was sent.</summary>
    private sealed class Speaker(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The budget asks what the account has left before anything is spoken.
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"character_count":0,"character_limit":100000}"""),
                };
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var text = body["text"]!.GetValue<string>();
            sent.Add(text);

            var characters = new JsonArray();
            var starts = new JsonArray();
            var ends = new JsonArray();
            for (var i = 0; i < text.Length; i++)
            {
                characters.Add(text[i].ToString());
                starts.Add(i * 0.05);
                ends.Add((i + 1) * 0.05);
            }

            var json = new JsonObject
            {
                ["audio_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("not really an mp3")),
                ["alignment"] = new JsonObject
                {
                    ["characters"] = characters,
                    ["character_start_times_seconds"] = starts,
                    ["character_end_times_seconds"] = ends,
                },
            };

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString()) };
        }
    }
}
