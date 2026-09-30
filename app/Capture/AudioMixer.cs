using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IdmClone;

/// <summary>
/// Listens to "what you hear" (the speakers) and/or a microphone, mixes them and writes one steady stream of
/// 48 kHz stereo 16-bit sound to a stream, exactly as fast as the clock runs (sample number n is the sound of n / 48000
/// seconds after Start). Windows sends nothing at all while the speakers are silent, so every source keeps a timeline
/// in which such gaps are filled with silence: the sound then always lines up with the picture.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int Rate = 48000, Channels = 2;

    /// <summary>One source of sound (speakers or microphone) turned into 48 kHz stereo floats.</summary>
    private sealed class Input : IDisposable
    {
        public readonly WasapiCapture Capture;
        public readonly BufferedWaveProvider Buffer;
        public readonly ISampleProvider Samples;
        public float Gain = 1f;

        private readonly Stopwatch _clock = new();
        private long _framesAdded;                       // device frames in Buffer so far (real sound plus the silence we filled in)

        public Input(WasapiCapture capture)
        {
            Capture = capture;
            Buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(30), DiscardOnBufferOverflow = true, ReadFully = false,
            };
            ISampleProvider s = Buffer.ToSampleProvider();
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            else if (s.WaveFormat.Channels > 2)
            {
                var two = new MultiplexingSampleProvider(new[] { s }, 2);
                two.ConnectInputToOutput(0, 0);
                two.ConnectInputToOutput(1, 1);
                s = two;
            }
            if (s.WaveFormat.SampleRate != Rate) s = new WdlResamplingSampleProvider(s, Rate);
            Samples = s;
            capture.DataAvailable += OnData;
        }

        public void Begin()
        {
            _clock.Restart();
            _framesAdded = 0;
            Capture.StartRecording();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            var format = Buffer.WaveFormat;
            int frames = e.BytesRecorded / format.BlockAlign;
            // This sound ends now. If less than that much has arrived so far, the missing time was silence.
            long gap = (long)(_clock.Elapsed.TotalSeconds * format.SampleRate) - _framesAdded - frames;
            if (gap > format.SampleRate / 50)
            {
                var silence = new byte[Math.Min(gap, format.SampleRate * 5L) * format.BlockAlign];
                Buffer.AddSamples(silence, 0, silence.Length);
                _framesAdded += silence.Length / format.BlockAlign;
            }
            Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _framesAdded += frames;
        }

        /// <summary>Throw away the oldest sound when more than <paramref name="keep"/> is waiting (our clock and the device's differ a little).</summary>
        public void TrimBacklog(TimeSpan keep)
        {
            var format = Buffer.WaveFormat;
            long keepBytes = (long)(keep.TotalSeconds * format.AverageBytesPerSecond);
            long excess = Buffer.BufferedBytes - keepBytes;
            excess -= excess % format.BlockAlign;
            if (excess <= 0) return;
            var scratch = new byte[Math.Min(excess, 1 << 20)];
            while (excess > 0)
            {
                int n = Buffer.Read(scratch, 0, (int)Math.Min(excess, scratch.Length));
                if (n <= 0) break;
                excess -= n;
            }
        }

        public void Dispose()
        {
            try { Capture.StopRecording(); } catch (Exception) { }
            try { Capture.Dispose(); } catch (Exception) { }
        }
    }

    private readonly List<Input> _inputs = new();
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Set (with a readable reason) if a source could not be opened: the recording then goes on without it.</summary>
    public string? Problem { get; private set; }

    /// <summary>The microphones Windows knows about (id, name).</summary>
    public static List<(string Id, string Name)> Microphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <param name="system">Record what plays on the default speakers/headphones.</param>
    /// <param name="microphone">null = no microphone, "" = the default one, otherwise a device id from <see cref="Microphones"/>.</param>
    public AudioMixer(bool system, string? microphone)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (system)
        {
            try { _inputs.Add(new Input(new WasapiLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)))); }
            catch (Exception e) { Problem = "Couldn't record the system sound (" + e.Message + ")."; }
        }
        if (microphone != null)
        {
            try
            {
                var device = microphone.Length == 0
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : enumerator.GetDevice(microphone);
                _inputs.Add(new Input(new WasapiCapture(device)));
            }
            catch (Exception e) { Problem = (Problem != null ? Problem + " " : "") + "Couldn't use the microphone (" + e.Message + ")."; }
        }
    }

    public bool HasInput => _inputs.Count > 0;

    /// <summary>Start listening and writing to <paramref name="sink"/> in the background. Stops by itself if the sink closes.</summary>
    public void Start(Stream sink)
    {
        foreach (var input in _inputs)
        {
            try { input.Begin(); }
            catch (Exception e) { Problem = "Couldn't start a sound source (" + e.Message + ")."; }
        }
        _thread = new Thread(() => Pump(sink)) { IsBackground = true, Name = "Utylix audio mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Pump(Stream sink)
    {
        var clock = Stopwatch.StartNew();
        long written = 0;                                            // frames (one frame = left + right sample)
        var mix = new float[Rate * Channels];
        var part = new float[Rate * Channels];
        var bytes = new byte[Rate * Channels * 2];
        try
        {
            while (!_stop)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * Rate) - written;
                if (due < Rate / 100) { Thread.Sleep(4); continue; }            // send in pieces of 10 ms or more
                int frames = (int)Math.Min(due, Rate);                          // never more than a second at once
                int count = frames * Channels;
                Array.Clear(mix, 0, count);
                foreach (var input in _inputs)
                {
                    // ffmpeg may have been busy for a while (it is at the start): then a lot is due at once and all of it is real sound, in order
                    input.TrimBacklog(TimeSpan.FromSeconds(frames / (double)Rate) + TimeSpan.FromMilliseconds(120));
                    int got = input.Samples.Read(part, 0, count);
                    for (int i = 0; i < got; i++) mix[i] += part[i] * input.Gain;
                }
                for (int i = 0; i < count; i++)
                {
                    float v = Math.Clamp(mix[i], -1f, 1f);
                    short s = (short)Math.Round(v * 32767f);
                    bytes[i * 2] = (byte)(s & 0xFF);
                    bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                sink.Write(bytes, 0, count * 2);
                sink.Flush();
                written += frames;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { /* the reader went away: nothing more to do */ }
    }

    /// <summary>Stop listening. (Does not close the sink.)</summary>
    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        foreach (var input in _inputs) input.Dispose();
        _inputs.Clear();
    }

    public void Dispose() => Stop();
}
