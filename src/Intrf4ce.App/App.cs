using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Intrf4ce.App.Render;
using Intrf4ce.App.SelfTest;
using Intrf4ce.Core;
using Intrf4ce.Core.Settings;

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
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

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
                    desktop.MainWindow = CreateMainWindow();
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Raised for anything that escaped onto the UI thread, for the window to show.</summary>
    public static event Action<Exception>? UiThreadError;

    private static MainWindow CreateMainWindow()
    {
        var paths = AppPaths.Default;
        Log.Initialise(paths.Logs);
        CatchWhatEscapes();

        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Log.Write($"intrf4ce {version} starting on {Environment.OSVersion}, .NET {Environment.Version}");
        Log.Write($"config {paths.Config}, cache {paths.Cache}, library {paths.Library}");

        try
        {
            SettingsFile.WriteDefaultIfMissing(paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("could not write default settings", ex);
        }

        var load = SettingsFile.Load(paths.SettingsFile);
        if (load.Error is not null)
        {
            Log.Write("settings error, using defaults: " + load.Error);
        }

        foreach (var warning in load.Warnings ?? [])
        {
            Log.Write("settings: " + warning);
        }

        var problem = load.Error ?? (load.Warnings is { Count: > 0 } warnings ? string.Join("\n", warnings) : null);
        return new MainWindow(paths, load.Settings, problem);
    }

    /// <summary>A crash that leaves nothing behind is the worst kind of bug report.</summary>
    /// <summary>
    /// Keeps the window up when something throws where nobody was catching.
    /// </summary>
    /// <remarks>
    /// The app is driven by an agent while someone watches a presentation: a mistake in one
    /// handler has to end as a message, not as a window disappearing mid-sentence. Everything
    /// is logged with its stack, and what reached the UI thread is shown to the user.
    /// </remarks>
    private static void CatchWhatEscapes()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("unhandled on the UI thread", e.Exception);
            UiThreadError?.Invoke(e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("FATAL unhandled exception: " + e.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("a task failed and nobody was watching", e.Exception);
            e.SetObserved();
        };
    }

    private static void StartRenderSelfTest(IClassicDesktopStyleApplicationLifetime desktop, string directory)
    {
        var service = new WebRenderService(Skin.Default.Renderer);
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
