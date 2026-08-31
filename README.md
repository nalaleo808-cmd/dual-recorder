# Dual Recorder

Records your microphone and your speakers at the same time, into separate files plus a combined one.

## How to run it (for someone who does not write code)

1. Install the free .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 (pick "SDK", x64).
2. Unzip this folder somewhere, then double click `build.cmd` and wait about a minute; a folder will pop open with `DualRecorder.exe` inside it.
3. Copy that one `DualRecorder.exe` anywhere you like and double click it to record; it needs nothing else installed.

## Using it

- Pick your microphone and your speakers at the top. Both start on whatever Windows is already using.
- Choose a folder, press **Start**, and watch the two meters move so you can see both sources are live.
- **Pause** stops adding to the files and keeps the clock still; **Resume** carries on in the same files.
- **Stop** writes three files, all exactly the same length:
  - `2026-08-31_14-05-33_mic-only.wav`
  - `2026-08-31_14-05-33_system-only.wav`
  - `2026-08-31_14-05-33_mixed.wav`
- Tick **Also export MP3 copies** to get an `.mp3` beside each `.wav`. If MP3 encoding ever fails, the WAV files are untouched and the app says so in the log.
- **Ctrl + Shift + R** starts and stops from anywhere, even when the window is minimised.
- Minimising sends the app to the system tray. Double click the tray icon to bring it back, or right click it for Start/Stop and Exit.
- Device choice, folder and the MP3 toggle are remembered in `%AppData%\DualRecorder\settings.json`.

Output format is 48 kHz, stereo, 16-bit PCM WAV.

## How the hard parts are handled

**Loopback goes quiet.** WASAPI loopback stops raising callbacks entirely when nothing is playing, so the system track would otherwise be short and everything after the gap would slide earlier in time. A 50 ms pump compares each track against the recording clock and writes real silence into any track that has fallen more than 250 ms behind. Both tracks therefore always match the wall clock, and the mixdown only ever consumes frames that both tracks have, so it cannot drift.

**Different sample rates.** The mic and the speakers usually run at different rates (44.1 kHz vs 48 kHz is the common pair). Each source is converted to 48 kHz stereo at capture time by `WdlResamplingSampleProvider`, before anything is written or mixed, so mixing never has to line up mismatched rates.

**A device is unplugged mid-recording.** The capture object raises `RecordingStopped` with an exception, or throws inside the data callback. That marks only that one track as faulted; the pump then pads it with silence to the clock, the other track keeps recording, and the app writes a note in the log. Nothing already captured is lost and Stop still produces all three files.

**A crash must not corrupt the files.** Files are written by `WavWriterSafe`, which writes a 44 byte RIFF header up front and rewrites the size fields about once per second, then flushes to disk. If the process is killed, the file on disk already has a valid header covering everything written up to the last flush, so it opens and plays. The header is never allowed to claim more data than is actually on disk.

## Layout

```
src/DualRecorder/
  Core/            no audio library here, so it can be tested off Windows
    WavWriterSafe.cs   crash safe incremental WAV writer
    SourceTrack.cs     one source: its WAV file, mix feed, meter, silence padding
    MixWriter.cs       frame aligned two source mixdown
    SyncPump.cs        the clock rule that keeps everything the same length
    FloatFifo.cs
  Audio/
    RecordingEngine.cs         owns both captures, the clock and the three files
    CaptureSource.cs           one WASAPI capture plus resampling
    AudioDevices.cs            device lists and default resolution
    ChannelAdapterSampleProvider.cs
    Mp3Exporter.cs
  Interop/GlobalHotkey.cs      RegisterHotKey
  MainWindow.xaml(.cs)         UI, meters, tray
  Settings.cs
tests/
  CoreTests/           runnable proof of the sync and crash safety rules
  AudioCompileCheck/   type checks the audio layer without NuGet or Windows
```

## Tests

`tests/CoreTests` is a plain console app. On any machine with the .NET 8 SDK:

```
dotnet run --project tests/CoreTests
```

It simulates 10 second recordings and asserts, by reading the finished files back off disk:

- both tracks and the mixdown come out byte for byte the same length when both sources are live
- when the system source delivers nothing for 6 seconds, exactly 6.0 s of silence is inserted and all three files still match
- when the mic is unplugged at 4 seconds, the mic file is still finalised at full length and the first 4 seconds survive
- after a simulated crash with nothing closed, the half written files still have valid headers and never overstate their data size
- out of range and NaN samples are clamped rather than wrapping to loud noise

All of those pass. `tests/AudioCompileCheck` compiles the whole audio layer against a small stand-in for the NAudio API so it can be type checked without NuGet access.

## What has not been run

The WPF and NAudio parts have not been compiled or run, because this was built in a Linux sandbox with no NuGet access and no Windows. The core timing, mixing and file writing logic is tested as described above, and the audio layer is type checked against stub interfaces, but the first real `dotnet publish` is on your machine.
