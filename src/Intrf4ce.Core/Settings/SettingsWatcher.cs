namespace Intrf4ce.Core.Settings;

/// <summary>
/// Watches settings.yaml and reloads it while the app runs.
/// </summary>
/// <remarks>
/// Editors save in their own way — write and rename, or several writes in a row — so changes
/// are debounced and the read is retried: the first look often finds the file still locked.
/// </remarks>
public sealed class SettingsWatcher : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher _watcher;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;

    public SettingsWatcher(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        _path = path;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    /// <summary>The file was saved and read again. Raised on a thread pool thread.</summary>
    public event EventHandler<SettingsLoad>? Reloaded;

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = ReloadAsync(token);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var load = SettingsFile.Load(_path);
                if (load.Error is null || attempt == 3)
                {
                    Reloaded?.Invoke(this, load);
                    return;
                }

                // Probably still being written; give the editor a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
