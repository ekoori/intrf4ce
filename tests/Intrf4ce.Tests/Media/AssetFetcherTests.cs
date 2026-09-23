using System.Net;
using Intrf4ce.Media.Images;

namespace Intrf4ce.Tests.Media;

/// <summary>
/// What a host says about a download, turned into something the agent can act on.
/// </summary>
public class AssetFetcherTests
{
    private static readonly byte[] Png =
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public async Task AHostAskingForASlowerPaceIsWaitedOut()
    {
        var replies = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.TooManyRequests),
            Image(),
        ]);

        using var http = new HttpClient(new Replies(replies));
        var fetcher = new AssetFetcher(http) { Backoff = _ => TimeSpan.Zero };

        var (bytes, extension) = await fetcher.DownloadAsync("https://upload.wikimedia.org/a.png", CancellationToken.None);

        Assert.Equal(".png", extension);
        Assert.Equal(Png, bytes);
        Assert.Empty(replies);
    }

    [Fact]
    public async Task ASiteThatRefusesApplicationsSaysToLookElsewhere()
    {
        var replies = new Queue<HttpResponseMessage>([new HttpResponseMessage(HttpStatusCode.Forbidden)]);
        using var http = new HttpClient(new Replies(replies));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => new AssetFetcher(http).DownloadAsync("https://static.wikia.nocookie.net/oddbods/images/a.png", CancellationToken.None));

        Assert.Contains("static.wikia.nocookie.net", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Wikimedia Commons", ex.Message, StringComparison.Ordinal);

        // One refusal is enough: trying again would only be slower.
        Assert.Empty(replies);
    }

    [Fact]
    public async Task AMissingFileSaysToLinkTheImageNotThePage()
    {
        using var http = new HttpClient(new Replies(new Queue<HttpResponseMessage>([new HttpResponseMessage(HttpStatusCode.NotFound)])));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => new AssetFetcher(http).DownloadAsync("https://example.org/page-about-a-brain", CancellationToken.None));

        Assert.Contains("not the page", ex.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class Replies(Queue<HttpResponseMessage> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(replies.Count > 0 ? replies.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
