using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Rendering;
using SkiaSharp;

namespace Intrf4ce.App.SelfTest;

/// <summary>
/// <c>intrf4ce --selftest render &lt;dir&gt;</c>: renders a fixed set of diagrams, formulas and
/// charts through the real hidden web view and writes each as SVG and as a native PNG.
/// </summary>
/// <remarks>
/// The renderer cannot be unit tested: it needs a real web engine, a window, and a message
/// loop. This mode exercises exactly the code the stage uses, and the PNGs are how Svg.Skia's
/// fidelity against the browser's own drawing gets judged.
/// </remarks>
public sealed class RenderSelfTest
{
    private static readonly (string Name, RenderKind Kind, string Source, RenderOptions Options)[] Samples =
    [
        ("flowchart", RenderKind.Mermaid, """
            flowchart LR
              A[Sensor] --> B{Over threshold?}
              B -- yes --> C[Raise alarm]
              B -- no --> D[Log reading]
              subgraph Control room
                C --> E((Operator))
              end
            """, RenderOptions.Default),
        ("sequence", RenderKind.Mermaid, """
            sequenceDiagram
              participant U as User
              participant A as Agent
              participant S as Stage
              U->>A: Explain the knee
              A->>S: put_slide(ligaments)
              S-->>U: Narration with focus cues
              Note over A,S: slides keep arriving
            """, RenderOptions.Default),
        ("state", RenderKind.Mermaid, """
            stateDiagram-v2
              [*] --> Idle
              Idle --> Building: user asks
              Building --> Presenting: first slide ready
              Presenting --> Building: more to add
              Presenting --> [*]
            """, RenderOptions.Default),
        ("class", RenderKind.Mermaid, """
            classDiagram
              class Deck { +string Title +List~Slide~ Slides }
              class Slide { +string Id +string Narration }
              class Element { <<abstract>> +string Id }
              Deck "1" --> "*" Slide
              Slide "1" --> "*" Element
            """, RenderOptions.Default),
        ("pie", RenderKind.Mermaid, """
            pie title Where the time goes
              "Research" : 42
              "Drafting" : 30
              "Narration" : 18
              "Review" : 10
            """, RenderOptions.Default),
        ("mindmap", RenderKind.Mermaid, """
            mindmap
              root((Phone))
                Radio
                  Modem
                  Antennas
                Compute
                  SoC
                  RAM
                Power
                  Battery
                  PMIC
            """, RenderOptions.Default),
        ("block", RenderKind.Mermaid, """
            block-beta
              columns 1
              apps["Apps"]
              framework["Application framework"]
              runtime["Runtime and native libraries"]
              hal["Hardware abstraction layer"]
              kernel["Linux kernel"]
            """, RenderOptions.Default),
        ("broken", RenderKind.Mermaid, "flowchart LR\n  A --> \n  ((( B", RenderOptions.Default),
        ("gaussian", RenderKind.Tex, @"\int_{-\infty}^{\infty} e^{-x^2}\,dx = \sqrt{\pi}", new RenderOptions(FontSize: 32)),
        ("maxwell", RenderKind.Tex, @"\begin{aligned} \nabla \cdot \mathbf{E} &= \frac{\rho}{\varepsilon_0} \\ \nabla \times \mathbf{B} &= \mu_0 \mathbf{J} + \mu_0 \varepsilon_0 \frac{\partial \mathbf{E}}{\partial t} \end{aligned}", new RenderOptions(FontSize: 32)),
        ("badtex", RenderKind.Tex, @"\frac{1}{\nosuchmacro}", new RenderOptions(FontSize: 32)),
        ("bars", RenderKind.VegaLite, """
            {"data":{"values":[{"k":"Mon","v":12},{"k":"Tue","v":19},{"k":"Wed","v":7},{"k":"Thu","v":22},{"k":"Fri","v":15}]},
             "mark":"bar","encoding":{"x":{"field":"k","type":"nominal","title":null,"sort":null},"y":{"field":"v","type":"quantitative","title":"Minutes"}}}
            """, new RenderOptions(Width: 640, Height: 400)),
        ("donut", RenderKind.VegaLite, """
            {"data":{"values":[{"k":"Apps","v":45},{"k":"System","v":25},{"k":"Media","v":20},{"k":"Free","v":10}]},
             "mark":{"type":"arc","innerRadius":80},"encoding":{"theta":{"field":"v","type":"quantitative"},"color":{"field":"k","type":"nominal","title":null}}}
            """, new RenderOptions(Width: 480, Height: 400)),
    ];

