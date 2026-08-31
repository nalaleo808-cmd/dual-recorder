using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DualRecorder.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualRecorder.Audio
{
    public enum RecorderState { Idle, Recording, Paused, Stopping }

    public sealed class RecordingResult
    {
        public string MicPath { get; set; }
        public string SystemPath { get; set; }
        public string MixedPath { get; set; }
        public TimeSpan Duration { get; set; }
        public long MicSilenceFrames { get; set; }
        public long SystemSilenceFrames { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Owns both captures, the three output files and the sync clock.
    /// Everything that can throw is contained: a dead device degrades to silence,
    /// it never takes the recording down.
    /// </summary>
    public sealed class RecordingEngine : IDisposable
    {
        public const int TargetRate = 48000;
        public const int TargetChannels = 2;
        private const int TickMs = 50;

        private readonly object _gate = new object();
        private readonly Stopwatch _clock = new Stopwatch();

        private SourceTrack _micTrack, _sysTrack;
        private MixWriter _mix;
        private SyncPump _pump;
        private CaptureSource _micCap, _sysCap;
        private Timer _timer;
        private int _inTick;
        private RecordingResult _result;

        public RecorderState State { get; private set; } = RecorderState.Idle;
        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>Human readable notice, e.g. a device dropping out. Raised off the UI thread.</summary>
        public event Action<string> Notice;

        public float TakeMicPeak() { var t = _micTrack; return t == null ? 0f : t.TakePeak(); }
        public float TakeSystemPeak() { var t = _sysTrack; return t == null ? 0f : t.TakePeak(); }
        public bool MicFaulted => _micTrack != null && _micTrack.Faulted;
        public bool SystemFaulted => _sysTrack != null && _sysTrack.Faulted;

        public RecordingResult Start(string micDeviceId, string renderDeviceId, string folder)
        {
            lock (_gate)
            {
                if (State != RecorderState.Idle) throw new InvalidOperationException("Already recording.");

                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                _result = new RecordingResult
                {
                    MicPath = Path.Combine(folder, stamp + "_mic-only.wav"),
                    SystemPath = Path.Combine(folder, stamp + "_system-only.wav"),
                    MixedPath = Path.Combine(folder, stamp + "_mixed.wav")
                };

                _micTrack = new SourceTrack("Microphone", _result.MicPath, TargetRate, TargetChannels);
                _sysTrack = new SourceTrack("System audio", _result.SystemPath, TargetRate, TargetChannels);
                _mix = new MixWriter(_result.MixedPath, TargetRate, TargetChannels, _micTrack, _sysTrack);
                _pump = new SyncPump(_micTrack, _sysTrack, _mix, TargetRate);

                try
                {
                    var micDevice = AudioDevices.Resolve(micDeviceId, DataFlow.Capture);
                    var micCapture = new WasapiCapture(micDevice);
                    _micCap = new CaptureSource("Microphone", micCapture, _micTrack, TargetRate, TargetChannels);
                    _micCap.Faulted += OnFaulted;
                }
                catch (Exception ex)
                {
                    _micTrack.MarkFaulted(ex.Message);
                    _result.Warnings.Add("Microphone could not be opened: " + ex.Message + ". Its track will be silent.");
                }

                try
                {
                    var renderDevice = AudioDevices.Resolve(renderDeviceId, DataFlow.Render);
                    var loopCapture = new WasapiLoopbackCapture(renderDevice);
                    _sysCap = new CaptureSource("System audio", loopCapture, _sysTrack, TargetRate, TargetChannels);
                    _sysCap.Faulted += OnFaulted;
                }
                catch (Exception ex)
                {
                    _sysTrack.MarkFaulted(ex.Message);
                    _result.Warnings.Add("Speaker loopback could not be opened: " + ex.Message + ". Its track will be silent.");
                }

                _clock.Reset();
                _clock.Start();
                State = RecorderState.Recording;

                TryStart(_micCap, _micTrack);
                TryStart(_sysCap, _sysTrack);

                _timer = new Timer(OnTick, null, TickMs, TickMs);
                return _result;
            }
        }

        private void TryStart(CaptureSource cap, SourceTrack track)
        {
            if (cap == null) return;
            try { cap.Start(); }
            catch (Exception ex)
            {
                track.MarkFaulted(ex.Message);
                RaiseNotice(track.Name + " failed to start: " + ex.Message + ". Recording continues with silence on that track.");
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (State != RecorderState.Recording) return;
                // catch up to the clock first so the pause point is exact
                SafeTick();
                _clock.Stop();
                if (_micCap != null) _micCap.SetAccepting(false);
                if (_sysCap != null) _sysCap.SetAccepting(false);
                State = RecorderState.Paused;
                FlushHeaders();
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (State != RecorderState.Paused) return;
                if (_micCap != null) _micCap.SetAccepting(true);
                if (_sysCap != null) _sysCap.SetAccepting(true);
                _clock.Start();
                State = RecorderState.Recording;
            }
        }

        public RecordingResult Stop()
        {
            lock (_gate)
            {
                if (State == RecorderState.Idle) return null;
                State = RecorderState.Stopping;

                if (_timer != null) { _timer.Dispose(); _timer = null; }
                if (_micCap != null) _micCap.Stop();
                if (_sysCap != null) _sysCap.Stop();

                // let any in flight WASAPI buffer land before we level the tracks
                Thread.Sleep(120);
                _clock.Stop();

                long frames = ClockFrames();
                try { _pump.Finalise(frames); } catch (Exception ex) { _result.Warnings.Add("Finalise: " + ex.Message); }

                if (_micCap != null) { _micCap.Dispose(); _micCap = null; }
                if (_sysCap != null) { _sysCap.Dispose(); _sysCap = null; }

                _result.Duration = _clock.Elapsed;
                _result.MicSilenceFrames = _micTrack.PaddedFrames;
                _result.SystemSilenceFrames = _sysTrack.PaddedFrames;
                if (_micTrack.Faulted)
                    _result.Warnings.Add("Microphone dropped out during the recording (" + _micTrack.FaultMessage + "). Everything captured before that point was saved.");
                if (_sysTrack.Faulted)
                    _result.Warnings.Add("Speaker device dropped out during the recording (" + _sysTrack.FaultMessage + "). Everything captured before that point was saved.");

                _mix.Dispose();
                _micTrack.Dispose();
                _sysTrack.Dispose();

                var r = _result;
                _mix = null; _micTrack = null; _sysTrack = null; _pump = null; _result = null;
                State = RecorderState.Idle;
                return r;
            }
        }

        private long ClockFrames() => (long)(_clock.Elapsed.TotalSeconds * TargetRate);

        private void OnTick(object state)
        {
            if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;
            try { if (State == RecorderState.Recording) SafeTick(); }
            finally { Interlocked.Exchange(ref _inTick, 0); }
        }

        private void SafeTick()
        {
            try { _pump.Tick(ClockFrames()); }
            catch (Exception ex) { RaiseNotice("Writer problem: " + ex.Message); }
        }

        private void FlushHeaders()
        {
            try { _micTrack.FlushHeader(); _sysTrack.FlushHeader(); _mix.FlushHeader(); } catch { }
        }

        private void OnFaulted(CaptureSource source, string message)
        {
            RaiseNotice(source.Label + " stopped delivering audio (" + message +
                        "). That track is being filled with silence so the files stay in sync. Press Stop to save.");
        }

        private void RaiseNotice(string message)
        {
            var h = Notice;
            if (h != null) { try { h(message); } catch { } }
        }

        public void Dispose()
        {
            try { if (State != RecorderState.Idle) Stop(); } catch { }
            if (_timer != null) { _timer.Dispose(); _timer = null; }
        }
    }
}
