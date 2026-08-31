using System;
using System.IO;
using System.Text.Json;

namespace DualRecorder
{
    /// <summary>Remembers the last device choice, folder and MP3 toggle. Best effort only.</summary>
    public sealed class Settings
    {
        public string MicDeviceId { get; set; }
        public string RenderDeviceId { get; set; }
        public string OutputFolder { get; set; }
        public bool ExportMp3 { get; set; }
        public int Mp3BitRate { get; set; } = 192;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualRecorder");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new Settings
            {
                OutputFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "DualRecorder")
            };
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
