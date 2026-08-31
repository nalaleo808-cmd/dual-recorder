// Compile-only stand-ins for the NAudio API surface this app uses.
// They exist so the audio layer can be type checked on a machine without
// NuGet or Windows. They are never referenced by the real build.
using System;
using System.Collections.Generic;
using System.IO;

namespace NAudio.Wave
{
    public class WaveFormat
    {
        public virtual int SampleRate { get; protected set; }
        public virtual int Channels { get; protected set; }
        public WaveFormat() : this(44100, 2) { }
        public WaveFormat(int rate, int channels) { SampleRate = rate; Channels = channels; }
        public static WaveFormat CreateIeeeFloatWaveFormat(int rate, int channels) => new WaveFormat(rate, channels);
    }

    public class WaveFormatExtensible : WaveFormat
    {
        public WaveFormatExtensible() : base(48000, 2) { }
        public WaveFormat ToStandardWaveFormat() => new WaveFormat(SampleRate, Channels);
    }

    public class WaveInEventArgs : EventArgs
    {
        public byte[] Buffer { get; set; }
        public int BytesRecorded { get; set; }
    }

    public class StoppedEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
    }

    public interface IWaveProvider
    {
        WaveFormat WaveFormat { get; }
        int Read(byte[] buffer, int offset, int count);
    }

    public interface ISampleProvider
    {
        WaveFormat WaveFormat { get; }
        int Read(float[] buffer, int offset, int count);
    }

    public interface IWaveIn : IDisposable
    {
        WaveFormat WaveFormat { get; }
        event EventHandler<WaveInEventArgs> DataAvailable;
        event EventHandler<StoppedEventArgs> RecordingStopped;
        void StartRecording();
        void StopRecording();
    }

    public class BufferedWaveProvider : IWaveProvider
    {
        public BufferedWaveProvider(WaveFormat format) { WaveFormat = format; }
        public WaveFormat WaveFormat { get; private set; }
        public TimeSpan BufferDuration { get; set; }
        public bool DiscardOnBufferOverflow { get; set; }
        public bool ReadFully { get; set; }
        public void AddSamples(byte[] buffer, int offset, int count) { }
        public void ClearBuffer() { }
        public int Read(byte[] buffer, int offset, int count) => 0;
    }

    public static class WaveExtensionMethods
    {
        public static ISampleProvider ToSampleProvider(this IWaveProvider provider) => null;
    }

    public class WaveFileReader : Stream
    {
        public WaveFileReader(string path) { }
        public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 2);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => 0;
        public override long Seek(long o, SeekOrigin s) => 0;
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }
}

namespace NAudio.Wave.SampleProviders
{
    public class WdlResamplingSampleProvider : ISampleProvider
    {
        public WdlResamplingSampleProvider(ISampleProvider source, int newSampleRate)
        { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSampleRate, source.WaveFormat.Channels); }
        public WaveFormat WaveFormat { get; private set; }
        public int Read(float[] buffer, int offset, int count) => 0;
    }
}

namespace NAudio.CoreAudioApi
{
    public enum DataFlow { Render, Capture, All }
    public enum Role { Console, Multimedia, Communications }

    [Flags]
    public enum DeviceState { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8, All = 15 }

    public class MMDevice : IDisposable
    {
        public string ID { get; set; }
        public string FriendlyName { get; set; }
        public void Dispose() { }
    }

    public class MMDeviceEnumerator : IDisposable
    {
        public IEnumerable<MMDevice> EnumerateAudioEndPoints(DataFlow flow, DeviceState state) => new List<MMDevice>();
        public MMDevice GetDefaultAudioEndpoint(DataFlow flow, Role role) => new MMDevice();
        public MMDevice GetDevice(string id) => new MMDevice();
        public void Dispose() { }
    }

    public class WasapiCapture : NAudio.Wave.IWaveIn
    {
        public WasapiCapture(MMDevice device) { }
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new NAudio.Wave.WaveFormatExtensible();
        public event EventHandler<NAudio.Wave.WaveInEventArgs> DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs> RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
    }

    public class WasapiLoopbackCapture : WasapiCapture
    {
        public WasapiLoopbackCapture(MMDevice device) : base(device) { }
    }
}

namespace NAudio.Lame
{
    public class LameMP3FileWriter : Stream
    {
        public LameMP3FileWriter(string path, NAudio.Wave.WaveFormat format, int bitRate) { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => 0;
        public override long Seek(long o, SeekOrigin s) => 0;
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }
}
