using System;
using System.IO;
using NAudio.Lame;
using NAudio.Wave;

namespace DualRecorder.Audio
{
    public static class Mp3Exporter
    {
        /// <summary>
        /// Encode a finished 16-bit WAV to MP3 next to it. Returns the mp3 path, or null on
        /// failure; a failed export never touches the WAV files, which stay the master copy.
        /// </summary>
        public static string Encode(string wavPath, int bitRateKbps, out string error)
        {
            error = null;
            try
            {
                string mp3Path = Path.ChangeExtension(wavPath, ".mp3");
                using (var reader = new WaveFileReader(wavPath))
                using (var writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, bitRateKbps))
                {
                    reader.CopyTo(writer);
                }
                return mp3Path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
