using System;

namespace DualRecorder.Core
{
    /// <summary>
    /// One recorded source (mic or system audio): its own WAV file, a queue feeding the
    /// mixdown, a peak meter, and the silence padding that keeps it on the wall clock.
    /// All public members are safe to call from the capture callback and the pump thread.
    /// </summary>
    public sealed class SourceTrack : IDisposable
    {
        private readonly object _gate = new object();
        private readonly WavWriterSafe _wav;
        private readonly FloatFifo _mixFeed;
        private readonly int _channels;
        private float _peak;
        private bool _disposed;

        public string Name { get; }
        public long FramesWritten { get; private set; }
        public long PaddedFrames { get; private set; }
        public bool Faulted { get; private set; }
        public string FaultMessage { get; private set; }

        public SourceTrack(string name, string wavPath, int sampleRate, int channels, int fifoSeconds = 30)
        {
            Name = name;
            _channels = channels;
            _wav = new WavWriterSafe(wavPath, sampleRate, channels);
            int cap = sampleRate * channels * Math.Max(2, fifoSeconds);
            _mixFeed = new FloatFifo(sampleRate * channels, cap);
        }

        public string WavPath => _wav.Path;

        /// <summary>Append real captured audio (interleaved floats, -1..1).</summary>
        public void Append(float[] interleaved, int count)
        {
            if (count <= 0) return;
            lock (_gate)
            {
                if (_disposed) return;
                _wav.WriteSamples(interleaved, 0, count);
                _mixFeed.Write(interleaved, 0, count);
                FramesWritten += count / _channels;

                float p = _peak;
                for (int i = 0; i < count; i++)
                {
                    float a = interleaved[i];
                    if (a < 0) a = -a;
                    if (a > p) p = a;
                }
                _peak = p;
            }
        }

        /// <summary>
        /// Pad with silence up to targetFrames. This is the fix for WASAPI loopback going
        /// completely quiet (no callbacks at all) and for a device that has been unplugged.
        /// </summary>
        public long PadTo(long targetFrames)
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                long need = targetFrames - FramesWritten;
                if (need <= 0) return 0;

                _wav.WriteSilenceFrames(need);

                // feed the same silence to the mixdown so both tracks stay frame aligned
                var zeros = new float[Math.Min(need, 4096) * _channels];
                long left = need;
                while (left > 0)
                {
                    int frames = (int)Math.Min(left, 4096);
                    _mixFeed.Write(zeros, 0, frames * _channels);
                    left -= frames;
                }

                FramesWritten += need;
                PaddedFrames += need;
                return need;
            }
        }

        public int ReadMix(float[] dst, int offset, int count)
        {
            lock (_gate) { return _mixFeed.Read(dst, offset, count); }
        }

        public int MixAvailable { get { lock (_gate) { return _mixFeed.Count; } } }

        /// <summary>Peak since the last call, then reset. Drives the level meter.</summary>
        public float TakePeak()
        {
            lock (_gate) { float p = _peak; _peak = 0f; return p; }
        }

        public void MarkFaulted(string message)
        {
            lock (_gate) { Faulted = true; FaultMessage = message; }
        }

        public void FlushHeader()
        {
            lock (_gate) { if (!_disposed) _wav.UpdateHeader(); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _wav.Dispose();
            }
        }
    }
}
