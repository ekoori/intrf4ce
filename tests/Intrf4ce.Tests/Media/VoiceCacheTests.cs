using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Tts;

namespace Intrf4ce.Tests.Media;

/// <summary>
/// Narration is kept with the presentation that speaks it, and a voice that says it is busy is
/// waited out rather than handed to the next voice down.
/// </summary>
public class VoiceCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

    [Fact]
    public void AClipGoesToTheSessionAndIsKeptInTheSharedCacheToo()
    {
        var shared = Folder("cache");
        var session = Folder("session");
        var clips = new VoiceCache(shared) { Session = () => session };

        var target = clips.Target("elevenlabs");
        File.WriteAllText(Path.Combine(target, "abc.mp3"), "audio");
        clips.Share("elevenlabs", target, "abc.mp3");

        Assert.Equal(Path.Combine(session, "elevenlabs"), target);
        Assert.True(File.Exists(Path.Combine(shared, "elevenlabs", "abc.mp3")));
        Assert.Equal(target, clips.Existing("elevenlabs", "abc.mp3"));
    }

    [Fact]
    public void AClipAnotherPresentationPaidForIsCopiedIntoThisOne()
    {
        var shared = Folder("cache");
        var session = Folder("session");
        Directory.CreateDirectory(Path.Combine(shared, "elevenlabs"));
        File.WriteAllText(Path.Combine(shared, "elevenlabs", "abc.mp3"), "audio");
        File.WriteAllText(Path.Combine(shared, "elevenlabs", "abc.json"), "{}");

        var clips = new VoiceCache(shared) { Session = () => session };
        var found = clips.Existing("elevenlabs", "abc.mp3", "abc.json");

        Assert.Equal(Path.Combine(session, "elevenlabs"), found);
        Assert.True(File.Exists(Path.Combine(session, "elevenlabs", "abc.mp3")));
        Assert.True(File.Exists(Path.Combine(session, "elevenlabs", "abc.json")));
    }

    [Fact]
    public void WithNoSessionOpenClipsFallBackToTheSharedCache()
    {
        var shared = Folder("cache");
        var clips = new VoiceCache(shared) { Session = () => null };

        Assert.Equal(Path.Combine(shared, "system"), clips.Target("system"));
        Assert.Null(clips.Existing("system", "nothing.wav"));
    }

    [Fact]
    public async Task ElevenLabsWaitsOutBeingBusyRatherThanGivingUpTheLine()
    {
        var shared = Folder("cache");
        var session = Folder("session");
        var replies = new Queue<HttpResponseMessage>(
        [
            Busy(),
            Busy(),
            Speech(),
        ]);

        using var http = new HttpClient(new Replies(replies));
        var engine = new ElevenLabsEngine(http, "key", new ElevenLabsSettings(), new VoiceCache(shared) { Session = () => session })
        {
            Backoff = _ => TimeSpan.Zero,
        };

        var clip = await engine.SynthesizeAsync("Hello there.", CancellationToken.None);

        Assert.Equal("ElevenLabs", clip.Engine);
        Assert.Empty(replies);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(session, "elevenlabs")));
    }

    [Fact]
    public async Task AVoiceThatStaysBusySaysSoInWordsAndStandsAside()
    {
        var replies = new Queue<HttpResponseMessage>([Busy(), Busy(), Busy(), Busy(), Busy()]);
        using var http = new HttpClient(new Replies(replies));
        var engine = new ElevenLabsEngine(http, "key", new ElevenLabsSettings(), new VoiceCache(Folder("cache")))
        {
            Backoff = _ => TimeSpan.Zero,
        };

        var ex = await Assert.ThrowsAsync<VoiceUnavailableException>(
            () => engine.SynthesizeAsync("Hello there.", CancellationToken.None));

        Assert.Contains("busy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next voice", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The wall of JSON the API sends is not what the user is told.
        Assert.DoesNotContain("detail", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Busy() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent("""{"detail":{"message":"Too many concurrent requests. Your current subscription is associated with a maximum of 3 concurrent requests (running in parallel). This is done such that a single user does not overwhelm our systems and affect other users negatively. Please upgrade your subscription or contact sales if you want to increase this limit."}}"""),
    };

    private static HttpResponseMessage Speech()
    {
        var json = new JsonObject
        {
            ["audio_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("not really an mp3")),
            ["alignment"] = new JsonObject { ["characters"] = new JsonArray(), ["character_start_times_seconds"] = new JsonArray() },
        };

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString()) };
    }

    private sealed class Replies(Queue<HttpResponseMessage> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(replies.Count > 0 ? replies.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("out of replies") });
    }
}
