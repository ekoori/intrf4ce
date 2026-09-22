using System.Globalization;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.Media.Images;

/// <summary><c>inspect_image</c> and <c>scan_media</c>.</summary>
public sealed class MediaTools(AssetFetcher fetcher, string scratchFolder) : IMediaTools
{
    /// <summary>Big enough to read detail, small enough to be cheap for the agent to look at.</summary>
    private const int InspectSide = 1280;

    public async Task<InspectedImage> InspectAsync(string source, bool grid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        string path;
        if (AssetFetcher.IsUrl(source))
        {
            // Inspected images are not part of any deck until the agent puts them on a slide.
            var (resolved, _) = await fetcher.ResolveAsync(source, null, scratchFolder, cancellationToken).ConfigureAwait(false);
            path = resolved;
        }
        else
        {
            path = Environment.ExpandEnvironmentVariables(source);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"no file at {path}");
            }
        }

        var facts = PhotoMetadata.Read(path);
        using var bitmap = await Task.Run(() => ImageDecoder.Decode(path, InspectSide), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not an image this platform can decode");

        using var marked = grid ? CoordinateGrid.Draw(bitmap) : bitmap.Copy();
        var png = ImageDecoder.EncodePng(marked);

        var width = facts.Width > 0 ? facts.Width : bitmap.Width;
        var height = facts.Height > 0 ? facts.Height : bitmap.Height;
        var summary = $"{Path.GetFileName(path)}: {width}×{height}"
            + (bitmap.Width != width ? string.Create(CultureInfo.InvariantCulture, $" (shown at {bitmap.Width}×{bitmap.Height})") : "")
            + (facts.Summary() is { Length: > 0 } details && details != $"{width}×{height}" ? " · " + details : "")
            + ".";

        return new InspectedImage(png, width, height, summary);
    }

    public Task<IReadOnlyList<MediaItem>> ScanAsync(string folder, bool recursive, int limit, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MediaItem>>(() =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            var items = new List<MediaItem>();
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ImageDecoder.IsImage(file))
                {
                    continue;
                }

                var facts = PhotoMetadata.Read(file);
                items.Add(new MediaItem(
                    file,
                    facts.Width,
                    facts.Height,
                    facts.Taken,
                    facts.Latitude,
                    facts.Longitude,
                    facts.Camera,
                    new FileInfo(file).Length));

                if (items.Count >= limit)
                {
                    break;
                }
            }

            // Chronological when the photos know their date; by name otherwise.
            return [.. items.OrderBy(i => i.Taken ?? DateTimeOffset.MaxValue).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)];
        }, cancellationToken);
}
