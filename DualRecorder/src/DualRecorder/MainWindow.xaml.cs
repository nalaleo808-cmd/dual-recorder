using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using DualRecorder.Transcription;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DualRecorder.Audio;
using DualRecorder.Interop;
using WinForms = System.Windows.Forms;

namespace DualRecorder
{
    public partial class MainWindow : Window
    {
        private readonly RecordingEngine _engine = new RecordingEngine();
        private readonly GlobalHotkey _hotkey = new GlobalHotkey();
        private readonly DispatcherTimer _ui = new DispatcherTimer();
        private readonly Settings _settings;
        private readonly bool _preview;

        private WinForms.NotifyIcon _tray;
        private double _micLevel, _sysLevel;
        private bool _shuttingDown;

        private const uint VK_R = 0x52;

        public MainWindow() : this(false) { }

        public MainWindow(bool preview)
        {
            _preview = preview;
            _settings = preview ? new Settings { OutputFolder = "Recordings", MicrophoneName = "You" } : Settings.Load();
            InitializeComponent();
            TranscriptCheck.IsChecked = _settings.LiveTranscription;
            OnlyMeCheck.IsChecked = _settings.MicrophoneIsOnlyMe;
            MicrophoneNameBox.Text = _settings.MicrophoneName;
            TranscriptList.ItemsSource = _transcriptRows;
            if (SpeechModels.Available) TranscriptStatus.Text = "Speech files are ready. Start recording to see your transcript.";

            FolderBox.Text = _settings.OutputFolder;
            Mp3Check.IsChecked = _settings.ExportMp3;
            SelectBitrate(_settings.Mp3BitRate);

            _engine.Notice += OnEngineNotice;

            _ui.Interval = TimeSpan.FromMilliseconds(50);
            _ui.Tick += OnUiTick;
            if (!preview) _ui.Start();

            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += OnStateChanged;
        }

        private readonly ObservableCollection<TranscriptEntry> _transcriptRows = new ObservableCollection<TranscriptEntry>();
        private LiveTranscriptionSession _transcription;
        private bool _finishingTranscript, _settingUpSpeech, _closingAfterTranscription;

        private async void ModelsButton_Click(object sender, RoutedEventArgs e)
        {
            _settingUpSpeech = true;
            UpdateButtons();
            try
            {
                await SpeechModels.DownloadAsync(new Progress<string>(text => TranscriptStatus.Text = text), CancellationToken.None);
                SaveSettings();
            }
            catch (Exception ex) { TranscriptStatus.Text = "Speech setup failed: " + ex.Message + ". You can still record audio."; }
            finally { _settingUpSpeech = false; UpdateButtons(); }
        }

