using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace UpdateHub
{
    [DataContract]
    public sealed class HistoryEntry
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public DateTime Time { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Before { get; set; }
        [DataMember] public string Requested { get; set; }
        [DataMember] public string Installed { get; set; }
        [DataMember] public string Result { get; set; }

        public HistoryEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            Time = DateTime.UtcNow;
        }

        public string Date { get { return Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm"); } }
        public string Target { get { return String.IsNullOrEmpty(Installed) ? Requested + " (цель)" : Installed; } }

        public void Verify(Package current, bool scanSucceeded)
        {
            if (scanSucceeded && current != null && !String.IsNullOrWhiteSpace(current.Installed) &&
                !String.Equals(current.Installed, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(current.Installed, Before, StringComparison.OrdinalIgnoreCase) &&
                (String.Equals(current.Installed, Requested, StringComparison.OrdinalIgnoreCase) || !current.CanUpdate))
            {
                Installed = current.Installed;
                Result = "Установлено";
            }
            else Result = "Версия не подтверждена";
        }
    }

    public sealed class UpdateHistory
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly string path;

        public UpdateHistory(string path) { this.path = path; }

        public void Append(HistoryEntry entry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(HistoryEntry)).WriteObject(buffer, entry);
                File.AppendAllText(path, "\n" + Utf8.GetString(buffer.ToArray()) + "\n", Utf8);
            }
        }

        public List<HistoryEntry> Read()
        {
            var entries = new Dictionary<string, HistoryEntry>();
            var serializer = new DataContractJsonSerializer(typeof(HistoryEntry));
            try
            {
                foreach (var line in File.ReadLines(path, Utf8))
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using (var buffer = new MemoryStream(Utf8.GetBytes(line)))
                        {
                            var entry = (HistoryEntry)serializer.ReadObject(buffer);
                            if (entry != null && !String.IsNullOrEmpty(entry.Id)) entries[entry.Id] = entry;
                        }
                    }
                    catch (SerializationException) { /* A truncated record does not hide older entries. */ }
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            return entries.Values.OrderByDescending(entry => entry.Time).ToList();
        }
    }
}
