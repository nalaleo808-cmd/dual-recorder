using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SherpaOnnx;

namespace DualRecorder.Transcription
{
    public sealed class SpeakerSegmenter : IDisposable
    {
        private readonly OfflineSpeakerDiarization _diarizer;
        private readonly SpeakerEmbeddingExtractor _extractor;
        private readonly SpeakerRegistry _speakers;

        public SpeakerSegmenter(string modelFolder, SpeakerRegistry speakers)
        {
            _speakers = speakers;
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = System.IO.Path.Combine(modelFolder, "segmentation.onnx");
            config.Segmentation.NumThreads = 1;
            config.Embedding.Model = System.IO.Path.Combine(modelFolder, "embedding.onnx");
            config.Embedding.NumThreads = 1;
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = 0.5f;
            _diarizer = new OfflineSpeakerDiarization(config);
            var embedding = new SpeakerEmbeddingExtractorConfig();
            embedding.Model = config.Embedding.Model;
            embedding.NumThreads = 1;
            _extractor = new SpeakerEmbeddingExtractor(embedding);
        }

        public TranscriptEntry[] Split(string id, string source, double offset, double duration,
            float[] audio, string text, string[] tokens, float[] timestamps, bool microphoneIsOnlyMe)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TranscriptEntry>();
            if (microphoneIsOnlyMe)
                return new[] { Entry(id, source, offset, offset + duration, SpeakerRegistry.MicrophoneId, text, false) };

            var segments = audio.Length >= 16000 ? _diarizer.Process(audio) : Array.Empty<OfflineSpeakerDiarizationSegment>();
            var identities = new Dictionary<int, string>();
            var assigned = new HashSet<string>();
            foreach (var group in segments.GroupBy(x => x.Speaker))
            {
                var samples = new List<float>();
                // Use non-overlapping speech to avoid enrolling a mixture of voices.
                foreach (var segment in group)
                {
                    int begin = Math.Max(0, (int)(segment.Start * 16000));
                    int end = Math.Min(audio.Length, (int)(segment.End * 16000));
                    for (int i = begin; i < end && samples.Count < 8 * 16000; i++)
                    {
                        double t = i / 16000.0;
                        if (!segments.Any(other => other.Speaker != segment.Speaker && t >= other.Start && t < other.End))
                            samples.Add(audio[i]);
                    }
                }
                string speaker = SpeakerRegistry.UnknownId;
                if (samples.Count >= 24000)
                {
                    using var stream = _extractor.CreateStream();
                    stream.AcceptWaveform(16000, samples.ToArray());
                    stream.InputFinished();
                    if (_extractor.IsReady(stream)) speaker = _speakers.MatchOrAdd(_extractor.Compute(stream), assigned);
                }
                identities[group.Key] = speaker;
                if (speaker != SpeakerRegistry.UnknownId) assigned.Add(speaker);
            }

            var words = TokenWords(tokens, timestamps, duration);
            if (words.Count == 0)
            {
                var labels = identities.Values.Distinct().ToArray();
                string label = labels.Length == 1 ? labels[0] : SpeakerRegistry.UnknownId;
                return new[] { Entry(id, source, offset, offset + duration, label, text, true) };
            }
            var answer = new List<TranscriptEntry>();
            string previousSpeaker = null;
            foreach (var word in words)
            {
                double at = Math.Min(duration, word.Start + 0.03);
                var hits = segments.Where(x => at >= x.Start && at < x.End).ToArray();
                if (hits.Length == 0)
                {
                    var nearest = segments.Select(x => new { Span = x, Distance = Math.Max(x.Start - at, Math.Max(0, at - x.End)) })
                        .OrderBy(x => x.Distance).ToArray();
                    if (nearest.Length > 0 && nearest[0].Distance <= 0.25)
                    {
                        double limit = nearest[0].Distance + 0.04;
                        hits = nearest.Where(x => x.Distance <= limit).Select(x => x.Span).ToArray();
                    }
                }
                int[] clusterIds = hits.Select(x => x.Speaker).Distinct().ToArray();
                string speaker = clusterIds.Length > 1 ? SpeakerRegistry.OverlapId :
                    clusterIds.Length == 1 && identities.TryGetValue(clusterIds[0], out var found) ? found : SpeakerRegistry.UnknownId;
                if (speaker != previousSpeaker)
                {
                    answer.Add(Entry(id + "-" + answer.Count, source, offset + word.Start, offset + word.End,
                        speaker, word.Text, speaker == SpeakerRegistry.UnknownId || speaker == SpeakerRegistry.OverlapId));
                    previousSpeaker = speaker;
                }
                else
                {
                    var current = answer[answer.Count - 1];
                    current.Text += " " + word.Text;
                    current.End = offset + word.End;
                }
            }
            return answer.ToArray();
        }

        private TranscriptEntry Entry(string id, string source, double start, double end, string speaker, string text, bool review)
            => new TranscriptEntry { Id = id, Source = source, Start = start, End = end,
                SpeakerId = speaker, SpeakerName = _speakers.Name(speaker), Text = text.Trim(), IsFinal = true, NeedsReview = review };

        public sealed record Word(string Text, double Start, double End);
        public static List<Word> TokenWords(string[] tokens, float[] timestamps, double duration)
        {
            var words = new List<Word>();
            if (tokens == null || timestamps == null || tokens.Length != timestamps.Length) return words;
            var text = new StringBuilder();
            double start = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Replace("\u2581", " ");
                double at = Math.Clamp(timestamps[i], 0, Math.Max(0, duration));
                if (token.StartsWith(" ", StringComparison.Ordinal) && text.Length > 0)
                {
                    words.Add(new Word(text.ToString().Trim(), start, Math.Max(start, Math.Min(at, start + 0.6))));
                    text.Clear();
                }
                if (text.Length == 0) start = at;
                text.Append(token.TrimStart());
            }
            if (text.Length > 0) words.Add(new Word(text.ToString().Trim(), start, Math.Min(duration, start + 0.35)));
            return words.Where(x => x.Text.Length > 0).ToList();
        }
        public void Dispose() { _diarizer.Dispose(); _extractor.Dispose(); }
    }
}
