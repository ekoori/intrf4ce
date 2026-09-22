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

    /// <summary>The file for <paramref name="source"/>, downloading it into the deck's assets folder if needed.</summary>
    /// <returns>The absolute path, and for downloads the path relative to the deck folder to record.</returns>
    public async Task<(string Path, string? Relative)> ResolveAsync(
        string source, string? recorded, string deckFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

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

            return File.Exists(path)
                ? (Path.GetFullPath(path), null)
                : throw new FileNotFoundException($"no file at {path}");
        }

        if (recorded is not null)
        {
            var existing = Path.GetFullPath(Path.Combine(deckFolder, recorded));
            if (File.Exists(existing))
            {
                return (existing, recorded);
            }
        }

        var (bytes, extension) = await DownloadAsync(source, cancellationToken).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12].ToLowerInvariant();
        var name = Slug.From(Path.GetFileNameWithoutExtension(new Uri(source).AbsolutePath), "image", 40);
        var relative = Path.Combine("assets", $"{hash}-{name}{extension}");
        var target = Path.Combine(deckFolder, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        return (target, relative.Replace('\\', '/'));
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
