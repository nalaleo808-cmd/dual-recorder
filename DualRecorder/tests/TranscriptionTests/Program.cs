using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DualRecorder;
using DualRecorder.Core;
using DualRecorder.Transcription;
using SherpaOnnx;

internal static class Program
{
    private static string _scratch;
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            _scratch = args.Length >= 2 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "DualRecorder-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            if (args.Length > 0 && args[0] == "--diagnose") { Diagnose(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--render") { Render(); return 0; }
            CheckGrowingFile();
            CheckSpeakersAndExport();
            CheckTokens();
            CheckNamingControls();
            if (args.Length > 0) NativeTests(Path.GetFullPath(args[0]));
            Console.WriteLine($"PASS: {_checks} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Assert(bool result, string description)
    {
        if (!result) throw new Exception("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
    private static void CheckGrowingFile()
    {
        string path = Path.Combine(_scratch, "growing.wav");
        using (var writer = new WavWriterSafe(path, 48000, 2))
        using (var reader = new GrowingWaveReader(path))
        {
            Assert(reader.ReadBlock().Length == 0, "A buffered empty header waits safely");
            var samples = Enumerable.Repeat(0.25f, 600).ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
            writer.UpdateHeader();
            var block = reader.ReadBlock();
            Assert(block.Length == 100 && block.All(x => Math.Abs(x - 0.25) < 0.001), "Growing stereo WAV produces the correct 16 kHz samples");
            Assert(reader.ReadBlock().Length == 0, "Already consumed audio is never repeated");
            writer.WriteSamples(new[] { 0.5f, 0.5f }, 0, 2);
            writer.UpdateHeader();
            Assert(reader.ReadBlock().Length == 0, "An incomplete resampling group waits for more frames");
            Assert(reader.ReadBlock(final: true).Length == 1, "Stop drains a final short frame group");
        }
        string bad = Path.Combine(_scratch, "unsupported.wav");
        File.WriteAllBytes(bad, new byte[44]);
        bool rejected = false;
        try { using var reader = new GrowingWaveReader(bad); reader.ReadBlock(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Invalid audio headers are rejected without affecting recordings");
    }
    private static void CheckSpeakersAndExport()
    {
        var speakers = new SpeakerRegistry("Tony");
        string first = speakers.MatchOrAdd(new float[] { 1, 0, 0 });
        string second = speakers.MatchOrAdd(new float[] { 0, 1, 0 });
        Assert(first != second && first == speakers.MatchOrAdd(new float[] { 0.99f, 0.01f, 0 }), "Same and distinct voices retain separate stable IDs");
        Assert(speakers.MatchOrAdd(new float[] { 0.7f, 0.7f, 0 }) == SpeakerRegistry.UnknownId, "Ambiguous voice matches remain unknown");
        Assert(speakers.MatchOrAdd(new float[] { float.NaN, 0, 0 }) == SpeakerRegistry.UnknownId, "Invalid voice embeddings are never named");
        var localClusters = new SpeakerRegistry();
        string clusterA = localClusters.MatchOrAdd(new float[] { 1, 0 });
        string clusterB = localClusters.MatchOrAdd(new float[] { 0.8f, 0.6f }, new HashSet<string> { clusterA });
        Assert(clusterA != clusterB, "Distinct voices in one diarization result cannot collapse to the same global identity");
        string person = localClusters.AddPerson("Casey");
        localClusters.LearnVoice(person, new float[] { 0, 1 });
        Assert(localClusters.MatchOrAdd(new float[] { 0, 1 }) == person, "An added person can be identified from a user-assigned voice reference");
        speakers.Rename(first, "Mike");
        var rows = new[] {
            new TranscriptEntry { Id="a", Start=2, End=4, Source="Computer audio", SpeakerId=first, Text="First speaker", IsFinal=true },
            new TranscriptEntry { Id="b", Start=1, End=3, Source="Microphone", SpeakerId=SpeakerRegistry.MicrophoneId, Text="Overlapping microphone speech", IsFinal=true },
            new TranscriptEntry { Id="c", Start=5, End=6, Source="Computer audio", SpeakerId=second, Text="Unfinished draft", IsFinal=false }
        };
        string prefix = Path.Combine(_scratch, "export");
        TranscriptExport.Save(prefix, rows, speakers, "Complete");
        string text = File.ReadAllText(prefix + "_transcript.txt");
        Assert(text.Contains("Mike") && text.Contains("Tony"), "Renaming applies to the saved transcript");
        Assert(text.IndexOf("Overlapping microphone speech") < text.IndexOf("First speaker") && !text.Contains("Unfinished draft"), "Transcript preserves overlapping tracks in timestamp order and excludes live drafts");
        Assert(!File.ReadAllText(prefix + "_transcript.json").Contains("Embedding"), "Export contains no voice embeddings");
    }
    private static void CheckTokens()
    {
        var words = SpeakerSegmenter.TokenWords(new[] { "\u2581HEL", "LO", "\u2581WORLD" }, new[] { 0.5f, 0.6f, 1.2f }, 2);
        Assert(words.Count == 2 && words[0].Text == "HELLO" && words[1].Text == "WORLD", "Subword timestamps preserve whole words at speaker boundaries");
    }
    private static void CheckNamingControls()
    {
        if (Application.Current == null) { var app = new DualRecorder.App(); app.InitializeComponent(); }
        var window = new MainWindow(true);
        var speakers = new SpeakerRegistry();
        string first = speakers.MatchOrAdd(new float[] { 1, 0, 0 });
        window.RefreshSpeakerRows(speakers);
        var table = (DataGrid)window.FindName("SpeakerGrid");
        var chooser = (ComboBox)window.FindName("SpeakerAssignCombo");
        var source = table.ItemsSource;
        var firstRow = table.Items.Cast<SpeakerIdentity>().First(x => x.Id == first);
        firstRow.Name = "Alex";
        speakers.Rename(first, "Alex");
        string second = speakers.MatchOrAdd(new float[] { 0, 1, 0 });
        window.RefreshSpeakerRows(speakers);
        var secondRow = table.Items.Cast<SpeakerIdentity>().First(x => x.Id == second);
        secondRow.Name = "Casey";
        chooser.SelectedValue = second;
        for (int i=0;i<50;i++) window.RefreshSpeakerRows(speakers);
        Assert(ReferenceEquals(source,table.ItemsSource) && ReferenceEquals(firstRow,table.Items.Cast<SpeakerIdentity>().First(x=>x.Id==first)),
            "Live updates retain the people table and its existing speaker rows");
        Assert(firstRow.Name == "Alex" && secondRow.Name == "Casey" && (string)chooser.SelectedValue == second,
            "A second speaker can be named and selected after the first without live updates resetting the choice");
        string third = speakers.AddPerson("Jordan");
        window.RefreshSpeakerRows(speakers);
        Assert(table.Items.Cast<SpeakerIdentity>().Any(x => x.Id == third && x.Name == "Jordan"), "A missed person can be added to the naming controls");
    }
    private static void NativeTests(string models)
    {
        Assert(SpeechModels.Verify(models), "All six model files match their pinned SHA-256 hashes");
        var sample = new TestWave(Path.Combine(models, "test-asr.wav"));
        Assert(sample.SampleRate == 16000, "Public streaming sample is 16 kHz");
        string prefix = Path.Combine(_scratch, "live");
        int liveUpdates = 0;
        var statuses = new List<string>();
        var session = new LiveTranscriptionSession(prefix + "_mic-only.wav", prefix + "_system-only.wav", models, "Tony", true);
        var timer = Stopwatch.StartNew();
        using (var mic = new WavWriterSafe(prefix + "_mic-only.wav", 48000, 2))
        using (var system = new WavWriterSafe(prefix + "_system-only.wav", 48000, 2))
        {
            mic.UpdateHeader(); system.UpdateHeader();
            session.Updated += x => { if (x.Entries.Any(e => !e.IsFinal)) Interlocked.Increment(ref liveUpdates); };
            session.StatusChanged += x => { lock(statuses) statuses.Add(x); };
            session.Start();
            for (int offset = 0; offset < sample.Samples.Length; offset += 1600)
            {
                var block = sample.Samples.Skip(offset).Take(1600).ToArray();
                var stereo = Stereo48(block);
                mic.WriteSamples(stereo, 0, stereo.Length);
                system.WriteSamples(new float[stereo.Length], 0, stereo.Length);
                mic.UpdateHeader(); system.UpdateHeader();
                Thread.Sleep(100);
            }
            Assert(liveUpdates > 0, "Streaming text appears before Stop");
        }
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(prefix + "_mic-only.wav"));
        session.RequestStop();
        Assert(session.Completion.Wait(TimeSpan.FromSeconds(30)), "Stop drains and saves the final transcript");
        string transcript = File.ReadAllText(session.TranscriptPath);
        Console.WriteLine(transcript);
        Assert(transcript.Contains("AFTER EARLY NIGHTFALL", StringComparison.OrdinalIgnoreCase) && transcript.Contains("BROTHELS", StringComparison.OrdinalIgnoreCase), "Native streaming transcript retains the public sample's first and last words");
        Assert(!transcript.Contains("Status: Incomplete") && !transcript.Contains("Live draft"), "Final export is complete and has no provisional rows");
        Assert(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(prefix + "_mic-only.wav"))), "Transcription leaves source audio byte-identical");
        Console.WriteLine($"Streaming fixture elapsed: {timer.Elapsed.TotalSeconds:0.0} seconds for {sample.Samples.Length / 16000.0:0.0} seconds of speech");

        var speech = new TestWave(Path.Combine(models, "test-speakers.wav"));
        using (var segmenter = new SpeakerSegmenter(models, new SpeakerRegistry()))
        {
            var parts = segmenter.Split("two", "Computer audio", 0, speech.Samples.Length / 16000.0,
                speech.Samples, "Two-speaker public fixture", Array.Empty<string>(), Array.Empty<float>(), false);
            // The registry is checked separately below because the no-token fallback is intentionally uncertain.
            Assert(parts.Length > 0 && parts.All(x => x.NeedsReview), "Missing token timing cannot produce a falsely certain speaker label");
        }
        var registry = new SpeakerRegistry();
        using (var segmenter = new SpeakerSegmenter(models, registry))
        {
            segmenter.Split("voices", "Computer audio", 0, speech.Samples.Length / 16000.0,
                speech.Samples, "Public fixture", Array.Empty<string>(), Array.Empty<float>(), false);
            Assert(registry.Identities.Count(x => x.Id.StartsWith("speaker-")) >= 2, "Native diarization detects distinct voices in the public two-speaker recording");
            foreach (var speaker in registry.Identities) Console.WriteLine(speaker.Id + ": " + speaker.Name);
        }
        string voicePrefix = Path.Combine(_scratch, "live-voices");
        var liveSpeakerIds = new HashSet<string>();
        var voices = new LiveTranscriptionSession(voicePrefix + "_mic-only.wav", voicePrefix + "_system-only.wav", models, "You", true);
        using (var mic = new WavWriterSafe(voicePrefix + "_mic-only.wav", 48000, 2))
        using (var system = new WavWriterSafe(voicePrefix + "_system-only.wav", 48000, 2))
        {
            mic.UpdateHeader(); system.UpdateHeader();
            voices.Updated += update => { lock (liveSpeakerIds) foreach (var row in update.Entries)
                if (row.SpeakerId.StartsWith("speaker-")) liveSpeakerIds.Add(row.SpeakerId); };
            voices.Start();
            for (int offset = 0; offset < speech.Samples.Length; offset += 1600)
            {
                var stereo = Stereo48(speech.Samples.Skip(offset).Take(1600).ToArray());
                mic.WriteSamples(new float[stereo.Length], 0, stereo.Length);
                system.WriteSamples(stereo, 0, stereo.Length);
                mic.UpdateHeader(); system.UpdateHeader();
                Thread.Sleep(100);
            }
            lock (liveSpeakerIds) Assert(liveSpeakerIds.Count >= 2, "Both detected speaker labels appear before Stop");
        }
        voices.RequestStop();
        Assert(voices.Completion.Wait(TimeSpan.FromSeconds(30)), "Live two-speaker recording finishes safely");
        foreach(var row in voices.Entries) Console.WriteLine($"{row.Time} {row.SpeakerId}: {row.Text}");
        Assert(voices.Entries.Where(x=>x.SpeakerId.StartsWith("speaker-")).Select(x=>x.SpeakerId).Distinct().Count() >= 2,
            "Live transcription preserves two distinct speaker identities in the saved result");
        string speechText = string.Join(" ",voices.Entries.Select(x=>x.Text));
        Assert(speechText.Contains("clothes and lodging",StringComparison.OrdinalIgnoreCase) && speechText.Contains("glow deepened",StringComparison.OrdinalIgnoreCase),
            "Updated live model retains the quieter speaker's words that the original model missed");
        var corrected = voices.Entries.First(x=>x.IsFinal);
        string manual = voices.Speakers.AddPerson("Jordan");
        voices.AssignSpeaker(new[] { corrected.Id },manual);
        voices.CorrectText(corrected.Id,"User corrected this sentence.");
        string revised = File.ReadAllText(voices.TranscriptPath);
        Assert(revised.Contains("Jordan") && revised.Contains("User corrected this sentence."), "Speaker reassignment and word corrections persist in the saved transcript");
        Assert(File.ReadAllText(voicePrefix+"_transcript.json").Contains("\"UserEdited\": true"), "The export records which transcript lines the user corrected");
        var reloaded = LiveTranscriptionSession.LoadSaved(voicePrefix+"_transcript.json",models);
        Assert(reloaded.Entries.Any(x=>x.Text=="User corrected this sentence." && reloaded.Speakers.Name(x.SpeakerId)=="Jordan"),
            "A saved transcript reopens with its corrected words and named people intact");
        // Pause/resume preserves the recording clock by using file offsets, rather than wall-clock time.
        string pausePrefix = Path.Combine(_scratch, "pause");
        using var pauseMic = new WavWriterSafe(pausePrefix + "_mic-only.wav", 48000, 2);
        using var pauseSys = new WavWriterSafe(pausePrefix + "_system-only.wav", 48000, 2);
        pauseMic.UpdateHeader(); pauseSys.UpdateHeader();
        var paused = new LiveTranscriptionSession(pausePrefix + "_mic-only.wav", pausePrefix + "_system-only.wav", models, "Tony", true);
        paused.Start();
        var full = Stereo48(sample.Samples);
        pauseMic.WriteSamples(full, 0, full.Length); pauseSys.WriteSamples(new float[full.Length], 0, full.Length);
        pauseMic.UpdateHeader(); pauseSys.UpdateHeader();
        paused.SetPaused(true);
        Thread.Sleep(2000);
        paused.SetPaused(false);
        pauseMic.WriteSamples(full, 0, full.Length); pauseSys.WriteSamples(new float[full.Length], 0, full.Length);
        pauseMic.UpdateHeader(); pauseSys.UpdateHeader();
        pauseMic.Dispose(); pauseSys.Dispose();
        paused.RequestStop();
        Assert(paused.Completion.Wait(TimeSpan.FromSeconds(30)), "Pause and resume followed by Stop complete safely");
        Assert(paused.Entries.Length >= 2 && paused.Entries.All(x => x.End <= sample.Samples.Length / 16000.0 * 2 + 0.01), "Transcript timestamps exclude the wall-clock pause");
    }
    private sealed class TestWave
    {
        public int SampleRate { get; }
        public float[] Samples { get; }
        public TestWave(string path)
        {
            using var reader = new NAudio.Wave.AudioFileReader(path);
            if (reader.WaveFormat.Channels != 1) throw new InvalidDataException("Public test fixture must be mono.");
            SampleRate = reader.WaveFormat.SampleRate;
            var samples = new List<float>();
            var block = new float[16000];
            int n;
            while ((n = reader.Read(block, 0, block.Length)) > 0) samples.AddRange(block.Take(n));
            Samples = samples.ToArray();
        }
    }
    private static float[] Stereo48(float[] mono)
    {
        float[] result = new float[mono.Length * 6];
        for (int i = 0; i < mono.Length; i++) for (int j = 0; j < 6; j++) result[i * 6 + j] = mono[i];
        return result;
    }
    private static void Diagnose(string models)
    {
        var audio = new TestWave(Path.Combine(models, "test-speakers.wav"));
        foreach (float threshold in new[] { 0.3f, 0.4f, 0.5f, 0.6f, 0.7f })
        {
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = Path.Combine(models, "segmentation.onnx");
            config.Embedding.Model = Path.Combine(models, "embedding.onnx");
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = threshold;
            using var sd = new OfflineSpeakerDiarization(config);
            var spans = sd.Process(audio.Samples);
            Console.WriteLine($"Threshold {threshold}: {spans.Select(x=>x.Speaker).Distinct().Count()} speakers, {spans.Length} spans");
            foreach (var span in spans) Console.WriteLine($"  {span.Start:0.00}-{span.End:0.00}: {span.Speaker}");
            var ec = new SpeakerEmbeddingExtractorConfig(); ec.Model = config.Embedding.Model;
            using var extractor = new SpeakerEmbeddingExtractor(ec);
            var embeddings = new List<float[]>();
            foreach (var group in spans.GroupBy(x=>x.Speaker))
            {
                var samples = group.SelectMany(s=>audio.Samples.Skip((int)(s.Start*16000)).Take((int)((s.End-s.Start)*16000))).ToArray();
                using var stream = extractor.CreateStream(); stream.AcceptWaveform(16000,samples); stream.InputFinished();
                if (extractor.IsReady(stream)) embeddings.Add(extractor.Compute(stream));
            }
            for(int i=0;i<embeddings.Count;i++) for(int j=0;j<i;j++)
            {
                var a=embeddings[i]; var b=embeddings[j];
                double cosine = a.Zip(b,(x,y)=>(double)x*y).Sum()/Math.Sqrt(a.Sum(x=>(double)x*x)*b.Sum(x=>(double)x*x));
                Console.WriteLine($"  Voice cosine {i}/{j}: {cosine:0.000}");
            }
        }
    }
    private static void Render()
    {
        var application = new DualRecorder.App();
        application.InitializeComponent();
        var window = new MainWindow(true);
        ((ComboBox)window.FindName("MicCombo")).Items.Add("Your microphone");
        ((ComboBox)window.FindName("MicCombo")).SelectedIndex = 0;
        ((ComboBox)window.FindName("SysCombo")).Items.Add("Your speakers / headset");
        ((ComboBox)window.FindName("SysCombo")).SelectedIndex = 0;
        ((TextBlock)window.FindName("TranscriptStatus")).Text = "Live transcript - processed on this PC";
        ((DataGrid)window.FindName("TranscriptList")).ItemsSource = new[] {
            new TranscriptEntry { Start=4, Source="Microphone", SpeakerName="You", Text="Can we go over the plan for next week?", IsFinal=true },
            new TranscriptEntry { Start=7, Source="Computer audio", SpeakerName="Mike", Text="Yes. I will send the updated numbers this afternoon.", IsFinal=true },
            new TranscriptEntry { Start=12, Source="Computer audio", SpeakerName="Speaker 2", Text="I have one question about the timing.", IsFinal=false }
        };
        var surface = (FrameworkElement)window.Content;
        var previewSpeakers = new SpeakerRegistry();
        string previewA = previewSpeakers.MatchOrAdd(new float[] {1,0});
        previewSpeakers.Rename(previewA,"Alex");
        string previewB = previewSpeakers.MatchOrAdd(new float[] {0,1});
        previewSpeakers.Rename(previewB,"Casey");
        window.RefreshSpeakerRows(previewSpeakers);
        surface.Measure(new Size(1220,880)); surface.Arrange(new Rect(0,0,1220,880)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1220,880,96,96,PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_scratch, "app-preview.png")); encoder.Save(output);
        Console.WriteLine("Saved app preview.");
    }
}
