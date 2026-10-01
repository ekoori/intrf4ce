using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Intrf4ce.App;

/// <summary>
/// Where the window was when it was last closed, and how big — and the rule that it always
/// opens somewhere it can be seen.
/// </summary>
/// <remarks>
/// Centring a fixed size on the primary screen put the title bar above the top of a 4K
/// laptop's screen at 250% scaling, where the window is bigger than the screen is tall, and
/// with no system title bar there was no way to drag it back. Now the window comes back where
/// it was left, clamped into the working area of whichever screen it was on; and the first
/// time, or when that screen has gone, it takes nine tenths of the screen the pointer is on,
/// centred.
/// </remarks>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    private const double FirstWidth = 1480;
    private const double FirstHeight = 920;

    /// <summary>
    /// Room left around the window for its frame, in device-independent pixels a side: the
    /// resize border Windows draws outside the client area, which the screen has to hold too.
    /// </summary>
    private const double Frame = 8;

    public static WindowPlacement? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening in the default place next time is not worth a failure on the way out.
        }
    }

    /// <summary>
    /// Sizes and places <paramref name="window"/> before it is shown: where <paramref name="saved"/>
    /// says, kept on a screen, or centred on the screen the pointer is on when there is nothing
    /// to go on.
    /// </summary>
    public static void Apply(Window window, WindowPlacement? saved)
    {
        ArgumentNullException.ThrowIfNull(window);

        var screens = window.Screens;
        if (screens is null || screens.All.Count == 0)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var screen = saved is null ? null : screens.All.FirstOrDefault(s => Overlaps(s, saved));
        var fresh = screen is null;
        screen ??= Pointer() is { } pointer ? screens.ScreenFromPoint(pointer) : null;
        screen ??= screens.Primary ?? screens.All[0];

        var area = screen.WorkingArea;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var frame = (int)Math.Round(Frame * scale);
        var room = (Width: (area.Width - (2 * frame)) / scale, Height: (area.Height - (2 * frame)) / scale);

        // Never bigger than the screen it is on; the first time, comfortably smaller.
        var width = fresh ? Math.Min(FirstWidth, room.Width * 0.9) : Math.Min(saved!.Width, room.Width);
        var height = fresh ? Math.Min(FirstHeight, room.Height * 0.9) : Math.Min(saved!.Height, room.Height);
        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);

        // The whole window, frame and all, in device pixels.
        var outer = (Width: (int)Math.Round(window.Width * scale) + (2 * frame), Height: (int)Math.Round(window.Height * scale) + (2 * frame));
        var x = fresh ? area.X + ((area.Width - outer.Width) / 2) : saved!.X;
        var y = fresh ? area.Y + ((area.Height - outer.Height) / 2) : saved!.Y;

        // The title bar, above all, must be on the screen: it is the only handle there is.
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - outer.Width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - outer.Height));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>Enough of the saved window on this screen to call it the one it was on.</summary>
    private static bool Overlaps(Screen screen, WindowPlacement saved)
    {
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var rect = new PixelRect(saved.X, saved.Y, (int)(saved.Width * scale), (int)(saved.Height * scale));
        var shared = screen.WorkingArea.Intersect(rect);
        return shared.Width >= 120 && shared.Height >= 80;
    }

    /// <summary>Where the mouse is, where the system will say: the screen the user is at.</summary>
    private static PixelPoint? Pointer() =>
        OperatingSystem.IsWindows() && GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
