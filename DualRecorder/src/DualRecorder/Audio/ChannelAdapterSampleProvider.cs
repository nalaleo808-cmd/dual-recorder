using System;
using NAudio.Wave;

namespace DualRecorder.Audio
{
    /// <summary>
    /// Converts any channel count to the target channel count (mono is duplicated,
    /// surround is folded down to the first two channels). Sample rate is untouched.
    /// </summary>
    public sealed class ChannelAdapterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _inCh;
        private readonly int _outCh;
        private float[] _src = new float[0];

        public WaveFormat WaveFormat { get; }

        public ChannelAdapterSampleProvider(ISampleProvider source, int outChannels)
        {
            _source = source;
            _inCh = source.WaveFormat.Channels;
            _outCh = outChannels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_inCh == _outCh) return _source.Read(buffer, offset, count);

            int frames = count / _outCh;
            int need = frames * _inCh;
            if (_src.Length < need) _src = new float[need];

            int got = _source.Read(_src, 0, need);
            int gotFrames = got / _inCh;

            for (int f = 0; f < gotFrames; f++)
            {
                if (_inCh == 1)
                {
                    float v = _src[f];
                    for (int c = 0; c < _outCh; c++) buffer[offset + f * _outCh + c] = v;
                }
                else if (_outCh == 1)
                {
                    float sum = 0f;
                    for (int c = 0; c < _inCh; c++) sum += _src[f * _inCh + c];
                    buffer[offset + f] = sum / _inCh;
                }
                else
                {
                    for (int c = 0; c < _outCh; c++)
                        buffer[offset + f * _outCh + c] = _src[f * _inCh + Math.Min(c, _inCh - 1)];
                }
            }
            return gotFrames * _outCh;
        }
    }
}
