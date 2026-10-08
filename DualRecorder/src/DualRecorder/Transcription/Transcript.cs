using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DualRecorder.Transcription
{
    public sealed class TranscriptEntry : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string Source { get; set; }
        public string SpeakerId { get; set; }
        private string _speakerName;
        public string SpeakerName
        {
            get => _speakerName;
            set { _speakerName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakerName))); }
        }
        public string Text { get; set; }
        public bool IsFinal { get; set; }
        public bool NeedsReview { get; set; }
        public string Time => TimeSpan.FromSeconds(Math.Max(0, Start)).ToString(@"hh\:mm\:ss");
        public string Status => !IsFinal ? "Live draft" : NeedsReview ? "Check speaker" : "Final";
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class TranscriptUpdate
    {
        public string RemoveId { get; set; }
        public TranscriptEntry[] Entries { get; set; } = Array.Empty<TranscriptEntry>();
    }

    public static class TranscriptExport
    {
        public static void Save(string basePath, IEnumerable<TranscriptEntry> entries, SpeakerRegistry speakers, string state)
        {
            var rows = entries.Where(x => x.IsFinal).OrderBy(x => x.Start).ThenBy(x => x.Source)
                .Select(x => new TranscriptEntry { Id = x.Id, Start = x.Start, End = x.End, Source = x.Source,
                    SpeakerId = x.SpeakerId, SpeakerName = speakers.Name(x.SpeakerId), Text = x.Text,
                    IsFinal = true, NeedsReview = x.NeedsReview }).ToArray();
            var text = new StringBuilder();
            text.AppendLine("Dual Recorder transcript");
            text.AppendLine("Status: " + state);
            text.AppendLine("Automatic English transcript. Review names, numbers and overlapping speech against the recording.");
            text.AppendLine();
            foreach (var row in rows)
            {
                text.AppendLine($"[{row.Time}] {row.SpeakerName} ({row.Source}){(row.NeedsReview ? " [check speaker]" : "")}: {row.Text}");
            }
            if (rows.Length == 0) text.AppendLine("No speech was transcribed.");
            WriteCompleted(basePath + "_transcript.txt", text.ToString());
            WriteCompleted(basePath + "_transcript.json", JsonSerializer.Serialize(new
            {
                status = state,
                language = "English",
                speakers = speakers.Identities,
                segments = rows
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        private static void WriteCompleted(string destination, string content)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "DualRecorder-transcript-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                File.Copy(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
