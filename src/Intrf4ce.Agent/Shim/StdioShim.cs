using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Intrf4ce.Agent.Mcp;

namespace Intrf4ce.Agent.Shim;

/// <summary>
/// <c>intrf4ce --mcp</c>: connects an agent's stdio to the running window's pipe.
/// </summary>
/// <remarks>
/// It knows nothing about MCP. It announces what kind of session this is, then copies bytes
/// both ways until either side closes, so every session's state lives in the window and a
/// crashed shim loses nothing. If no window is running it starts one and waits for the pipe,
/// which is what makes <c>claude mcp add intrf4ce -- intrf4ce --mcp</c> all the setup needed.
/// </remarks>
public static class StdioShim
{
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(20);

    /// <param name="kind">hosted, attached or channel.</param>
    /// <param name="executable">This program, to start the window with if it is not running.</param>
    public static async Task<int> RunAsync(string kind, string executable)
    {
        var pipeName = PipeName.ForCurrentUser();
        var pipe = await TryConnectAsync(pipeName, TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);

        if (pipe is null)
        {
            Log($"no intrf4ce window running; starting {executable}");
            Launch(executable);
            pipe = await TryConnectAsync(pipeName, LaunchWait).ConfigureAwait(false);
        }

        if (pipe is null)
        {
            Log($"intrf4ce did not start listening within {LaunchWait.TotalSeconds:0} s");
            return 2;
        }

        await using (pipe)
        {
            var hello = Encoding.UTF8.GetBytes(new Hello(kind).ToLine() + "\n");
            await pipe.WriteAsync(hello).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);

            await using var stdin = Console.OpenStandardInput();
            await using var stdout = Console.OpenStandardOutput();

            var up = PumpAsync(stdin, pipe);
            var down = PumpAsync(pipe, stdout);
            var finished = await Task.WhenAny(up, down).ConfigureAwait(false);

            // stdin closing is the agent ending the session normally; the pipe closing means the window went away.
            if (finished == down)
            {
                Log("the intrf4ce window closed the connection");
                return 1;
            }

            return 0;
        }
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                await pipe.ConnectAsync((int)Math.Clamp(remaining.TotalMilliseconds, 50, 1000)).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Starts the window without letting it inherit this process's stdio.
    /// </summary>
    /// <remarks>
    /// An agent reads our stdout until it closes. A window that inherited it would hold it
    /// open after the shim exits, and the agent would wait forever for a session that ended.
    /// On Windows ShellExecute passes no handles; elsewhere the child gets fresh pipes that
    /// are closed immediately.
    /// </remarks>
    private static void Launch(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };

        if (OperatingSystem.IsWindows())
        {
            start.UseShellExecute = true;
            Process.Start(start)?.Dispose();
            return;
        }

        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var process = Process.Start(start);
        if (process is not null)
        {
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
    }

    private static async Task PumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // One side closed; the caller decides what that means.
        }
    }

    /// <summary>stderr, which MCP clients keep in their logs; stdout belongs to the protocol.</summary>
    private static void Log(string message)
    {
        try
        {
            Console.Error.WriteLine("[intrf4ce --mcp] " + message);
        }
        catch (IOException)
        {
        }
    }
}
