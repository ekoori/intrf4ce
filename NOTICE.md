# Third-party notices

intrf4ce itself is MIT ([LICENSE](LICENSE)). It ships the following libraries, each under its own
licence. Browser libraries that run inside the hidden renderer are listed separately, with where
they came from and how to update them, in [VENDORED.md](VENDORED.md).

## .NET packages

| Package | Version | Licence |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.0.5 | MIT |
| Avalonia.Fonts.Inter (Inter typeface) | 12.0.5 | MIT (font: SIL OFL 1.1) |
| Avalonia.Controls.WebView | 12.1.0 | MIT |
| SkiaSharp | 3.119.4 | MIT |
| Svg.Skia | 5.1.1 | MIT |
| Markdig | 1.4.0 | BSD-2-Clause |
| YamlDotNet | 18.1.0 | MIT |
| MetadataExtractor | 2.9.3 | Apache-2.0 |
| SoundFlow | 1.4.1 | MIT |
| System.Speech | 10.0.12 | MIT |

Test-only: xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector
(all MIT/Apache-2.0), FsCheck.Xunit (BSD-3-Clause), Xunit.SkippableFact (MIT).

Debug-only: AvaloniaUI.DiagnosticsSupport (MIT), excluded from Release builds.

## Native components

SkiaSharp carries Skia (BSD-3-Clause) and HarfBuzz (MIT). SoundFlow carries miniaudio
(public domain / MIT-0). The hidden renderer uses the platform's own web engine — WebView2 on
Windows, WKWebView on macOS, WebKitGTK on Linux — which intrf4ce does not redistribute.

## Content the app fetches

Images an agent puts on a slide are downloaded into the deck's folder and stay the property of
whoever made them. The presenter guide asks agents to prefer images whose licence allows reuse
and to credit them in the caption; it cannot enforce it. Narration synthesised by ElevenLabs is
subject to your own ElevenLabs terms.
