using System;
using System.IO;
using System.Text;

namespace DualRecorder.Transcription
{
    // Tails the recorder's canonical PCM files without touching the capture callbacks.
    public sealed class GrowingWaveReader : IDisposable
    {
        private readonly FileStream _input;
        private long _offset = 44;
        private bool _headerChecked;
        public long SamplesRead { get; private set; }
        public long AvailableSamples => Math.Max(0, (_input.Length - _offset) / 12);

        public GrowingWaveReader(string path)
        {
            _input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public float[] ReadBlock(int maximumSamples = 1600, bool final = false)
        {
            if (!_headerChecked)
            {
                if (_input.Length < 44) return Array.Empty<float>();
                byte[] header = new byte[44];
                _input.Position = 0;
                _input.ReadExactly(header);
                if (Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE" ||
                    Encoding.ASCII.GetString(header, 36, 4) != "data" || BitConverter.ToUInt16(header, 20) != 1 ||
                    BitConverter.ToUInt16(header, 22) != 2 || BitConverter.ToInt32(header, 24) != 48000 ||
                    BitConverter.ToUInt16(header, 34) != 16)
                    throw new InvalidDataException("Live transcription expects DualRecorder's 48 kHz stereo PCM WAV files.");
                _headerChecked = true;
            }
            int frames = (int)Math.Min(maximumSamples * 3L, Math.Max(0, (_input.Length - _offset) / 4));
            if (!final) frames -= frames % 3;
            if (frames == 0) return Array.Empty<float>();
            byte[] bytes = new byte[frames * 4];
            _input.Position = _offset;
            int count = _input.Read(bytes, 0, bytes.Length);
            count -= count % 4;
            if (!final) count -= count % 12;
            _offset += count;
            int readFrames = count / 4;
            float[] mono = new float[(readFrames + 2) / 3];
            for (int i = 0; i < mono.Length; i++)
            {
                int n = Math.Min(3, readFrames - i * 3);
                double sum = 0;
                for (int j = 0; j < n; j++)
                {
                    int p = (i * 3 + j) * 4;
                    sum += (short)(bytes[p] | bytes[p + 1] << 8);
                    sum += (short)(bytes[p + 2] | bytes[p + 3] << 8);
                }
                mono[i] = (float)(sum / (n * 2.0 * 32768.0));
            }
            SamplesRead += mono.Length;
            return mono;
        }
        public void Dispose() => _input.Dispose();
    }
}
