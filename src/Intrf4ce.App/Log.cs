using System.Globalization;
using System.Text;

namespace Intrf4ce.App;

/// <summary>
/// A plain append-only log file per day.
/// </summary>
/// <remarks>
/// Deliberately not Microsoft.Extensions.Logging: a WinExe has no console, and the one thing
/// needed is a file to read after something went wrong. Writes are serialised and flushed
/// line by line, so a crash leaves everything up to its last line on disk.
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _folder;

    public static void Initialise(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);

            // Keep a fortnight.
            foreach (var old in Directory.EnumerateFiles(folder, "intrf4ce-*.log")
                         .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string? CurrentFile =>
        _folder is null ? null : Path.Combine(_folder, $"intrf4ce-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(string message)
    {
        var file = CurrentFile;
        if (file is null)
        {
            return;
        }

        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {message}{Environment.NewLine}");
        lock (Gate)
        {
            try
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Error(string what, Exception ex) => Write($"ERROR {what}: {ex}");
}
