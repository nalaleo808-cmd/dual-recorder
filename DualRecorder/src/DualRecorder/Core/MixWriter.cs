using System;

namespace DualRecorder.Core
{
    /// <summary>
    /// Sums the two source tracks into mixed.wav. It only ever consumes the number of
    /// frames both sources have available, so the mix can never slide out of alignment.
    /// </summary>
    public sealed class MixWriter : IDisposable
    {
        private readonly WavWriterSafe _wav;
        private readonly SourceTrack _a;
        private readonly SourceTrack _b;
        private readonly int _channels;
        private float[] _bufA = new float[0];
        private float[] _bufB = new float[0];
        private bool _disposed;

        public float GainA { get; set; } = 1.0f;
        public float GainB { get; set; } = 1.0f;

        public MixWriter(string path, int sampleRate, int channels, SourceTrack a, SourceTrack b)
        {
            _wav = new WavWriterSafe(path, sampleRate, channels);
            _a = a; _b = b; _channels = channels;
        }

        public string WavPath => _wav.Path;
        public long FramesWritten => _wav.FramesWritten;

        /// <summary>Drain whatever both sources have ready. Returns frames written.</summary>
        public long Pump()
        {
            if (_disposed) return 0;
            long total = 0;
            while (true)
            {
                int avail = Math.Min(_a.MixAvailable, _b.MixAvailable);
                if (avail < _channels) break;

                int take = Math.Min(avail - (avail % _channels), 1 << 16);
                if (_bufA.Length < take) { _bufA = new float[take]; _bufB = new float[take]; }

                int na = _a.ReadMix(_bufA, 0, take);
                int nb = _b.ReadMix(_bufB, 0, take);
                int n = Math.Min(na, nb);
                if (n <= 0) break;

                for (int i = 0; i < n; i++)
                {
                    float s = _bufA[i] * GainA + _bufB[i] * GainB;
                    if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                    _bufA[i] = s;
                }

                _wav.WriteSamples(_bufA, 0, n);
                total += n / _channels;
            }
            return total;
        }

        public void FlushHeader() { if (!_disposed) _wav.UpdateHeader(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wav.Dispose();
        }
    }
}
