using System;
using System.IO;

namespace DualRecorder.Core
{
    /// <summary>
    /// 16-bit PCM WAV writer that keeps the RIFF header valid while it is still writing.
    /// The header is rewritten roughly once per second and again on Dispose, so a crash,
    /// a power cut or a killed process leaves a file that still opens and plays.
    /// </summary>
    public sealed class WavWriterSafe : IDisposable
    {
        private const int HeaderSize = 44;

        private readonly FileStream _fs;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly long _headerUpdateBytes;

        private long _dataBytes;
        private long _bytesSinceHeaderUpdate;
        private byte[] _scratch = new byte[0];
        private bool _disposed;

        public string Path { get; }
        public int Channels => _channels;
        public int SampleRate => _sampleRate;

        /// <summary>Frames (sample groups across all channels) written so far.</summary>
        public long FramesWritten => _dataBytes / (_channels * 2);

        public WavWriterSafe(string path, int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            Path = path;
            _sampleRate = sampleRate;
            _channels = channels;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.None);
            _fs.Write(BuildHeader(0), 0, HeaderSize);

            // rewrite the header about once per second of audio
            _headerUpdateBytes = (long)sampleRate * channels * 2;
        }

        private byte[] BuildHeader(long dataBytes)
        {
            int blockAlign = _channels * 2;
            int byteRate = _sampleRate * blockAlign;
            var h = new byte[HeaderSize];

            Write(h, 0, "RIFF");
            WriteU32(h, 4, (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
            Write(h, 8, "WAVE");
            Write(h, 12, "fmt ");
            WriteU32(h, 16, 16);            // PCM fmt chunk size
            WriteU16(h, 20, 1);             // PCM
            WriteU16(h, 22, (ushort)_channels);
            WriteU32(h, 24, (uint)_sampleRate);
            WriteU32(h, 28, (uint)byteRate);
            WriteU16(h, 32, (ushort)blockAlign);
            WriteU16(h, 34, 16);            // bits per sample
            Write(h, 36, "data");
            WriteU32(h, 40, (uint)Math.Min(uint.MaxValue, dataBytes));
            return h;
        }

        private static void Write(byte[] b, int o, string ascii)
        {
            for (int i = 0; i < ascii.Length; i++) b[o + i] = (byte)ascii[i];
        }

        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        }

        /// <summary>Write interleaved float samples in the range -1..1.</summary>
        public void WriteSamples(float[] buffer, int offset, int count)
        {
            if (_disposed || count <= 0) return;

            int bytes = count * 2;
            if (_scratch.Length < bytes) _scratch = new byte[bytes];

            int p = 0;
            for (int i = 0; i < count; i++)
            {
                float s = buffer[offset + i];
                if (float.IsNaN(s)) s = 0f;
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;
                short v = (short)(s * 32767f);
                _scratch[p++] = (byte)v;
                _scratch[p++] = (byte)(v >> 8);
            }

            _fs.Write(_scratch, 0, bytes);
            Advance(bytes);
        }

        /// <summary>Append digital silence. This is what keeps a stalled or dead source in sync.</summary>
        public void WriteSilenceFrames(long frames)
        {
            if (_disposed || frames <= 0) return;

            long bytes = frames * _channels * 2;
            var zeros = new byte[Math.Min(bytes, 1 << 16)];
            long left = bytes;
            while (left > 0)
            {
                int chunk = (int)Math.Min(left, zeros.Length);
                _fs.Write(zeros, 0, chunk);
                left -= chunk;
            }
            Advance(bytes);
        }

        private void Advance(long bytes)
        {
            _dataBytes += bytes;
            _bytesSinceHeaderUpdate += bytes;
            if (_bytesSinceHeaderUpdate >= _headerUpdateBytes) UpdateHeader();
        }

        /// <summary>Rewrite the RIFF/data sizes and push everything to disk.</summary>
        public void UpdateHeader()
        {
            if (_disposed) return;
            long pos = _fs.Position;
            _fs.Seek(0, SeekOrigin.Begin);
            _fs.Write(BuildHeader(_dataBytes), 0, HeaderSize);
            _fs.Seek(pos, SeekOrigin.Begin);
            _fs.Flush(true);
            _bytesSinceHeaderUpdate = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { UpdateHeader(); }
            catch { /* never let finalising one file take down the others */ }
            _disposed = true;
            try { _fs.Dispose(); } catch { }
        }
    }
}
