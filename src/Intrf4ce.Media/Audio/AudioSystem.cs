using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Intrf4ce.Media.Audio;

/// <summary>
/// Plays one clip at a time and says where it is, on every platform, through miniaudio.
/// </summary>
/// <remarks>
/// Everything is decoded to one format, 44.1 kHz mono float, by asking the decoder to convert.
/// SoundFlow does not resample between a clip and the device on its own: a 22 kHz clip on a
/// 48 kHz device plays at double speed. ElevenLabs' MP3s are already 44.1 kHz mono, so for the
/// usual voice the conversion is free.
/// <para>
/// The device is opened lazily and failure is tolerated: a machine with no audio output
/// still gets subtitles timed as if the voice were playing.
/// </para>
/// </remarks>
public sealed class AudioSystem : IDisposable
{
    public static readonly AudioFormat Format = new() { SampleRate = 44100, Channels = 1, Format = SampleFormat.F32 };

    private readonly object _gate = new();
    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private SoundPlayer? _player;
    private StreamDataProvider? _provider;
    private Stream? _stream;
    private bool _failed;
    private float _volume = 1;
    private int _generation;

    /// <summary>The clip playing finished on its own (not stopped). Raised on the audio thread.</summary>
    public event EventHandler<int>? Ended;

    /// <summary>Why the device could not be opened, if it could not.</summary>
    public string? Error { get; private set; }

    public bool IsAvailable => EnsureDevice();

    /// <summary>Seconds into the playing clip, or 0.</summary>
    public double Position
    {
        get
        {
            lock (_gate)
            {
                return _player?.Time ?? 0;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _player?.State == PlaybackState.Playing;
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_gate)
            {
                if (_player is not null)
                {
                    _player.Volume = _volume;
                }
            }
        }
    }

    /// <summary>Starts <paramref name="path"/> from the beginning, stopping whatever was playing.</summary>
    /// <returns>A generation number that <see cref="Ended"/> reports, so a late end of an old clip is not mistaken for this one.</returns>
    public int Play(string path)
    {
        if (!EnsureDevice())
        {
            throw new InvalidOperationException(Error ?? "no audio device");
        }

        lock (_gate)
        {
            StopLocked();

            var generation = ++_generation;
            _stream = File.OpenRead(path);
            _provider = new StreamDataProvider(_engine!, Format, _stream);
            _player = new SoundPlayer(_engine!, Format, _provider) { Volume = _volume };
            _player.PlaybackEnded += (_, _) => Ended?.Invoke(this, generation);
            _device!.MasterMixer.AddComponent(_player);
            _player.Play();
            return generation;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _player?.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_player?.State == PlaybackState.Paused)
            {
                _player.Play();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    /// <summary>Decodes a whole file to mono samples at <see cref="Format"/>'s rate, for analysis.</summary>
    public float[] Decode(string path)
    {
        var engine = EnsureEngine();
        using var stream = File.OpenRead(path);
        using var provider = new StreamDataProvider(engine, Format, stream);

        var samples = new List<float>(Math.Max(1024, provider.Length));
        var buffer = new float[16384];
        int read;
        while ((read = provider.ReadBytes(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return [.. samples];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
            _device?.Stop();
            _device?.Dispose();
            _device = null;
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void StopLocked()
    {
        if (_player is not null)
        {
            _player.Stop();
            _device?.MasterMixer.RemoveComponent(_player);
            _player.Dispose();
            _player = null;
        }

        _provider?.Dispose();
        _provider = null;
        _stream?.Dispose();
        _stream = null;
    }

    private MiniAudioEngine EnsureEngine()
    {
        lock (_gate)
        {
            return _engine ??= new MiniAudioEngine();
        }
    }

    private bool EnsureDevice()
    {
        lock (_gate)
        {
            if (_device is not null)
            {
                return true;
            }

            if (_failed)
            {
                return false;
            }

            try
            {
                _engine ??= new MiniAudioEngine();
                _device = _engine.InitializePlaybackDevice(null, Format);
                _device.Start();
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Error = "No audio output: " + ex.Message;
                return false;
            }
        }
    }
}