        private void BeginTranscription(RecordingResult recording)
        {
            _transcription = null;
            _transcriptRows.Clear();
            SpeakerCombo.ItemsSource = null;
            SaveTranscriptButton.IsEnabled = false;
            OpenTranscriptButton.IsEnabled = false;
            if (TranscriptCheck.IsChecked != true) { TranscriptStatus.Text = "Transcription is off for this recording."; return; }
            if (!SpeechModels.Available) { TranscriptStatus.Text = "Recording audio. Set up speech files before the next recording to enable live transcription."; return; }
            var session = new LiveTranscriptionSession(recording.MicPath, recording.SystemPath, SpeechModels.Folder,
                MicrophoneNameBox.Text, OnlyMeCheck.IsChecked == true);
            _transcription = session;
            session.Updated += update => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_transcription != session) return;
                var removed = _transcriptRows.FirstOrDefault(x => x.Id == update.RemoveId);
                if (removed != null) _transcriptRows.Remove(removed);
                foreach (var entry in update.Entries)
                {
                    int at = 0;
                    while (at < _transcriptRows.Count && _transcriptRows[at].Start <= entry.Start) at++;
                    _transcriptRows.Insert(at, entry);
                }
                string selected = SpeakerCombo.SelectedValue as string;
                SpeakerCombo.ItemsSource = session.Speakers.Identities;
                if (selected != null) SpeakerCombo.SelectedValue = selected;
                else if (SpeakerCombo.Items.Count > 0) SpeakerCombo.SelectedIndex = 0;
                SaveTranscriptButton.IsEnabled = true;
                if (_transcriptRows.Count > 0) TranscriptList.ScrollIntoView(_transcriptRows[_transcriptRows.Count - 1]);
            }));
            session.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_transcription == session) TranscriptStatus.Text = status;
                if (status.StartsWith("Transcription stopped:", StringComparison.Ordinal)) Log(status);
            }));
            session.Start();
        }

        private async void FinishTranscription()
        {
            var session = _transcription;
            if (session == null) return;
            _finishingTranscript = true;
            UpdateButtons();
            session.RequestStop();
            await session.Completion;
            _finishingTranscript = false;
            if (_transcription == session)
            {
                SaveTranscriptButton.IsEnabled = true;
                OpenTranscriptButton.IsEnabled = File.Exists(session.TranscriptPath);
            }
            UpdateButtons();
        }

        private void RenameSpeaker_Click(object sender, RoutedEventArgs e)
        {
            var selected = SpeakerCombo.SelectedItem as SpeakerIdentity;
            if (_transcription == null || selected == null) { TranscriptStatus.Text = "Choose a detected speaker first."; return; }
            try
            {
                _transcription.Speakers.Rename(selected.Id, SpeakerNameBox.Text);
                foreach (var row in _transcriptRows.Where(x => x.SpeakerId == selected.Id))
                    row.SpeakerName = _transcription.Speakers.Name(selected.Id);
                SpeakerCombo.ItemsSource = _transcription.Speakers.Identities;
                SpeakerCombo.SelectedValue = selected.Id;
                if (_transcription.Finished) _transcription.Save();
                TranscriptStatus.Text = "Speaker renamed to " + _transcription.Speakers.Name(selected.Id) + ".";
                SpeakerNameBox.Clear();
            }
            catch (Exception ex) { TranscriptStatus.Text = ex.Message; }
        }

        private void SaveTranscript_Click(object sender, RoutedEventArgs e)
        {
            if (_transcription == null) return;
            try
            {
                _transcription.Save();
                OpenTranscriptButton.IsEnabled = true;
                TranscriptStatus.Text = "Transcript saved: " + _transcription.TranscriptPath;
            }
            catch (Exception ex) { TranscriptStatus.Text = "Could not save the transcript: " + ex.Message; }
        }

        private void OpenTranscript_Click(object sender, RoutedEventArgs e)
        {
            if (_transcription == null || !File.Exists(_transcription.TranscriptPath)) return;
            try { Process.Start(new ProcessStartInfo(_transcription.TranscriptPath) { UseShellExecute = true }); }
            catch (Exception ex) { TranscriptStatus.Text = "Could not open the transcript: " + ex.Message; }
        }
        // ---------- startup ----------

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_preview) return;
            LoadDevices();
            SetUpTray();

            bool ok = _hotkey.Register(this, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_SHIFT, VK_R);
            _hotkey.Pressed += ToggleRecording;
            HotkeyText.Text = ok
                ? "Hotkey: Ctrl + Shift + R"
                : "Hotkey Ctrl + Shift + R is taken by another program";

            Log("Ready. Recordings are saved as 48 kHz stereo 16-bit WAV.");
        }

        private void LoadDevices()
        {
            try
            {
                var ins = AudioDevices.Inputs();
                var outs = AudioDevices.Outputs();
                MicCombo.ItemsSource = ins;
                SysCombo.ItemsSource = outs;
                MicCombo.SelectedItem = Pick(ins, _settings.MicDeviceId);
                SysCombo.SelectedItem = Pick(outs, _settings.RenderDeviceId);

                if (ins.Count == 0) Log("No active microphone found. Plug one in and press Refresh.");
                if (outs.Count == 0) Log("No active playback device found, so system audio cannot be recorded.");
            }
            catch (Exception ex)
            {
                Log("Could not list audio devices: " + ex.Message);
            }
        }

        private static DeviceEntry Pick(List<DeviceEntry> list, string savedId)
        {
            if (list.Count == 0) return null;
            if (!string.IsNullOrEmpty(savedId))
                foreach (var d in list) if (d.Id == savedId) return d;
            foreach (var d in list) if (d.IsDefault) return d;   // Windows default
            return list[0];
        }

        private void SetUpTray()
        {
            try
            {
                _tray = new WinForms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Visible = true,
                    Text = "Dual Recorder"
                };
                _tray.DoubleClick += (s, e) => RestoreFromTray();

                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("Show window", null, (s, e) => RestoreFromTray());
                menu.Items.Add("Start / Stop  (Ctrl+Shift+R)", null, (s, e) => Dispatcher.Invoke(ToggleRecording));
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => Dispatcher.Invoke(Close));
                _tray.ContextMenuStrip = menu;
            }
            catch (Exception ex)
            {
                Log("System tray icon unavailable: " + ex.Message);
            }
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _tray != null)
            {
                Hide();
                try
                {
                    _tray.ShowBalloonTip(1500, "Dual Recorder",
                        _engine.State == RecorderState.Recording ? "Still recording. Ctrl+Shift+R to stop."
                                                                 : "Running in the tray. Ctrl+Shift+R to record.",
                        WinForms.ToolTipIcon.Info);
                }
                catch { }
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // ---------- transport ----------

        private void ToggleRecording()
        {
            if (_engine.State == RecorderState.Idle) StartRecording();
            else StopRecording();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e) => StartRecording();
        private void StopButton_Click(object sender, RoutedEventArgs e) => StopRecording();

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.State == RecorderState.Recording)
            {
                _engine.Pause();
                _transcription?.SetPaused(true);
                PauseButton.Content = "Resume";
                StateText.Text = "Paused";
                Log("Paused.");
            }
            else if (_engine.State == RecorderState.Paused)
            {
                _engine.Resume();
                _transcription?.SetPaused(false);
                PauseButton.Content = "Pause";
                StateText.Text = "Recording";
                Log("Resumed.");
            }
            UpdateButtons();
        }

        private void StartRecording()
        {
            if (_engine.State != RecorderState.Idle || _finishingTranscript || _settingUpSpeech) return;

            string folder = FolderBox.Text.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                MessageBox.Show(this, "Pick a folder to save the recordings in.", "Dual Recorder");
                return;
            }

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "That folder cannot be used: " + ex.Message, "Dual Recorder");
                return;
            }

            var mic = MicCombo.SelectedItem as DeviceEntry;
            var sys = SysCombo.SelectedItem as DeviceEntry;

            try
            {
                var r = _engine.Start(mic?.Id, sys?.Id, folder);
                foreach (var w in r.Warnings) Log(w);
                BeginTranscription(r);
                Log("Recording to " + folder);
                StateText.Text = "Recording";
                PauseButton.Content = "Pause";
                if (_tray != null) _tray.Text = "Dual Recorder - recording";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start recording: " + ex.Message, "Dual Recorder");
                Log("Start failed: " + ex.Message);
            }

            SaveSettings();
            UpdateButtons();
        }

        private void StopRecording()
        {
            if (_engine.State == RecorderState.Idle) return;

            RecordingResult r;
            try { r = _engine.Stop(); }
            catch (Exception ex) { Log("Stop failed: " + ex.Message); UpdateButtons(); return; }
            if (r == null) { UpdateButtons(); return; }

            StateText.Text = "Ready";
            if (_tray != null) _tray.Text = "Dual Recorder";
            UpdateButtons();

            Log("Saved " + r.Duration.ToString(@"hh\:mm\:ss") + ":");
            Log("   " + Path.GetFileName(r.MicPath));
            Log("   " + Path.GetFileName(r.SystemPath));
            Log("   " + Path.GetFileName(r.MixedPath));
            if (r.SystemSilenceFrames > 0)
                Log("   " + Seconds(r.SystemSilenceFrames) + " of silence was inserted into the system track to keep it in sync.");
            if (r.MicSilenceFrames > 0)
                Log("   " + Seconds(r.MicSilenceFrames) + " of silence was inserted into the mic track to keep it in sync.");
            foreach (var w in r.Warnings) Log("   " + w);

            if (Mp3Check.IsChecked == true) ExportMp3(r);
            FinishTranscription();
            SaveSettings();
        }

        private static string Seconds(long frames)
            => (frames / (double)RecordingEngine.TargetRate).ToString("0.0") + " s";

        private void ExportMp3(RecordingResult r)
        {
            int bitrate = SelectedBitrate();
            Log("Exporting MP3 at " + bitrate + " kbps...");
            var paths = new[] { r.MicPath, r.SystemPath, r.MixedPath };

            Task.Run(() =>
            {
                foreach (var wav in paths)
                {
                    string err;
                    string mp3 = Mp3Exporter.Encode(wav, bitrate, out err);
                    string line = mp3 != null
                        ? "   " + Path.GetFileName(mp3)
                        : "   MP3 export failed for " + Path.GetFileName(wav) + ": " + err + " (the WAV is fine)";
                    Dispatcher.Invoke(() => Log(line));
                }
                Dispatcher.Invoke(() => Log("MP3 export finished."));
            });
        }

        // ---------- ui plumbing ----------

        private void OnUiTick(object sender, EventArgs e)
        {
            bool live = _engine.State == RecorderState.Recording;

            TimerText.Text = _engine.Elapsed.ToString(@"hh\:mm\:ss\.f");

            double mic = _engine.State == RecorderState.Idle ? 0 : _engine.TakeMicPeak();
            double sys = _engine.State == RecorderState.Idle ? 0 : _engine.TakeSystemPeak();

            // fast attack, slow release so the meter is readable
            _micLevel = mic > _micLevel ? mic : Math.Max(0, _micLevel - 0.04);
            _sysLevel = sys > _sysLevel ? sys : Math.Max(0, _sysLevel - 0.04);
            if (!live && _engine.State != RecorderState.Paused) { _micLevel *= 0.8; _sysLevel *= 0.8; }

            MicMeter.Value = Scale(_micLevel);
            SysMeter.Value = Scale(_sysLevel);
            MicDb.Text = Db(_micLevel);
            SysDb.Text = Db(_sysLevel);
        }

        // meters are drawn on a dB scale, otherwise normal speech barely moves them
        private static double Scale(double peak)
        {
            if (peak <= 0.0001) return 0;
            double db = 20 * Math.Log10(peak);
            double pct = (db + 60) / 60 * 100;
            return Math.Max(0, Math.Min(100, pct));
        }

        private static string Db(double peak)
            => peak <= 0.0001 ? "-inf" : (20 * Math.Log10(peak)).ToString("0.0");

        private void UpdateButtons()
        {
            bool idle = _engine.State == RecorderState.Idle;
            StartButton.IsEnabled = idle && !_finishingTranscript && !_settingUpSpeech;
            TranscriptCheck.IsEnabled = idle && !_finishingTranscript;
            OnlyMeCheck.IsEnabled = idle && !_finishingTranscript;
            MicrophoneNameBox.IsEnabled = idle && !_finishingTranscript;
            ModelsButton.IsEnabled = idle && !_finishingTranscript && !_settingUpSpeech;
            PauseButton.IsEnabled = !idle;
            StopButton.IsEnabled = !idle;
            MicCombo.IsEnabled = idle;
            SysCombo.IsEnabled = idle;
            FolderBox.IsEnabled = idle;
            RefreshButton.IsEnabled = idle;
        }

        private void OnEngineNotice(string message) => Dispatcher.BeginInvoke(new Action(() => Log(message)));

        private void Log(string message)
        {
            string text = LogText.Text + (LogText.Text.Length > 0 ? "\n" : "") +
                          DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            if (text.Length > 12000) text = text.Substring(text.Length - 12000);
            LogText.Text = text;
            LogScroll.ScrollToEnd();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadDevices();

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "Where should recordings be saved?";
                if (Directory.Exists(FolderBox.Text)) dlg.SelectedPath = FolderBox.Text;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    FolderBox.Text = dlg.SelectedPath;
                    SaveSettings();
                }
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(FolderBox.Text);
                Process.Start(new ProcessStartInfo(FolderBox.Text) { UseShellExecute = true });
            }
            catch (Exception ex) { Log("Could not open the folder: " + ex.Message); }
        }

        private int SelectedBitrate()
        {
            var item = BitrateCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
            int v;
            return item != null && int.TryParse(item.Content.ToString(), out v) ? v : 192;
        }

        private void SelectBitrate(int value)
        {
            foreach (var o in BitrateCombo.Items)
            {
                var item = o as System.Windows.Controls.ComboBoxItem;
                if (item != null && item.Content.ToString() == value.ToString()) { BitrateCombo.SelectedItem = item; return; }
            }
        }

        private void SaveSettings()
        {
            var mic = MicCombo.SelectedItem as DeviceEntry;
            var sys = SysCombo.SelectedItem as DeviceEntry;
            _settings.MicDeviceId = mic?.Id;
            _settings.RenderDeviceId = sys?.Id;
            _settings.OutputFolder = FolderBox.Text;
            _settings.ExportMp3 = Mp3Check.IsChecked == true;
            _settings.LiveTranscription = TranscriptCheck.IsChecked == true;
            _settings.MicrophoneIsOnlyMe = OnlyMeCheck.IsChecked == true;
            _settings.MicrophoneName = MicrophoneNameBox.Text.Trim();
            _settings.Mp3BitRate = SelectedBitrate();
            _settings.Save();
        }

        private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_shuttingDown || _preview) return;
            if (_closingAfterTranscription) { e.Cancel = true; return; }

            if (_engine.State != RecorderState.Idle)
            {
                var answer = MessageBox.Show(this,
                    "A recording is still running. Stop it and save the files?",
                    "Dual Recorder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (answer == MessageBoxResult.Yes) StopRecording();
                else { try { _engine.Stop(); } catch { } FinishTranscription(); }
            }

            if (_transcription != null && !_transcription.Completion.IsCompleted)
            {
                e.Cancel = true;
                _closingAfterTranscription = true;
                _transcription.RequestStop();
                TranscriptStatus.Text = "Saving the last transcript sections before closing...";
                await _transcription.Completion;
                _closingAfterTranscription = false;
                _shuttingDown = true;
                SaveSettings();
                _ui.Stop();
                try { _hotkey.Dispose(); } catch { }
                try { _engine.Dispose(); } catch { }
                if (_tray != null) { try { _tray.Visible = false; _tray.Dispose(); } catch { } _tray = null; }
                Close();
                return;
            }
            _shuttingDown = true;
            SaveSettings();
            _ui.Stop();
            try { _hotkey.Dispose(); } catch { }
            try { _engine.Dispose(); } catch { }
            if (_tray != null) { try { _tray.Visible = false; _tray.Dispose(); } catch { } _tray = null; }
        }
    }
}
