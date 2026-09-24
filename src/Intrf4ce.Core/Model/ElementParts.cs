using System.Text.Json;

namespace Intrf4ce.Core.Model;

/// <summary>
/// Names for parts that come from content rather than from ids the agent wrote: a table's
/// rows, a gallery's photos.
/// </summary>
/// <remarks>
/// Kept in one place so that the validator, which checks cues before anything is drawn, and
/// the views that draw the parts agree on every name. Names are slugs because a cue target
/// cannot hold spaces: the row "Knee joint" is <c>knee-joint</c>.
/// </remarks>
public static class ElementParts
{
    /// <summary>One id per row, from the key column (the first unless named), made unique.</summary>
    public static IReadOnlyList<string> TableRows(TableElement table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var key = table.Key is { } name ? IndexOf(table.Columns, name) : 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(table.Rows.Count);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var text = key >= 0 && key < row.Count ? CellText(row[key]) : null;
            ids.Add(Unique(Slug.From(text, $"row{r + 1}"), used));
        }

        return ids;
    }

    /// <summary>Ids for the items the agent listed: its own where given, item1, item2… otherwise.</summary>
    public static IReadOnlyList<string> GalleryItems(IReadOnlyList<GalleryItem> items, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(used);

        // The agent's own ids first, so a generated one never takes a name it chose.
        foreach (var item in items)
        {
            if (item.Id is { Length: > 0 } id)
            {
                used.Add(id);
            }
        }

        return [.. items.Select((item, i) => item.Id is { Length: > 0 } id ? id : Unique($"item{i + 1}", used))];
    }

    /// <summary>Ids for the places on a map: the agent's own, or slugs of their labels.</summary>
    public static IReadOnlyList<string> Places(IReadOnlyList<Place> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in places)
        {
            if (place.Id is { Length: > 0 } id)
            {
                used.Add(id);
            }
        }

        return [.. places.Select((place, i) => place.Id is { Length: > 0 } id ? id : Unique(Slug.From(place.Label, $"place{i + 1}"), used))];
    }

    /// <summary>An id for a photo found in a folder, from its file name ("IMG_2031.jpg" is img-2031).</summary>
    public static string Photo(string fileName, ISet<string> used) =>
        Unique(Slug.From(Path.GetFileNameWithoutExtension(fileName), "photo"), used);

    /// <summary><paramref name="id"/>, or id-2, id-3… if it is taken; records the one returned.</summary>
    public static string Unique(string id, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var candidate = id;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    /// <summary>A cell as plain text: strings as they are, numbers as written, null as null.</summary>
    public static string? CellText(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString(),
        JsonValueKind.Number => cell.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
