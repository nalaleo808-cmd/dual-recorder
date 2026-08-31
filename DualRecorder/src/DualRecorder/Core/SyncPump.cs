using System;

namespace DualRecorder.Core
{
    /// <summary>
    /// The timing brain of the recorder. It is deliberately free of any audio library so
    /// it can be unit tested off Windows.
    ///
    /// Rule: every source must have written as many frames as the recording clock says have
    /// elapsed. A live source is naturally a little behind (device buffer), so we allow a
    /// tolerance and only pad beyond it. A silent loopback stream, or a device that was
    /// unplugged, falls further and further behind and gets padded with silence, which is
    /// what keeps the three output files the same length and in sync.
    /// </summary>
    public sealed class SyncPump
    {
        private readonly SourceTrack _mic;
        private readonly SourceTrack _sys;
        private readonly MixWriter _mix;
        private readonly int _sampleRate;
        private readonly long _toleranceFrames;

        public SyncPump(SourceTrack mic, SourceTrack sys, MixWriter mix, int sampleRate, int toleranceMs = 250)
        {
            _mic = mic; _sys = sys; _mix = mix;
            _sampleRate = sampleRate;
            _toleranceFrames = (long)sampleRate * toleranceMs / 1000;
        }

        public long MicPadded => _mic.PaddedFrames;
        public long SystemPadded => _sys.PaddedFrames;

        /// <summary>Call every ~50 ms with the frame count implied by the recording clock.</summary>
        public void Tick(long clockFrames)
        {
            long target = clockFrames - _toleranceFrames;

            // a faulted device will never deliver again, so pad it right up to the clock
            _mic.PadTo(_mic.Faulted ? clockFrames : target);
            _sys.PadTo(_sys.Faulted ? clockFrames : target);

            _mix.Pump();
        }

        /// <summary>Called once on stop: level both tracks exactly and drain the mixer.</summary>
        public void Finalise(long clockFrames)
        {
            long end = Math.Max(clockFrames, Math.Max(_mic.FramesWritten, _sys.FramesWritten));
            _mic.PadTo(end);
            _sys.PadTo(end);
            _mix.Pump();
        }

        public static long MsToFrames(double milliseconds, int sampleRate)
            => (long)(milliseconds * sampleRate / 1000.0);
    }
}
