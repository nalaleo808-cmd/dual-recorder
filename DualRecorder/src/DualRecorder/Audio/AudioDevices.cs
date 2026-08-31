using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace DualRecorder.Audio
{
    public sealed class DeviceEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public override string ToString() => IsDefault ? Name + "  (Windows default)" : Name;
    }

    public static class AudioDevices
    {
        public static List<DeviceEntry> Inputs() => List(DataFlow.Capture);
        public static List<DeviceEntry> Outputs() => List(DataFlow.Render);

        private static List<DeviceEntry> List(DataFlow flow)
        {
            var result = new List<DeviceEntry>();
            using (var en = new MMDeviceEnumerator())
            {
                string defaultId = null;
                try
                {
                    using (var def = en.GetDefaultAudioEndpoint(flow, Role.Console))
                        defaultId = def.ID;
                }
                catch { /* no default endpoint present */ }

                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    try
                    {
                        result.Add(new DeviceEntry { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defaultId });
                    }
                    catch { }
                    finally { d.Dispose(); }
                }
            }
            // the Windows default first
            result.Sort((a, b) => a.IsDefault == b.IsDefault
                ? string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)
                : (a.IsDefault ? -1 : 1));
            return result;
        }

        /// <summary>Resolve an id to a live device, falling back to the Windows default.</summary>
        public static MMDevice Resolve(string id, DataFlow flow)
        {
            var en = new MMDeviceEnumerator();
            try
            {
                if (!string.IsNullOrEmpty(id))
                {
                    try { return en.GetDevice(id); } catch { }
                }
                return en.GetDefaultAudioEndpoint(flow, Role.Console);
            }
            finally { en.Dispose(); }
        }
    }
}
