using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using SherpaOnnx;

namespace DualRecorder.Transcription
{
    public sealed class LiveTranscriptionSession
    {
        private readonly string _micPath, _systemPath, _models, _basePath;
        private readonly bool _onlyMe;
        private readonly ConcurrentQueue<long> _micPauses = new ConcurrentQueue<long>();
        private readonly ConcurrentQueue<long> _systemPauses = new ConcurrentQueue<long>();
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
        public static LiveTranscriptionSession LoadSaved(string jsonPath, string models)
        {
            const string suffix = "_transcript.json";
            if (!jsonPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a DualRecorder transcript JSON file.");
            string prefix = jsonPath.Substring(0, jsonPath.Length - suffix.Length);
            var session = new LiveTranscriptionSession(prefix + "_mic-only.wav", prefix + "_system-only.wav", models, "You", false);
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var source = document.RootElement;
            foreach (var person in source.GetProperty("speakers").Deserialize<SpeakerIdentity[]>()) session.Speakers.RestorePerson(person.Id, person.Name);
            foreach (var entry in source.GetProperty("segments").Deserialize<TranscriptEntry[]>())
            {
                entry.SpeakerName = session.Speakers.Name(entry.SpeakerId);
                entry.IsFinal = true;
                session._final.Add(entry);
            }
            string status = source.GetProperty("status").GetString();
            if (status != null && status.StartsWith("Incomplete:", StringComparison.OrdinalIgnoreCase)) session._failure = status.Substring(11).Trim();
            session._finished = true;
            return session;
        }
        public void Start() => Completion = Task.Run(Run);
        public void SetPaused(bool paused)
        {
            if (paused)
            {
                // Record exact audio boundaries so a quick resume cannot erase a queued pause.
                _micPauses.Enqueue(Math.Max(0, (new FileInfo(_micPath).Length - 44) / 12));
                _systemPauses.Enqueue(Math.Max(0, (new FileInfo(_systemPath).Length - 44) / 12));
            }
            _paused = paused;
        }
        public void RequestStop() => _stop = true;
        public TranscriptEntry[] Entries { get { lock (_entriesGate) return _final.ToArray(); } }
        public void Save()
        {
            lock (_entriesGate) TranscriptExport.Save(_basePath, _final.ToArray(), Speakers,
                _failure != null ? "Incomplete: " + _failure : _finished ? "Complete" : "Live snapshot");
        }

        public void CorrectText(string entryId, string text)
        {
            lock (_entriesGate)
            {
                var entry = _final.FirstOrDefault(x => x.Id == entryId);
                if (entry == null) throw new InvalidOperationException("Wait until this speech section is final before editing it.");
                entry.Text = text ?? "";
                entry.UserEdited = true;
            }
            if (_finished) Save();
        }
        public void AssignSpeaker(IEnumerable<string> entryIds, string speakerId)
        {
            if (!Speakers.Identities.Any(x => x.Id == speakerId)) throw new ArgumentException("Choose a named speaker.");
            var ids = new HashSet<string>(entryIds);
            lock (_entriesGate)
                foreach (var entry in _final.Where(x => ids.Contains(x.Id)))
                {
                    entry.SpeakerId = speakerId;
                    entry.SpeakerName = Speakers.Name(speakerId);
                    entry.NeedsReview = false;
                    entry.UserEdited = true;
                }
            if (_finished) Save();
        }
        public Task<bool> LearnSpeakerAsync(TranscriptEntry example, string speakerId) => Task.Run(() =>
        {
            if (example.End - example.Start < 1.5) return false;
            string path = example.Source == "Microphone" ? _micPath : _systemPath;
            if (!File.Exists(path)) return false;
            using var reader = new GrowingWaveReader(path);
            reader.SeekTo(example.Start);
            var samples = new List<float>();
            int maximum = (int)(Math.Min(15, example.End - example.Start) * 16000);
            while (samples.Count < maximum)
            {
                var block = reader.ReadBlock(Math.Min(1600, maximum - samples.Count), true);
                if (block.Length == 0) break;
                samples.AddRange(block);
            }
            if (samples.Count < 24000) return false;
            var config = new SpeakerEmbeddingExtractorConfig();
            config.Model = Path.Combine(_models, "embedding.onnx");
            config.NumThreads = 1;
            using var extractor = new SpeakerEmbeddingExtractor(config);
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(16000, samples.ToArray());
            stream.InputFinished();
            if (!extractor.IsReady(stream)) return false;
            Speakers.LearnVoice(speakerId, extractor.Compute(stream));
            return true;
        });

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
                config.ModelConfig.ModelType = "zipformer2";
                config.DecodingMethod = "greedy_search";
                config.EnableEndpoint = 1;
                config.Rule1MinTrailingSilence = 2.4f;
                config.Rule2MinTrailingSilence = 1.0f;
                config.Rule3MinUtteranceLength = 12;
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
            private readonly ConcurrentQueue<long> _pauses;
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
                _pauses = source == "Microphone" ? owner._micPauses : owner._systemPauses;
                _recognizer = recognizer;
                _segmenter = segmenter;
                _owner = owner;
                _stream = recognizer.CreateStream();
            }
            public bool Read(bool stopping)
            {
                while (_pauses.TryPeek(out var boundary) && boundary <= _reader.SamplesRead)
                {
                    Finish(true);
                    _pauses.TryDequeue(out _);
                }
                int maximum = _pauses.TryPeek(out var next) ? (int)Math.Min(1600, Math.Max(1, next - _reader.SamplesRead)) : 1600;
                var samples = _reader.ReadBlock(maximum, stopping);
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
