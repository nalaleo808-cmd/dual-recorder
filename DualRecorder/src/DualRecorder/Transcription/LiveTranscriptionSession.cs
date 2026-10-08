using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace DualRecorder.Transcription
{
    public sealed class LiveTranscriptionSession
    {
        private readonly string _micPath, _systemPath, _models, _basePath;
        private readonly bool _onlyMe;
        private readonly List<TranscriptEntry> _final = new List<TranscriptEntry>();
        private readonly object _entriesGate = new object();
        private volatile bool _stop, _paused, _finished;
        private string _failure;
        public SpeakerRegistry Speakers { get; }
        public Task Completion { get; private set; } = Task.CompletedTask;
        public bool Finished => _finished;
        public string TranscriptPath => _basePath + "_transcript.txt";
        public event Action<TranscriptUpdate> Updated;
        public event Action<string> StatusChanged;

        public LiveTranscriptionSession(string micPath, string systemPath, string models, string microphoneName, bool onlyMe)
        {
            _micPath = micPath;
            _systemPath = systemPath;
            _models = models;
            _onlyMe = onlyMe;
            _basePath = micPath.Substring(0, micPath.Length - "_mic-only.wav".Length);
            Speakers = new SpeakerRegistry(microphoneName, onlyMe);
        }
        public void Start() => Completion = Task.Run(Run);
        public void SetPaused(bool paused) => _paused = paused;
        public void RequestStop() => _stop = true;
        public TranscriptEntry[] Entries { get { lock (_entriesGate) return _final.ToArray(); } }
        public void Save()
        {
            lock (_entriesGate) TranscriptExport.Save(_basePath, _final.ToArray(), Speakers,
                _failure != null ? "Incomplete: " + _failure : _finished ? "Complete" : "Live snapshot");
        }

        private void Run()
        {
            try
            {
                Notice("Loading local speech models...");
                if (!SpeechModels.Verify(_models)) throw new InvalidDataException("Speech files are missing or damaged. Run speech setup again.");
                var config = new OnlineRecognizerConfig();
                config.FeatConfig.SampleRate = 16000;
                config.FeatConfig.FeatureDim = 80;
                config.ModelConfig.Transducer.Encoder = Path.Combine(_models, "encoder.onnx");
                config.ModelConfig.Transducer.Decoder = Path.Combine(_models, "decoder.onnx");
                config.ModelConfig.Transducer.Joiner = Path.Combine(_models, "joiner.onnx");
                config.ModelConfig.Tokens = Path.Combine(_models, "tokens.txt");
                config.ModelConfig.NumThreads = 2;
                config.ModelConfig.Provider = "cpu";
                config.DecodingMethod = "greedy_search";
                config.EnableEndpoint = 1;
                config.Rule1MinTrailingSilence = 2.4f;
                config.Rule2MinTrailingSilence = 0.8f;
                config.Rule3MinUtteranceLength = 8;
                using var recognizer = new OnlineRecognizer(config);
                using var segmenter = new SpeakerSegmenter(_models, Speakers);
                using var mic = new Track(_micPath, "Microphone", _onlyMe, recognizer, segmenter, this);
                using var system = new Track(_systemPath, "Computer audio", false, recognizer, segmenter, this);
                Notice("Live transcript. Text is a draft until the speaker finishes a short section.");
                string lastStatus = "";
                while (true)
                {
                    bool read = mic.Read(_stop) | system.Read(_stop);
                    double lag = Math.Max(mic.BacklogSeconds, system.BacklogSeconds);
                    string status = _stop ? "Finishing transcript..." : _paused ? "Transcription paused" :
                        lag > 3 ? $"Transcription is catching up ({lag:0} seconds behind). Audio is still being saved." : "Live transcript - processed on this PC";
                    if (status != lastStatus) { Notice(status); lastStatus = status; }
                    if (!read)
                    {
                        if (_stop) { mic.Finish(false); system.Finish(false); break; }
                        if (_paused) { mic.Finish(true); system.Finish(true); }
                        Thread.Sleep(80);
                    }
                }
            }
            catch (Exception ex)
            {
                _failure = ex.Message;
                Notice("Transcription stopped: " + ex.Message + " Audio recording is unaffected.");
            }
            finally
            {
                _finished = true;
                try { Save(); Notice(_failure == null ? "Transcript saved: " + TranscriptPath : "Partial transcript saved: " + TranscriptPath); }
                catch (Exception ex) { Notice("Could not save the transcript: " + ex.Message); }
            }
        }
        private void Notice(string text) { try { StatusChanged?.Invoke(text); } catch { } }
        private void Publish(TranscriptUpdate update)
        {
            lock (_entriesGate) _final.AddRange(update.Entries.Where(x => x.IsFinal));
            try { Updated?.Invoke(update); } catch { }
        }

        private sealed class Track : IDisposable
        {
            private readonly GrowingWaveReader _reader;
            private readonly OnlineRecognizer _recognizer;
            private readonly SpeakerSegmenter _segmenter;
            private readonly LiveTranscriptionSession _owner;
            private readonly string _source;
            private readonly bool _onlyMe;
            private readonly List<float> _audio = new List<float>();
            private OnlineStream _stream;
            private string _lastText = "";
            private int _index;
            private int _nextSpeakerAt = 48000;
            private string _partialSpeaker = "pending";
            private long _beginSample;
            public double BacklogSeconds => _reader.AvailableSamples / 16000.0;
            private string DraftId => _source + "-" + _index + "-draft";

            public Track(string path, string source, bool onlyMe, OnlineRecognizer recognizer, SpeakerSegmenter segmenter, LiveTranscriptionSession owner)
            {
                _reader = new GrowingWaveReader(path);
                _source = source;
                _onlyMe = onlyMe;
                _recognizer = recognizer;
                _segmenter = segmenter;
                _owner = owner;
                _stream = recognizer.CreateStream();
            }
            public bool Read(bool stopping)
            {
                var samples = _reader.ReadBlock(final: stopping);
                if (samples.Length == 0) return false;
                _audio.AddRange(samples);
                _stream.AcceptWaveform(16000, samples);
                while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
                var result = _recognizer.GetResult(_stream);
                if (result.Text.Length > 0 && result.Text != _lastText)
                {
                    _lastText = result.Text;
                    if (!_onlyMe && _audio.Count >= _nextSpeakerAt)
                    {
                        var preview = _segmenter.Split("preview", _source, 0, _audio.Count / 16000.0,
                            _audio.ToArray(), "preview", Array.Empty<string>(), Array.Empty<float>(), false);
                        _partialSpeaker = preview.Length == 1 ? preview[0].SpeakerId : SpeakerRegistry.UnknownId;
                        _nextSpeakerAt = _audio.Count + 32000;
                    }
                    string speaker = _onlyMe ? SpeakerRegistry.MicrophoneId : _partialSpeaker;
                    _owner.Publish(new TranscriptUpdate { RemoveId = DraftId, Entries = new[] {
                        new TranscriptEntry { Id = DraftId, Start = _beginSample / 16000.0,
                            End = _reader.SamplesRead / 16000.0, Source = _source, SpeakerId = speaker,
                            SpeakerName = _owner.Speakers.Name(speaker), Text = result.Text, IsFinal = false }
                    }});
                }
                if (_recognizer.IsEndpoint(_stream)) Finish(true);
                return true;
            }
            public void Finish(bool restart)
            {
                if (_audio.Count == 0) return;
                _stream.AcceptWaveform(16000, new float[8000]);
                _stream.InputFinished();
                while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
                var result = _recognizer.GetResult(_stream);
                double offset = _beginSample / 16000.0;
                double duration = _audio.Count / 16000.0;
                var entries = _segmenter.Split(_source + "-" + _index, _source, offset, duration,
                    _audio.ToArray(), result.Text, result.Tokens, result.Timestamps, _onlyMe);
                _owner.Publish(new TranscriptUpdate { RemoveId = DraftId, Entries = entries });
                _audio.Clear();
                _lastText = "";
                _partialSpeaker = "pending";
                _nextSpeakerAt = 48000;
                _beginSample = _reader.SamplesRead;
                _index++;
                _stream.Dispose();
                _stream = restart ? _recognizer.CreateStream() : null;
            }
            public void Dispose() { _stream?.Dispose(); _reader.Dispose(); }
        }
    }
}
