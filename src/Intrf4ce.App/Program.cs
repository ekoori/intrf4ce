using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace Intrf4ce.App;

public static class Program
{
    /// <summary>What <see cref="App"/> should do once Avalonia is up.</summary>
    internal static LaunchMode Mode { get; private set; } = LaunchMode.Normal;

    /// <summary>The argument that goes with <see cref="Mode"/>, such as the self-test output folder.</summary>
    internal static string? ModeArgument { get; private set; }

    /// <summary>Set by the self-tests; what the process exits with.</summary>
    internal static int ExitCode { get; set; }

    // Nothing here may touch Avalonia or anything relying on a SynchronizationContext
    // before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args)
        {
            case ["--selftest", "render", var directory, ..]:
                UseParentConsole();
                Mode = LaunchMode.RenderSelfTest;
                ModeArgument = Path.GetFullPath(directory);
                break;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return ExitCode;
    }

    // Also used by the Avalonia visual designer; do not rename.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Borrows the console of whatever launched this, on Windows.
    /// </summary>
    /// <remarks>
    /// intrf4ce is a WinExe so a double-click does not flash a console, which leaves the
    /// diagnostic switches with nowhere to write when run from a terminal. Attaching to the
    /// parent's console gives them one. The streams are reopened afterwards because
    /// <see cref="Console"/> caches writers bound to the handles that existed on first use.
    /// Same approach as Fjord.
    /// </remarks>
    internal static void UseParentConsole()
    {
        if (OperatingSystem.IsWindows())
        {
            AttachParentConsole();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AttachParentConsole()
    {
        const uint Parent = 0xFFFFFFFF;

        if (!AttachConsole(Parent))
        {
            return;
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch (IOException)
        {
            // The handle was attached and then closed under us. Nothing left to do.
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}

internal enum LaunchMode
{
    Normal,
    RenderSelfTest,
}
