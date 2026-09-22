namespace Intrf4ce.Core.Stage;

/// <summary>An image prepared for the agent to look at.</summary>
/// <param name="Png">The image, scaled down, with a labelled 0..1 grid when asked for.</param>
/// <param name="Summary">Size, format, date, camera and GPS in a line or two.</param>
public sealed record InspectedImage(byte[] Png, int Width, int Height, string Summary);

/// <summary>One image found in a folder, with what its metadata says.</summary>
public sealed record MediaItem(
    string Path,
    int Width,
    int Height,
    DateTimeOffset? Taken,
    double? Latitude,
    double? Longitude,
    string? Camera,
    long Bytes);

/// <summary>Image work the tools do outside the stage.</summary>
public interface IMediaTools
{
    /// <exception cref="FileNotFoundException">The file or URL could not be read.</exception>
    Task<InspectedImage> InspectAsync(string source, bool grid, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> ScanAsync(string folder, bool recursive, int limit, CancellationToken cancellationToken);
}
