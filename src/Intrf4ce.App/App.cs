using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Intrf4ce.App.Render;
using Intrf4ce.App.SelfTest;

namespace Intrf4ce.App;

/// <summary>
/// The Avalonia application, assembled entirely in code.
/// </summary>
/// <remarks>
/// No <c>App.axaml</c>, as in Fjord: Avalonia's generated XAML partial classes do not satisfy
/// <c>TreatWarningsAsErrors</c>, and the stage is drawn and composed in code anyway.
/// </remarks>
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            switch (Program.Mode)
            {
                case LaunchMode.RenderSelfTest:
                    StartRenderSelfTest(desktop, Program.ModeArgument!);
                    break;

                default:
                    desktop.MainWindow = new MainWindow();
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartRenderSelfTest(IClassicDesktopStyleApplicationLifetime desktop, string directory)
    {
        var service = new WebRenderService(Theme.Default.Renderer);
        var window = RenderSelfTest.CreateHostWindow(service);
        var test = new RenderSelfTest(directory, service);

        window.Opened += async (_, _) =>
        {
            try
            {
                await test.RunAsync();
                Program.ExitCode = test.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Program.ExitCode = 3;
            }

            desktop.Shutdown(Program.ExitCode);
        };

        desktop.MainWindow = window;
    }
}
