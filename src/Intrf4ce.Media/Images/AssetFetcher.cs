using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Media.Images;

/// <summary>
/// Turns an image source the agent named into a file on disk.
/// </summary>
/// <remarks>
/// Everything a slide shows is brought into the session's folder: a URL is downloaded once and
/// recorded, so a saved presentation still shows its pictures offline and years later.
/// <para>
/// A file already on this machine is pointed at where it lies, because a folder of photographs
/// belongs to the user and a presentation about it should not duplicate it; <see cref="CopyMedia"/>
/// brings them in for a session that has to travel.
/// </para>
/// <para>
/// Downloads queue and a host that asks for a slower pace is waited out. Some sites refuse
/// applications outright, and no amount of trying changes that, so a refusal comes back in
/// words the agent can act on — find the picture somewhere else — rather than as a status code.
/// </para>
/// </remarks>
public sealed class AssetFetcher(HttpClient http)
{
    /// <summary>Anything bigger is not a slide image.</summary>
    public const long MaxBytes = 40L * 1024 * 1024;

    private const int Attempts = 3;

    /// <summary>A deck full of pictures must not arrive as a stampede on one host.</summary>
    private static readonly SemaphoreSlim Downloads = new(3);

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        // Wikimedia, the best source of freely licensed diagrams, refuses requests without a
        // descriptive User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("intrf4ce/0.1 (desktop presentation app)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        return client;
    }

    public static bool IsUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file for <paramref name="source"/>, brought into the session's own folder: downloaded
    /// if it is a URL, copied if it is a file somewhere else on the machine.
    /// </summary>
    /// <remarks>
    /// A session is meant to be self-contained — the folder can be copied to another machine and
    /// still present — so the photograph an agent put on a slide cannot stay behind in whatever
    /// folder it came from. The source is kept exactly as the agent wrote it and the copy is
    /// recorded against it, so the same file is never fetched or copied twice.
    /// </remarks>
    /// <returns>The absolute path, and the path relative to the deck folder to record.</returns>
    public async Task<(string Path, string? Relative)> ResolveAsync(
        string source, string? recorded, string deckFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var kept = recorded is { } r ? Path.GetFullPath(Path.Combine(deckFolder, r)) : null;
        var hasKept = kept is not null && File.Exists(kept);

        if (!IsUrl(source))
        {
            var path = Environment.ExpandEnvironmentVariables(source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(source).LocalPath
                : source);

            if (!Path.IsPathRooted(path))
            {
                // Relative paths are relative to the deck, which is how deck-local assets are referenced.
                path = Path.Combine(deckFolder, path);
            }

            path = Path.GetFullPath(path);
            if (File.Exists(path))
            {
                // Already inside the session, or the user would rather it stayed where it is.
                if (!CopyMedia
                    || path.StartsWith(Path.GetFullPath(deckFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return (path, null);
                }

                if (hasKept)
                {
                    return (kept!, recorded);
                }

                var copy = Beside(deckFolder, source, Path.GetExtension(path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy.Target)!);
                File.Copy(path, copy.Target, overwrite: true);
                return (copy.Target, copy.Relative);
            }

            // The original has been moved or deleted; a copy the session kept still shows it.
            return hasKept ? (kept!, recorded) : throw new FileNotFoundException($"no file at {path}");
        }

        if (hasKept)
        {
            return (kept!, recorded);
        }

        var (bytes, extension) = await DownloadAsync(source, cancellationToken).ConfigureAwait(false);

        var download = Beside(deckFolder, source, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(download.Target)!);
        await File.WriteAllBytesAsync(download.Target, bytes, cancellationToken).ConfigureAwait(false);
        return (download.Target, download.Relative);
    }

    /// <summary>Where a copy of <paramref name="source"/> goes inside the session, named after it.</summary>
    private static (string Target, string Relative) Beside(string deckFolder, string source, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12].ToLowerInvariant();
        var stem = IsUrl(source) ? new Uri(source).AbsolutePath : source;
        var name = Slug.From(Path.GetFileNameWithoutExtension(stem), "image", 40);
        var relative = Path.Combine("assets", $"{hash}-{name}{extension}");
        return (Path.Combine(deckFolder, relative), relative.Replace('\\', '/'));
    }

    public async Task<(byte[] Bytes, string Extension)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        await Downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Downloads.Release();
        }
    }

    /// <summary>
    /// Copy images from elsewhere on this machine into the session instead of referencing them.
    /// </summary>
    public bool CopyMedia { get; set; }

    /// <summary>How long to wait after a host asks for a slower pace; overridden in tests.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(1.5 * (attempt + 1));

    private async Task<(byte[] Bytes, string Extension)> GetAsync(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                break;
            }

            var busy = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (busy && attempt + 1 < Attempts)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(Refusal(status, url));
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new InvalidDataException($"{url} is {response.Content.Headers.ContentLength / 1024 / 1024} MB; images over {MaxBytes / 1024 / 1024} MB are refused");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw new InvalidDataException($"{url} is larger than {MaxBytes / 1024 / 1024} MB");
            }
        }

        var bytes = buffer.ToArray();
        var extension = ExtensionFor(type, bytes)
            ?? throw new InvalidDataException($"{url} is not an image (it is {(type.Length > 0 ? type : "unknown")}); link to the image file itself, not the page showing it");

        response.Dispose();
        return (bytes, extension);
    }

    /// <summary>What a refusal means, and what to do about it, rather than a number.</summary>
    private static string Refusal(HttpStatusCode status, string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                $"{host} refuses downloads by applications ({(int)status}). Some sites, Fandom and most social networks among them, only serve images to browsers. "
                + "Take the picture from somewhere that allows it — Wikimedia Commons is reliable — rather than trying this one another way.",
            HttpStatusCode.NotFound or HttpStatusCode.Gone =>
                $"there is no file at {url} ({(int)status}); link to the image file itself, not the page showing it",
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                $"{host} is asking for a slower pace ({(int)status}) and was still doing so after {Attempts} tries; leave it a minute, or use another host",
            _ => $"{host} returned {(int)status} for {url}",
        };
    }

    /// <summary>From the content type, or failing that the file's own signature.</summary>
    private static string? ExtensionFor(string mediaType, byte[] bytes)
    {
        var fromType = mediaType switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            _ => null,
        };

        if (fromType is not null)
        {
            return fromType;
        }

        return bytes switch
        {
            [0xFF, 0xD8, 0xFF, ..] => ".jpg",
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0x47, 0x49, 0x46, ..] => ".gif",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
            [0x42, 0x4D, ..] => ".bmp",
            _ when bytes.Length > 5 && Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256)).Contains("<svg", StringComparison.OrdinalIgnoreCase) => ".svg",
            _ => null,
        };
    }
}
