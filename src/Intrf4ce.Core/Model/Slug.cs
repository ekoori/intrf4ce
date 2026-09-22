using System.Globalization;
using System.Text;

namespace Intrf4ce.Core.Model;

/// <summary>Turns titles into ids and folder names: lower case, ASCII, hyphenated.</summary>
public static class Slug
{
    public static string From(string? text, string fallback, int maxLength = 48)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        // Decompose so "Ćevapi" becomes "cevapi" rather than "evapi".
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var pendingHyphen = false;

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}
