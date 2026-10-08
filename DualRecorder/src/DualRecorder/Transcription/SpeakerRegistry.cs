using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;

namespace DualRecorder.Transcription
{
    public sealed class SpeakerIdentity : INotifyPropertyChanged
    {
        public string Id { get; set; }
        private string _name;
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
        public string Label => Id == SpeakerRegistry.MicrophoneId ? "Your microphone" : Id.Replace("speaker-", "Voice ");
        public event PropertyChangedEventHandler PropertyChanged;
        public override string ToString() => Name;
    }

    public sealed class SpeakerRegistry
    {
        private sealed class Voice
        {
            public SpeakerIdentity Identity;
            public float[] Embedding;
        }
        private readonly object _gate = new object();
        private readonly List<Voice> _voices = new List<Voice>();
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>();
        public const string UnknownId = "unknown";
        public const string OverlapId = "overlap";
        public const string MicrophoneId = "microphone";

        public SpeakerRegistry(string microphoneName = "You", bool includeMicrophone = true)
        {
            _names[UnknownId] = "Unknown speaker";
            _names[OverlapId] = "Overlapping voices";
            if (includeMicrophone) _names[MicrophoneId] = string.IsNullOrWhiteSpace(microphoneName) ? "You" : microphoneName.Trim();
        }
        public string Name(string id) { lock (_gate) return _names.TryGetValue(id, out var name) ? name : "Identifying speaker"; }
        public SpeakerIdentity[] Identities
        {
            get
            {
                lock (_gate) return _names.Where(x => x.Key != UnknownId && x.Key != OverlapId)
                    .Select(x => new SpeakerIdentity { Id = x.Key, Name = x.Value }).ToArray();
            }
        }
        public void Rename(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a speaker name.");
            lock (_gate)
            {
                if (!_names.ContainsKey(id) || id == UnknownId || id == OverlapId) throw new ArgumentException("Choose a detected speaker.");
                _names[id] = name.Trim();
            }
        }
        public string MatchOrAdd(float[] embedding, ISet<string> excluded = null)
        {
            if (embedding == null || embedding.Length == 0 || embedding.Any(x => !float.IsFinite(x))) return UnknownId;
            double norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
            if (norm < 0.0001) return UnknownId;
            float[] normalized = embedding.Select(x => (float)(x / norm)).ToArray();
            lock (_gate)
            {
                var allScores = _voices.Select(v => new { Voice = v, Score = Similarity(v.Embedding, normalized) })
                    .OrderByDescending(x => x.Score).ToArray();
                var scores = allScores.Where(x => excluded == null || !excluded.Contains(x.Voice.Identity.Id)).ToArray();
                bool distinctLocalVoice = allScores.Length > 0 && excluded != null && excluded.Contains(allScores[0].Voice.Identity.Id);
                if (scores.Length > 0 && scores[0].Score >= 0.60)
                {
                    if (scores.Length > 1 && scores[0].Score - scores[1].Score < 0.06) return UnknownId;
                    return scores[0].Voice.Identity.Id;
                }
                // Avoid inventing new identities for weak or ambiguous matches.
                if (!distinctLocalVoice && scores.Length > 0 && scores[0].Score > 0.50 || VoiceCount >= 16) return UnknownId;
                string id = NextId();
                string name = id.Replace("speaker-", "Speaker ");
                _voices.Add(new Voice { Identity = new SpeakerIdentity { Id = id, Name = name }, Embedding = normalized });
                _names[id] = name;
                return id;
            }
        }
        private int VoiceCount => _names.Keys.Count(x => x.StartsWith("speaker-", StringComparison.Ordinal));
        private string NextId()
        {
            int index = 1;
            while (_names.ContainsKey("speaker-" + index)) index++;
            return "speaker-" + index;
        }
        public void RestorePerson(string id, string name)
        {
            if (id != MicrophoneId && !System.Text.RegularExpressions.Regex.IsMatch(id ?? "", @"^speaker-\d+$")) return;
            lock (_gate) _names[id] = string.IsNullOrWhiteSpace(name) ? id.Replace("speaker-", "Speaker ") : name.Trim();
        }
        public string AddPerson(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter the person's name.");
            lock (_gate)
            {
                if (VoiceCount >= 16) throw new InvalidOperationException("This recording already has 16 speaker labels.");
                string id = NextId();
                _names[id] = name.Trim();
                return id;
            }
        }
        public void LearnVoice(string id, float[] embedding)
        {
            if (embedding == null || embedding.Length == 0 || embedding.Any(x => !float.IsFinite(x))) return;
            double norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
            if (norm < 0.0001) return;
            lock (_gate)
            {
                if (!_names.ContainsKey(id) || id == UnknownId || id == OverlapId) throw new ArgumentException("Choose a named person.");
                var existing = _voices.FirstOrDefault(x => x.Identity.Id == id);
                var normalized = embedding.Select(x => (float)(x / norm)).ToArray();
                if (existing != null) existing.Embedding = normalized;
                else _voices.Add(new Voice { Identity = new SpeakerIdentity { Id = id, Name = _names[id] }, Embedding = normalized });
            }
        }
        private static double Similarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return -1;
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }
    }
}
