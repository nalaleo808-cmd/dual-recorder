using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DualRecorder.Transcription
{
    public static class SpeechModels
    {
        private const string AsrUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26/resolve/672fbf1b30579d6585301139bb363f42a0ad4a24/";
        private sealed record ModelFile(string Name, string Url, string Hash);
        private static readonly ModelFile[] Files =
        {
            new("encoder.onnx", AsrUrl + "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx", "563fde436d16cf7607cf408cd6b30909819d03162652ef389c2450ced3f45ac1"),
            new("decoder.onnx", AsrUrl + "decoder-epoch-99-avg-1-chunk-16-left-128.onnx", "7bf787f90b194b307e5a4ad6a34fadb4e748304c35f78a8d66358a05b13ee6ef"),
            new("joiner.onnx", AsrUrl + "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx", "d944208d660d67c8d72cd2acaeac971fa5ceb8c80e76c1968148846fedd6e297"),
            new("tokens.txt", AsrUrl + "tokens.txt", "49e3c2646595fd907228b3c6787069658f67b17377c60aeb8619c4551b2316fb"),
            new("segmentation.onnx", "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx", "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
            new("embedding.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx", "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012")
        };

        public static string Folder
        {
            get
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "models");
                return HasAllFiles(DownloadFolder) ? DownloadFolder : bundled;
            }
        }

        public static string DownloadFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualRecorder", "models", "live-en-v1");
        public static bool Available => HasAllFiles(Folder);

        private static bool HasAllFiles(string folder)
        {
            foreach (var model in Files)
                if (!File.Exists(Path.Combine(folder, model.Name))) return false;
            return true;
        }

        public static bool Verify(string folder)
        {
            foreach (var model in Files)
            {
                string path = Path.Combine(folder, model.Name);
                if (!File.Exists(path)) return false;
                using var input = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(model.Hash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // Downloads only public model files. Recording audio never enters an HTTP request.
        public static async Task DownloadAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            string folder = DownloadFolder;
            Directory.CreateDirectory(folder);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            for (int i = 0; i < Files.Length; i++)
            {
                var model = Files[i];
                string destination = Path.Combine(folder, model.Name);
                if (File.Exists(destination))
                {
                    using var existing = File.OpenRead(destination);
                    if (Convert.ToHexString(SHA256.HashData(existing)).Equals(model.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                }
                progress?.Report($"Downloading speech files ({i + 1}/{Files.Length}): {model.Name}");
                string temporary = Path.Combine(Path.GetTempPath(), "DualRecorder-model-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var response = await client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using (var output = File.Create(temporary))
                        await response.Content.CopyToAsync(output, cancellationToken);
                    using (var input = File.OpenRead(temporary))
                        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(model.Hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The downloaded speech file did not pass verification. Try setup again.");
                    File.Copy(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            progress?.Report("Live transcription is ready. Speech is processed on this PC.");
        }
    }
}
