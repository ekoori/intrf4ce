using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Rendering;

/// <summary>
/// Turns the chart shorthand into a Vega-Lite spec.
/// </summary>
/// <remarks>
/// Most slides need a bar, line or pie chart, and asking the agent for Vega-Lite every time
/// costs tokens and invites mistakes in the parts that never vary. The shorthand names the
/// kind and the fields; the field types are inferred from the data, so a column of dates
/// gets a time axis without the agent saying so.
/// </remarks>
public static class ChartSpec
{
    public static string ToVegaLite(ChartElement chart, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(chart);

        if (chart.VegaLite is { } full)
        {
            var spec = JsonNode.Parse(full.GetRawText())!.AsObject();

            // The stage decides the size; a spec that fixes its own would fight the layout.
            spec.Remove("width");
            spec.Remove("height");
            spec["width"] = Round(width);
            spec["height"] = Round(height);
            return spec.ToJsonString();
        }

        var data = chart.Data ?? throw new ArgumentException("chart has no data");
        var x = chart.X!;
        var y = chart.Y!;
        var xType = FieldType(data, x);

        var encoding = new JsonObject();
        JsonNode mark;

        switch (chart.Kind)
        {
            case "pie" or "donut":
                var radius = Math.Min(width, height) / 2;
                mark = chart.Kind == "donut"
                    ? new JsonObject { ["type"] = "arc", ["innerRadius"] = Round(radius * 0.52), ["padAngle"] = 0.01 }
                    : new JsonObject { ["type"] = "arc" };
                encoding["theta"] = new JsonObject { ["field"] = y, ["type"] = "quantitative", ["stack"] = true };
                encoding["color"] = new JsonObject { ["field"] = x, ["type"] = "nominal", ["title"] = null, ["sort"] = null };
                encoding["order"] = new JsonObject { ["field"] = y, ["type"] = "quantitative", ["sort"] = "descending" };
                break;

            case "scatter":
                mark = new JsonObject { ["type"] = "point", ["filled"] = true, ["size"] = 90 };
                encoding["x"] = Axis(x, FieldType(data, x) == "nominal" ? "nominal" : "quantitative");
                encoding["y"] = Axis(y, "quantitative");
                break;

            case "line" or "area":
                mark = chart.Kind == "line"
                    ? new JsonObject { ["type"] = "line", ["point"] = true, ["interpolate"] = "monotone" }
                    : new JsonObject { ["type"] = "area", ["opacity"] = 0.85, ["interpolate"] = "monotone", ["line"] = true };
                encoding["x"] = Axis(x, xType == "nominal" ? "ordinal" : xType);
                encoding["y"] = Axis(y, "quantitative");
                break;

            default:
                mark = new JsonObject { ["type"] = "bar" };
                encoding["x"] = Axis(x, xType == "quantitative" ? "ordinal" : xType);
                encoding["y"] = Axis(y, "quantitative");
                if (chart.Series is not null)
                {
                    // Grouped side by side: stacked bars make every series but the first hard to compare.
                    encoding["xOffset"] = new JsonObject { ["field"] = chart.Series, ["type"] = "nominal" };
                }

                break;
        }

        if (chart.Series is not null && chart.Kind is not ("pie" or "donut"))
        {
            encoding["color"] = new JsonObject { ["field"] = chart.Series, ["type"] = "nominal", ["title"] = null };
        }

        var result = new JsonObject
        {
            ["$schema"] = "https://vega.github.io/schema/vega-lite/v6.json",
            ["width"] = Round(width),
            ["height"] = Round(height),
            ["data"] = new JsonObject { ["values"] = JsonNode.Parse(data.GetRawText()) },
            ["mark"] = mark,
            ["encoding"] = encoding,
        };

        return result.ToJsonString();
    }

    private static JsonObject Axis(string field, string type)
    {
        var axis = new JsonObject { ["field"] = field, ["type"] = type, ["title"] = Humanize(field) };
        if (type is "nominal" or "ordinal")
        {
            axis["sort"] = null;
            axis["axis"] = new JsonObject { ["labelAngle"] = 0 };
        }

        return axis;
    }

    /// <summary>quantitative when every value is a number, temporal when every value parses as a date, else nominal.</summary>
    internal static string FieldType(JsonElement data, string field)
    {
        var numbers = 0;
        var dates = 0;
        var total = 0;

        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            total++;
            if (value.ValueKind == JsonValueKind.Number)
            {
                numbers++;
            }
            else if (value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: >= 7 } text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && text.Any(char.IsDigit))
            {
                dates++;
            }
        }

        if (total > 0 && numbers == total)
        {
            return "quantitative";
        }

        return total > 0 && dates == total ? "temporal" : "nominal";
    }

    private static string Humanize(string field) =>
        field.Replace('_', ' ').Trim() is { Length: > 0 } text ? char.ToUpperInvariant(text[0]) + text[1..] : field;

    private static double Round(double value) => Math.Round(Math.Max(40, value));
}
