using System.ComponentModel;
using System.Diagnostics;

namespace Intrf4ce.App;

/// <summary>Hands a file to whatever the system opens that kind of file with.</summary>
public static class Shell
{
    /// <summary>ERROR_NO_ASSOCIATION: nothing on this Windows opens files with that extension.</summary>
    private const int NoAssociation = 1155;

    /// <summary>Opens <paramref name="path"/> in its default application.</summary>
    /// <returns>Why it could not be opened, or null.</returns>
    public static string? Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);
    }

    /// <summary>
    /// Opens a text file to be edited: in whatever the user opens that kind of file with, or the
    /// system's own text editor when nothing claims it — a fresh Windows has no program for .yaml,
    /// and .NET asks the shell not to put up its "Open with" dialog.
    /// </summary>
    /// <returns>Why it could not be opened, or null.</returns>
    public static string? Edit(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsMacOS())
        {
            // -t: the default text editor, whatever the extension.
            return Start(new ProcessStartInfo("open") { ArgumentList = { "-t", path } }, path);
        }

        try
        {
            using var opening = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex) when (OperatingSystem.IsWindows() && ex.NativeErrorCode == NoAssociation)
        {
            return Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path } }, path);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return $"could not open {path}: {ex.Message}";
        }
    }

    private static string? Start(ProcessStartInfo start, string path)
    {
        try
        {
            using var started = Process.Start(start);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return $"could not open {path}: {ex.Message}";
        }
    }
}
