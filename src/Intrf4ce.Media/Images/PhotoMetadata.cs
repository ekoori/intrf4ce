using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;

namespace Intrf4ce.Media.Images;

/// <summary>What a photo's metadata says, as far as intrf4ce cares.</summary>
public sealed record PhotoFacts(
    int Width,
    int Height,
    DateTimeOffset? Taken,
    double? Latitude,
    double? Longitude,
    string? Camera,
    string? Lens,
    string? Exposure)
{
    /// <summary>One line for a hover card or the agent: "12 Mar 2024 14:02 · iPhone 15 · 45.81, 15.98".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Taken is { } taken)
        {
            parts.Add(taken.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        }

        if (Camera is { Length: > 0 })
        {
            parts.Add(Camera);
        }

        if (Exposure is { Length: > 0 })
        {
            parts.Add(Exposure);
        }

        if (Latitude is { } lat && Longitude is { } lon)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{lat:0.####}, {lon:0.####}"));
        }

        if (Width > 0)
        {
            parts.Add($"{Width}×{Height}");
        }

        return string.Join(" · ", parts);
    }
}

public static class PhotoMetadata
{
    public static PhotoFacts Read(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or UnauthorizedAccessException)
        {
            // SVG, and anything else MetadataExtractor does not read: the decoder knows the size.
            var size = ImageDecoder.Probe(path);
            return new PhotoFacts(size?.Width ?? 0, size?.Height ?? 0, null, null, null, null, null, null);
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        var (width, height) = Dimensions(directories, sub);
        if (width == 0 && ImageDecoder.Probe(path) is { } probed)
        {
            // No size in the metadata the reader understood: ask the decoder, which also knows it upright.
            return new PhotoFacts(probed.Width, probed.Height, null, null, null, null, null, null);
        }

        var orientation = ifd0?.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) == true ? o : 1;
        if (orientation is >= 5 and <= 8)
        {
            (width, height) = (height, width);
        }

        DateTimeOffset? taken = null;
        if (sub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original) == true)
        {
            taken = new DateTimeOffset(DateTime.SpecifyKind(original, DateTimeKind.Unspecified), TimeSpan.Zero);
        }
        else if (ifd0?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified) == true)
        {
            taken = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        double? latitude = null, longitude = null;
        if (gps?.TryGetGeoLocation(out var location) == true && !location.IsZero)
        {
            latitude = location.Latitude;
            longitude = location.Longitude;
        }

        var make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
        var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
        var camera = model is { Length: > 0 }
            ? (make is { Length: > 0 } && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model)
            : make;

        var lens = sub?.GetDescription(ExifDirectoryBase.TagLensModel)?.Trim();
        var exposure = Exposure(sub);

        return new PhotoFacts(width, height, taken, latitude, longitude, camera, lens, exposure);
    }

    private static (int Width, int Height) Dimensions(IReadOnlyList<MetadataExtractor.Directory> directories, ExifSubIfdDirectory? sub)
    {
        if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg
            && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh))
        {
            return (jw, jh);
        }

        if (directories.OfType<PngDirectory>().FirstOrDefault() is { } png
            && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
        {
            return (pw, ph);
        }

        if (directories.OfType<WebPDirectory>().FirstOrDefault() is { } webp
            && webp.TryGetInt32(WebPDirectory.TagImageWidth, out var ww) && webp.TryGetInt32(WebPDirectory.TagImageHeight, out var wh))
        {
            return (ww, wh);
        }

        if (sub is not null
            && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
        {
            return (ew, eh);
        }

        return (0, 0);
    }

    private static string? Exposure(ExifSubIfdDirectory? sub)
    {
        if (sub is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (sub.GetDescription(ExifDirectoryBase.TagFNumber) is { Length: > 0 } f)
        {
            parts.Add(f);
        }

        if (sub.GetDescription(ExifDirectoryBase.TagExposureTime) is { Length: > 0 } t)
        {
            parts.Add(t);
        }

        if (sub.GetDescription(ExifDirectoryBase.TagIsoEquivalent) is { Length: > 0 } iso)
        {
            parts.Add("ISO " + iso);
        }

        if (sub.GetDescription(ExifDirectoryBase.TagFocalLength) is { Length: > 0 } focal)
        {
            parts.Add(focal);
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
