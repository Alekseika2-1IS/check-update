using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UpdateHub
{
    public sealed class NvidiaUpdateReport
    {
        public readonly List<string> Lines = new List<string>();
        public readonly HashSet<Version> InstalledVersions = new HashSet<Version>();
        public bool HasAdapter;
        public bool HasUpdate;
        public bool Complete = true;
    }

    public static class NvidiaUpdates
    {
        public const string LoggingFixUrl = "https://www.nvidia.com/en-us/drivers/details/288689/";

        public static string LoggingNotice(Version installed)
        {
            if (installed == new Version(621, 24))
                return "621.24: возможна избыточная запись в журнал NVIDIA. NVIDIA указала исправление в 621.40.";
            if (installed == new Version(621, 40))
                return "621.40: NVIDIA указала исправление избыточной фоновой записи в журнал.";
            return null;
        }

        public static async Task<string> CheckLogWritesAsync()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NVIDIA Corporation", "nvtopps", "nct", "nvlog.nvlgstg");
            var previous = ReadLogStamp(path);
            bool found = previous != null;
            int changes = 0;
            for (int sample = 0; sample < 6; sample++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var current = ReadLogStamp(path);
                if (!Equals(previous, current)) changes++;
                found |= current != null;
                previous = current;
            }
            if (!found) return "За 30 секунд журнал NVIDIA не появился.";
            if (changes == 0) return "За 30 секунд размер и время изменения журнала остались прежними.";
            return "За 30 секунд журнал изменился в " + changes + " из 6 интервалов. Этот замер показывает активность журнала. Объём записи на SSD по нему определить нельзя.";
        }

        private static Tuple<long, DateTime, DateTime> ReadLogStamp(string path)
        {
            var file = new FileInfo(path);
            try { return Tuple.Create(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private const string Catalog = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";
        private static readonly Regex HardwareId = new Regex(@"^PCI\\VEN_10DE&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{4})([0-9A-F]{4})(?:&|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024
        };

        [DataContract]
        private sealed class DriverResponse
        {
            [DataMember(Name = "DriverAttributes")] public DriverAttributes Driver { get; set; }
            [DataMember(Name = "criteria")] public DriverCriteria Criteria { get; set; }
        }

        [DataContract]
        private sealed class DriverCriteria
        {
            [DataMember(Name = "IsSupported")] public SupportedDevice Device { get; set; }
        }

        [DataContract]
        private sealed class SupportedDevice
        {
            [DataMember(Name = "state")] public string State { get; set; }
            [DataMember(Name = "DeviceID")] public string Id { get; set; }
            [DataMember(Name = "Vendor")] public string Vendor { get; set; }
            [DataMember(Name = "SubSystemID")] public string Subsystem { get; set; }
            [DataMember(Name = "SubVendorID")] public string Subvendor { get; set; }
        }

        [DataContract]
        private sealed class DriverAttributes
        {
            [DataMember(Name = "Version")] public string Version { get; set; }
            [DataMember(Name = "IsCRD")] public int Studio { get; set; }
            [DataMember(Name = "IsBeta")] public int Beta { get; set; }
            [DataMember(Name = "IsDC")] public int Dch { get; set; }
        }

        private sealed class Adapter
        {
            public string Name;
            public string Device;
            public Version Installed;
        }

        private sealed class ChannelResult
        {
            public readonly string Message;
            public readonly bool? HasUpdate;

            public ChannelResult(string message, bool? hasUpdate = null)
            {
                Message = message;
                HasUpdate = hasUpdate;
            }
        }

        private static readonly Regex VendorVersion = new Regex(@"^\d{2,4}\.\d{2}$", RegexOptions.CultureInvariant);

        public static Version InstalledVersion(string text)
        {
            Version version;
            if (!Version.TryParse(text, out version) || version.Build < 0 || version.Revision < 0 || version.Revision > 9999)
                return null;
            int build = version.Build % 10 * 10000 + version.Revision;
            return build >= 1000 ? new Version(build / 100, build % 100) : null;
        }

        public static Version ReadOffer(string json, string hardware, bool studio)
        {
            var parts = hardware.Split('_');
            using (var buffer = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('\uFEFF'))))
            {
                var data = (DriverResponse)new DataContractJsonSerializer(typeof(DriverResponse)).ReadObject(buffer);
                var device = data == null || data.Criteria == null ? null : data.Criteria.Device;
                var driver = data == null ? null : data.Driver;
                if (device == null || device.State != "true" || driver == null || parts.Length != 4 ||
                    !String.Equals(device.Id, parts[0], StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(device.Vendor, parts[1], StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(device.Subsystem, parts[2], StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(device.Subvendor, parts[3], StringComparison.OrdinalIgnoreCase) ||
                    driver.Studio != (studio ? 1 : 0) || driver.Beta != 0 || driver.Dch != 1 ||
                    driver.Version == null || !VendorVersion.IsMatch(driver.Version))
                    return null;
                return Version.Parse(driver.Version);
            }
        }

        public static async Task<NvidiaUpdateReport> ScanAsync()
        {
            var adapters = new List<Adapter>();
            using (var search = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController"))
            using (var results = search.Get())
            {
                foreach (ManagementObject item in results)
                using (item)
                {
                    string id = item["PNPDeviceID"] as string;
                    if (id == null || !id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase)) continue;
                    var match = HardwareId.Match(id);
                    adapters.Add(new Adapter
                    {
                        Name = item["Name"] as string ?? "NVIDIA",
                        Device = match.Success ? (match.Groups[1].Value + "_10DE_" + match.Groups[2].Value + "_" + match.Groups[3].Value).ToUpperInvariant() : null,
                        Installed = InstalledVersion(item["DriverVersion"] as string)
                    });
                }
            }
            var report = new NvidiaUpdateReport { HasAdapter = adapters.Count > 0 };
            foreach (var adapter in adapters)
                if (adapter.Installed != null) report.InstalledVersions.Add(adapter.Installed);
            if (!report.HasAdapter)
            {
                report.Lines.Add("Видеокарта NVIDIA не обнаружена.");
                return report;
            }

            string osBuild = "";
            using (var search = new ManagementObjectSearcher("SELECT BuildNumber FROM Win32_OperatingSystem"))
            using (var results = search.Get())
                foreach (ManagementObject item in results)
                using (item) { osBuild = item["BuildNumber"] as string; break; }
            int build;
            if (!Int32.TryParse(osBuild, out build) || build < 10240)
                throw new InvalidOperationException("Не определена сборка Windows 10 или 11.");

            bool mobile = false;
            using (var search = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem"))
            using (var results = search.Get())
                foreach (ManagementObject item in results)
                using (item) { mobile = Convert.ToInt32(item["PCSystemType"]) == 2; break; }

            foreach (var adapter in adapters)
            {
                report.Lines.Add(adapter.Name + (adapter.Installed == null ? "" : " · установлено " + DisplayVersion(adapter.Installed)));
                if (adapter.Device == null || adapter.Installed == null)
                {
                    report.Lines.Add("Не удалось определить модель или версию драйвера для проверки.");
                    report.Complete = false;
                    continue;
                }
                var channels = await Task.WhenAll(CheckChannelAsync(adapter, build, mobile, false), CheckChannelAsync(adapter, build, mobile, true)).ConfigureAwait(false);
                foreach (var channel in channels)
                {
                    report.Lines.Add(channel.Message);
                    report.HasUpdate |= channel.HasUpdate == true;
                    report.Complete &= channel.HasUpdate.HasValue;
                }
            }
            return report;
        }

        private static async Task<ChannelResult> CheckChannelAsync(Adapter adapter, int build, bool mobile, bool studio)
        {
            string channel = studio ? "Studio" : "Game Ready";
            string branch = studio ? "1" : "0";
            string query = "{\"dIDa\":[\"" + adapter.Device + "\"],\"osC\":\"10.0\",\"osB\":\"" + build.ToString(CultureInfo.InvariantCulture)
                + "\",\"is6\":\"1\",\"lg\":\"1033\",\"iLp\":\"" + (mobile ? "1" : "0")
                + "\",\"gIsB\":\"0\",\"dch\":\"1\",\"upCRD\":\"" + branch + "\",\"isCRD\":\"" + branch + "\"}";
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, Catalog + Uri.EscapeDataString(query)))
                {
                    request.Headers.UserAgent.ParseAdd("CheckUpdate/" + SelfUpdate.CurrentVersion.ToString(3));
                    using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var available = ReadOffer(await response.Content.ReadAsStringAsync().ConfigureAwait(false), adapter.Device, studio);
                        if (available == null) return new ChannelResult(channel + " · каталог не подтвердил совместимый выпуск.");
                        if (available > adapter.Installed) return new ChannelResult(channel + " · доступна версия " + DisplayVersion(available), true);
                        return new ChannelResult(channel + " · в каталоге " + DisplayVersion(available) + ", новее установленной версии нет.", false);
                    }
                }
            }
            catch (HttpRequestException) { return new ChannelResult(channel + " · каталог NVIDIA недоступен. Попробуйте позднее."); }
            catch (TaskCanceledException) { return new ChannelResult(channel + " · каталог NVIDIA не ответил вовремя."); }
            catch (SerializationException) { return new ChannelResult(channel + " · ответ каталога NVIDIA не распознан."); }
        }

        private static string DisplayVersion(Version version)
        {
            return version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
