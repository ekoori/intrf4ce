using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Media.Tts;

/// <summary>
/// Keeps ElevenLabs spending inside what the user allowed.
/// </summary>
/// <remarks>
/// Two limits, both checked before a paid synthesis: intrf4ce's own monthly allowance from
/// settings (the account is shared with other projects), counted locally; and the account's
/// real remaining characters, read from the subscription endpoint, less a reserve. An agent
/// can write a lot of narration very quickly, so the check happens before every call, and a
/// refusal falls back to the free system voice rather than failing the slide.
/// </remarks>
public sealed class VoiceBudget
{
    private readonly string _file;
    private readonly int _monthlyCap;
    private readonly int _reserve;
    private readonly Func<CancellationToken, Task<(int Used, int Limit)?>> _subscription;
    private readonly object _gate = new();
    private (int Used, int Limit, DateTime At)? _account;

    public VoiceBudget(string file, int monthlyCap, int reserve, Func<CancellationToken, Task<(int Used, int Limit)?>> subscription)
    {
        _file = file;
        _monthlyCap = monthlyCap;
        _reserve = reserve;
        _subscription = subscription;
    }

    /// <summary>Characters intrf4ce has spent this calendar month.</summary>
    public int UsedThisMonth
    {
        get
        {
            lock (_gate)
            {
                var (month, used) = Read();
                return month == CurrentMonth() ? used : 0;
            }
        }
    }

    public int MonthlyCap => _monthlyCap;

    /// <summary>What the account had left when last asked, if known.</summary>
    public int? AccountRemaining
    {
        get
        {
            lock (_gate)
            {
                return _account is { } a ? a.Limit - a.Used : null;
            }
        }
    }

    /// <summary>Checks that <paramref name="characters"/> may be spent; throws with the reason if not.</summary>
    /// <exception cref="VoiceUnavailableException">Over the cap or the reserve.</exception>
    public async Task CheckAsync(int characters, CancellationToken cancellationToken)
    {
        var used = UsedThisMonth;
        if (used + characters > _monthlyCap)
        {
            throw new VoiceUnavailableException(
                $"intrf4ce's ElevenLabs allowance for this month is spent ({used:N0} of {_monthlyCap:N0} characters); using the system voice. Raise elevenlabs.monthly_cap in settings.yaml to allow more.");
        }

        (int Used, int Limit, DateTime At)? account;
        lock (_gate)
        {
            account = _account;
        }

        // The account figure is cached for a few minutes; re-reading it before every clip
        // would double the number of requests for no benefit.
        if (account is null || DateTime.UtcNow - account.Value.At > TimeSpan.FromMinutes(5))
        {
            var fresh = await _subscription(cancellationToken).ConfigureAwait(false);
            if (fresh is { } f)
            {
                account = (f.Used, f.Limit, DateTime.UtcNow);
                lock (_gate)
                {
                    _account = account;
                }
            }
        }

        if (account is { } known && known.Limit - known.Used - characters < _reserve)
        {
            throw new VoiceUnavailableException(
                $"The ElevenLabs account has {known.Limit - known.Used:N0} characters left, under the {_reserve:N0} kept in reserve; using the system voice.");
        }
    }

    /// <summary>Records a paid synthesis.</summary>
    public void Spend(int characters)
    {
        lock (_gate)
        {
            var (month, used) = Read();
            var current = CurrentMonth();
            Write(current, (month == current ? used : 0) + characters);

            if (_account is { } a)
            {
                _account = (a.Used + characters, a.Limit, a.At);
            }
        }
    }

    private static string CurrentMonth() => DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private (string Month, int Used) Read()
    {
        try
        {
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject obj)
            {
                return (obj["month"]?.GetValue<string>() ?? "", obj["characters"]?.GetValue<int>() ?? 0);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
        }

        return ("", 0);
    }

    private void Write(string month, int used)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, new JsonObject { ["month"] = month, ["characters"] = used }.ToJsonString());
        }
        catch (IOException)
        {
        }
    }
}
