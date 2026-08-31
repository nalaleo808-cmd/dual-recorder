using System;
using DualRecorder.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DualRecorder.Audio
{
    /// <summary>
    /// Wraps one WASAPI capture (microphone or speaker loopback), resamples whatever the
    /// device gives us to the common recording format, and appends it to a SourceTrack.
    /// Mic and loopback almost always run at different sample rates, which is why the
    /// resampler sits here rather than at mixdown time.
    /// </summary>
    public sealed class CaptureSource : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly SourceTrack _track;
        private readonly BufferedWaveProvider _incoming;
        private readonly ISampleProvider _chain;
        private readonly object _readGate = new object();
        private float[] _read = new float[1 << 15];
        private volatile bool _accepting;
        private bool _disposed;

        /// <summary>Raised when the device dies mid recording (unplugged, driver reset).</summary>
        public event Action<CaptureSource, string> Faulted;

        public string Label { get; }
        public WaveFormat DeviceFormat { get; }
        public SourceTrack Track => _track;

        public CaptureSource(string label, IWaveIn capture, SourceTrack track, int targetRate, int targetChannels)
        {
            Label = label;
            _capture = capture;
            _track = track;

            // WASAPI usually reports a WaveFormatExtensible; the byte layout is identical
            // to the plain format, and the plain one is what the sample converters accept.
            var fmt = capture.WaveFormat;
            if (fmt is WaveFormatExtensible ext)
            {
                try { fmt = ext.ToStandardWaveFormat(); } catch { }
            }
            DeviceFormat = fmt;

            _incoming = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            ISampleProvider sp = _incoming.ToSampleProvider();
            if (sp.WaveFormat.Channels != targetChannels)
                sp = new ChannelAdapterSampleProvider(sp, targetChannels);
            if (sp.WaveFormat.SampleRate != targetRate)
                sp = new WdlResamplingSampleProvider(sp, targetRate);
            _chain = sp;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public void Start()
        {
            _accepting = true;
            _capture.StartRecording();
        }

        /// <summary>Pause just stops accepting; the device keeps running so resume is instant.</summary>
        public void SetAccepting(bool value)
        {
            _accepting = value;
            if (!value) { try { _incoming.ClearBuffer(); } catch { } }
        }

        public void Stop()
        {
            _accepting = false;
            try { _capture.StopRecording(); } catch { }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_accepting || e.BytesRecorded <= 0 || _disposed) return;
            try
            {
                lock (_readGate)
                {
                    _incoming.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    // drain the resampler; the guard stops a misbehaving provider spinning
                    for (int guard = 0; guard < 128; guard++)
                    {
                        int n = _chain.Read(_read, 0, _read.Length);
                        if (n <= 0) break;
                        if (_accepting) _track.Append(_read, n);
                        if (n < _read.Length) break;
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseFault(ex.Message);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e?.Exception != null) RaiseFault(e.Exception.Message);
        }

        private void RaiseFault(string message)
        {
            if (_track.Faulted) return;
            _accepting = false;
            _track.MarkFaulted(message);
            var h = Faulted;
            if (h != null) { try { h(this, message); } catch { } }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _accepting = false;
            try { _capture.DataAvailable -= OnDataAvailable; } catch { }
            try { _capture.RecordingStopped -= OnRecordingStopped; } catch { }
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
        }
    }
}
