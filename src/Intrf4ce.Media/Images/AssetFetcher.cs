using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Media.Images;

/// <summary>
/// Turns an image source the agent named into a file on disk.
/// </summary>
/// <remarks>
/// Local paths are used where they are, so a gallery of a thousand holiday photos is not
/// copied into every deck. URLs are downloaded once into the deck's assets folder and
/// recorded, so a saved deck still shows its pictures offline and years later.
/// </remarks>
public sealed class AssetFetcher(HttpClient http)
{
    /// <summary>Anything bigger is not a slide image.</summary>
    public const long MaxBytes = 40L * 1024 * 1024;

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

        if (recorded is not null)
        {
            var existing = Path.GetFullPath(Path.Combine(deckFolder, recorded));
            if (File.Exists(existing))
            {
                return (existing, recorded);
            }
        }

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
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"no file at {path}");
            }

            // Already inside the session: leave it where it is.
            if (path.StartsWith(Path.GetFullPath(deckFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return (path, null);
            }

            var copy = Beside(deckFolder, source, Path.GetExtension(path));
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Target)!);
            File.Copy(path, copy.Target, overwrite: true);
            return (copy.Target, copy.Relative);
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
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {url}");
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

        return (bytes, extension);
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
