using System;
using System.IO;
using DualRecorder.Core;

namespace CoreTests
{
    internal static class Program
    {
        private const int Rate = 48000;
        private const int Ch = 2;
        private static int _fail;

        private static void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
            if (!ok) _fail++;
        }

        private static string Dir(string name)
        {
            string d = Path.Combine(Path.GetTempPath(), "dualrec-tests", name);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            return d;
        }

        // read the RIFF header straight off disk, the way a media player would
        private static (long dataBytes, long riffSize, int rate, int ch, long fileLen) ReadWav(string p)
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var h = new byte[44];
            int n = fs.Read(h, 0, 44);
            if (n < 44) return (-1, -1, -1, -1, fs.Length);
            string riff = System.Text.Encoding.ASCII.GetString(h, 0, 4);
            string wave = System.Text.Encoding.ASCII.GetString(h, 8, 4);
            string data = System.Text.Encoding.ASCII.GetString(h, 36, 4);
            if (riff != "RIFF" || wave != "WAVE" || data != "data") return (-2, -2, -2, -2, fs.Length);
            uint riffSize = BitConverter.ToUInt32(h, 4);
            int ch = BitConverter.ToUInt16(h, 22);
            int rate = (int)BitConverter.ToUInt32(h, 24);
            uint dataBytes = BitConverter.ToUInt32(h, 40);
            return (dataBytes, riffSize, rate, ch, fs.Length);
        }

        private static float[] Tone(int frames, float amp, ref double phase, double hz)
        {
            var b = new float[frames * Ch];
            double step = 2 * Math.PI * hz / Rate;
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(Math.Sin(phase) * amp);
                b[i * Ch] = v; b[i * Ch + 1] = v;
                phase += step;
            }
            return b;
        }

        private static void Main()
        {
            Normal();
            SilentLoopback();
            DeviceUnplug();
            CrashMidRecording();
            ClippingAndOverflow();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "ALL TESTS PASSED" : _fail + " TEST(S) FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        // 10 simulated seconds, both sources delivering normally in 20 ms chunks
        private static void Normal()
        {
            Console.WriteLine("-- normal recording, both sources live");
            string d = Dir("normal");
            var mic = new SourceTrack("mic", Path.Combine(d, "mic.wav"), Rate, Ch);
            var sys = new SourceTrack("sys", Path.Combine(d, "sys.wav"), Rate, Ch);
            var mix = new MixWriter(Path.Combine(d, "mix.wav"), Rate, Ch, mic, sys);
            var pump = new SyncPump(mic, sys, mix, Rate);

            double pa = 0, pb = 0;
            int chunk = Rate / 50; // 20 ms
            for (int t = 0; t < 500; t++)  // 10 s
            {
                mic.Append(Tone(chunk, 0.5f, ref pa, 440), chunk * Ch);
                sys.Append(Tone(chunk, 0.4f, ref pb, 220), chunk * Ch);
                pump.Tick((long)(t + 1) * chunk);
            }
            long clock = 500L * chunk;
            pump.Finalise(clock);
            mic.Dispose(); sys.Dispose(); mix.Dispose();

            var m = ReadWav(Path.Combine(d, "mic.wav"));
            var s = ReadWav(Path.Combine(d, "sys.wav"));
            var x = ReadWav(Path.Combine(d, "mix.wav"));
            long want = clock * Ch * 2;
            Check("mic length exact", m.dataBytes == want, m.dataBytes + " vs " + want);
            Check("system length exact", s.dataBytes == want, s.dataBytes + " vs " + want);
            Check("mixed length exact", x.dataBytes == want, x.dataBytes + " vs " + want);
            Check("no padding needed when both live", pump.MicPadded == 0 && pump.SystemPadded == 0,
                  "mic=" + pump.MicPadded + " sys=" + pump.SystemPadded);
            Check("headers agree with file size", m.riffSize + 8 == m.fileLen && x.riffSize + 8 == x.fileLen);
            Check("format is 48k stereo", m.rate == Rate && m.ch == Ch);
        }

        // the real failure mode: loopback fires no callbacks at all while the speakers are silent
        private static void SilentLoopback()
        {
            Console.WriteLine("-- system audio silent for 6 s in the middle (no loopback callbacks)");
            string d = Dir("silent");
            var mic = new SourceTrack("mic", Path.Combine(d, "mic.wav"), Rate, Ch);
            var sys = new SourceTrack("sys", Path.Combine(d, "sys.wav"), Rate, Ch);
            var mix = new MixWriter(Path.Combine(d, "mix.wav"), Rate, Ch, mic, sys);
            var pump = new SyncPump(mic, sys, mix, Rate);

            double pa = 0, pb = 0;
            int chunk = Rate / 50;
            for (int t = 0; t < 500; t++)
            {
                mic.Append(Tone(chunk, 0.5f, ref pa, 440), chunk * Ch);
                bool speakersPlaying = t < 100 || t >= 400;   // silent from 2 s to 8 s
                if (speakersPlaying) sys.Append(Tone(chunk, 0.4f, ref pb, 220), chunk * Ch);
                pump.Tick((long)(t + 1) * chunk);
            }
            long clock = 500L * chunk;
            pump.Finalise(clock);
            mic.Dispose(); sys.Dispose(); mix.Dispose();

            var m = ReadWav(Path.Combine(d, "mic.wav"));
            var s = ReadWav(Path.Combine(d, "sys.wav"));
            var x = ReadWav(Path.Combine(d, "mix.wav"));
            long want = clock * Ch * 2;
            Check("mic length exact", m.dataBytes == want, m.dataBytes + "");
            Check("system padded to same length", s.dataBytes == want, s.dataBytes + " vs " + want);
            Check("mixed same length", x.dataBytes == want, x.dataBytes + " vs " + want);
            double padSec = pump.SystemPadded / (double)Rate;
            Check("padded about 6 s of silence", padSec > 5.5 && padSec < 6.3, padSec.ToString("0.000") + " s");
            Check("tail of system track is silence, not shifted audio", TailIsSilent(Path.Combine(d, "sys.wav")) == false);
        }

        // last audio in the silent test resumes at 8 s, so the final second should NOT be silent:
        // if it were, the track had slid late. returns true if the last 0.5 s is all zero.
        private static bool TailIsSilent(string p)
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read);
            long bytes = Rate / 2 * Ch * 2;
            fs.Seek(-bytes, SeekOrigin.End);
            var b = new byte[bytes];
            int read = fs.Read(b, 0, b.Length);
            for (int i = 0; i < read; i++) if (b[i] != 0) return false;
            return true;
        }

        // mic yanked out of the USB port at 4 s
        private static void DeviceUnplug()
        {
            Console.WriteLine("-- mic unplugged at 4 s");
            string d = Dir("unplug");
            var mic = new SourceTrack("mic", Path.Combine(d, "mic.wav"), Rate, Ch);
            var sys = new SourceTrack("sys", Path.Combine(d, "sys.wav"), Rate, Ch);
            var mix = new MixWriter(Path.Combine(d, "mix.wav"), Rate, Ch, mic, sys);
            var pump = new SyncPump(mic, sys, mix, Rate);

            double pa = 0, pb = 0;
            int chunk = Rate / 50;
            for (int t = 0; t < 500; t++)
            {
                if (t == 200) mic.MarkFaulted("Device removed");
                if (!mic.Faulted) mic.Append(Tone(chunk, 0.5f, ref pa, 440), chunk * Ch);
                sys.Append(Tone(chunk, 0.4f, ref pb, 220), chunk * Ch);
                pump.Tick((long)(t + 1) * chunk);
            }
            long clock = 500L * chunk;
            pump.Finalise(clock);
            mic.Dispose(); sys.Dispose(); mix.Dispose();

            var m = ReadWav(Path.Combine(d, "mic.wav"));
            var x = ReadWav(Path.Combine(d, "mix.wav"));
            long want = clock * Ch * 2;
            Check("mic file finalised at full length", m.dataBytes == want, m.dataBytes + " vs " + want);
            Check("first 4 s of mic survived", FirstSecondHasAudio(Path.Combine(d, "mic.wav")));
            Check("mixed still complete", x.dataBytes == want);
        }

        private static bool FirstSecondHasAudio(string p)
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read);
            fs.Seek(44, SeekOrigin.Begin);
            var b = new byte[Rate * Ch * 2];
            int read = fs.Read(b, 0, b.Length);
            for (int i = 0; i < read; i++) if (b[i] != 0) return true;
            return false;
        }

        // process killed mid recording: nothing is disposed, files are never closed
        private static void CrashMidRecording()
        {
            Console.WriteLine("-- process killed mid recording (nothing disposed)");
            string d = Dir("crash");
            var mic = new SourceTrack("mic", Path.Combine(d, "mic.wav"), Rate, Ch);
            var sys = new SourceTrack("sys", Path.Combine(d, "sys.wav"), Rate, Ch);
            var mix = new MixWriter(Path.Combine(d, "mix.wav"), Rate, Ch, mic, sys);
            var pump = new SyncPump(mic, sys, mix, Rate);

            double pa = 0, pb = 0;
            int chunk = Rate / 50;
            for (int t = 0; t < 350; t++)   // 7 s then "crash"
            {
                mic.Append(Tone(chunk, 0.5f, ref pa, 440), chunk * Ch);
                sys.Append(Tone(chunk, 0.4f, ref pb, 220), chunk * Ch);
                pump.Tick((long)(t + 1) * chunk);
            }
            // deliberately no Dispose, no Finalise

            var m = ReadWav(Path.Combine(d, "mic.wav"));
            var x = ReadWav(Path.Combine(d, "mix.wav"));
            Check("crashed mic file has a valid header", m.dataBytes > 0, "dataBytes=" + m.dataBytes);
            Check("crashed mic header covers at least 6 s",
                  m.dataBytes >= (long)6 * Rate * Ch * 2, (m.dataBytes / (double)(Rate * Ch * 2)).ToString("0.00") + " s");
            Check("crashed mix file has a valid header", x.dataBytes > 0, "dataBytes=" + x.dataBytes);
            Check("header never claims more data than is on disk", m.dataBytes + 44 <= m.fileLen,
                  m.dataBytes + 44 + " vs " + m.fileLen);
            mic.Dispose(); sys.Dispose(); mix.Dispose();
        }

        private static void ClippingAndOverflow()
        {
            Console.WriteLine("-- clipping, NaN and fifo behaviour");
            string d = Dir("clip");
            var mic = new SourceTrack("mic", Path.Combine(d, "mic.wav"), Rate, Ch);
            var sys = new SourceTrack("sys", Path.Combine(d, "sys.wav"), Rate, Ch);
            var mix = new MixWriter(Path.Combine(d, "mix.wav"), Rate, Ch, mic, sys);
            var pump = new SyncPump(mic, sys, mix, Rate);

            var loud = new float[Ch * 1000];
            for (int i = 0; i < loud.Length; i++) loud[i] = i % 3 == 0 ? 9.5f : (i % 3 == 1 ? -9.5f : float.NaN);
            mic.Append(loud, loud.Length);
            sys.Append(loud, loud.Length);
            pump.Tick(1000);
            pump.Finalise(1000);
            mic.Dispose(); sys.Dispose(); mix.Dispose();

            bool inRange = true;
            using (var fs = new FileStream(Path.Combine(d, "mix.wav"), FileMode.Open, FileAccess.Read))
            {
                fs.Seek(44, SeekOrigin.Begin);
                var b = new byte[2000];
                int n = fs.Read(b, 0, b.Length);
                for (int i = 0; i + 1 < n; i += 2)
                {
                    short v = BitConverter.ToInt16(b, i);
                    if (v != 32767 && v != -32767 && v != 0) { inRange = false; break; }
                }
            }
            Check("out of range and NaN samples are clamped, not wrapped", inRange);

            var fifo = new FloatFifo(1024, 2048);
            var src = new float[5000];
            for (int i = 0; i < src.Length; i++) src[i] = i;
            fifo.Write(src, 0, 1000);
            fifo.Write(src, 0, 5000);
            Check("fifo caps instead of growing forever", fifo.Count <= 2048, "count=" + fifo.Count);
            var outb = new float[5000];
            int got = fifo.Read(outb, 0, 5000);
            Check("fifo keeps the newest data", got == 2048 && outb[got - 1] == 4999f,
                  "got=" + got + " last=" + (got > 0 ? outb[got - 1] : -1));
            Check("fifo is empty after a full read", fifo.Count == 0, "count=" + fifo.Count);
        }
    }
}