    private readonly string _outputDirectory;
    private readonly WebRenderService _service;

    public RenderSelfTest(string outputDirectory, WebRenderService service)
    {
        _outputDirectory = outputDirectory;
        _service = service;
    }

    public int ExitCode { get; private set; } = 1;

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_outputDirectory);
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();

        var ready = await _service.Ready;
        report.AppendLine($"ready={ready} after {clock.ElapsedMilliseconds} ms, available={_service.IsAvailable}");
        if (!ready)
        {
            report.AppendLine("start error: " + _service.StartError);
            Finish(report);
            return;
        }

        var failures = 0;
        foreach (var (name, kind, source, options) in Samples)
        {
            clock.Restart();
            var result = await _service.RenderAsync(kind, source, options with { KeepOriginal = true });
            var elapsed = clock.ElapsedMilliseconds;
            var expectFailure = name is "broken" or "badtex";

            if (!result.Ok)
            {
                report.AppendLine($"{name,-10} FAILED in {elapsed} ms: {result.Error}");
                if (!expectFailure)
                {
                    failures++;
                }

                continue;
            }

            await File.WriteAllTextAsync(Path.Combine(_outputDirectory, name + ".svg"), result.Svg);
            if (result.Original is { } original)
            {
                await File.WriteAllTextAsync(Path.Combine(_outputDirectory, name + ".original.svg"), original);
            }

            using var picture = SvgPicture.Load(result.Svg!, result.Width, result.Height);
            if (picture is null)
            {
                report.AppendLine($"{name,-10} rendered but Svg.Skia could not load it");
                failures++;
                continue;
            }

            await File.WriteAllBytesAsync(
                Path.Combine(_outputDirectory, name + ".png"),
                picture.ToPng(2, new SKColor(0x12, 0x18, 0x20)));

            var parts = string.Join(", ", result.Parts.Select(p =>
                $"{p.Id}@{p.Box.X:0},{p.Box.Y:0} {p.Box.Width:0}x{p.Box.Height:0}"));

            report.AppendLine(
                $"{name,-10} ok in {elapsed,4} ms  {result.Width:0}x{result.Height:0}  svg {result.Svg!.Length / 1024} KB"
                + (result.Warning is null ? "" : "  WARNING " + result.Warning)
                + (parts.Length > 0 ? "\n           parts: " + parts : ""));

            if (expectFailure)
            {
                report.AppendLine($"{name,-10} should have failed");
                failures++;
            }
        }

        clock.Restart();
        var again = await _service.RenderAsync(Samples[0].Kind, Samples[0].Source, Samples[0].Options);
        report.AppendLine($"cache hit: {clock.ElapsedMilliseconds} ms, ok={again.Ok}");

        ExitCode = failures == 0 ? 0 : 2;
        report.AppendLine($"failures={failures}");
        Finish(report);
    }

    private void Finish(StringBuilder report)
    {
        var text = report.ToString();
        File.WriteAllText(Path.Combine(_outputDirectory, "report.txt"), text);
        Console.WriteLine(text);
    }

    /// <summary>A window that exists only to give the web view a place in a visual tree.</summary>
    public static Window CreateHostWindow(WebRenderService service) => new()
    {
        Title = "intrf4ce render self-test",
        Width = 320,
        Height = 120,
        ShowInTaskbar = false,
        Content = new Panel { Children = { new TextBlock { Text = "rendering…", Margin = new(12) }, service.View } },
    };
}
