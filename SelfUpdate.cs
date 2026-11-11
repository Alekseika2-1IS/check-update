using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace UpdateHub
{
    public sealed class GitHubRateLimitException : HttpRequestException
    {
        public GitHubRateLimitException() : base("GitHub временно ограничил число запросов.") { }
    }
    [DataContract]
    public class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "browser_download_url")] public string Url;
        [DataMember(Name = "size")] public long Size;
    }

    [DataContract]
    public class AppRelease
    {
        [DataMember(Name = "tag_name")] public string Tag;
        [DataMember(Name = "draft")] public bool Draft;
        [DataMember(Name = "prerelease")] public bool Prerelease;
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets;
    }

    public static class SelfUpdate
    {
        public const string Repository = "Alekseika2-1IS/check-update";
        public const string AssetName = "Check-Update.exe";
        public static Version CurrentVersion { get { return typeof(SelfUpdate).Assembly.GetName().Version; } }
        private const long MaxDownload = 50 * 1024 * 1024;

        public static Version ParseVersion(string tag)
        {
            Version value;
            if (String.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out value)) return null;
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        public static bool IsTrustedAsset(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "github.com"
                && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Query)
                && String.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal);
        }

        public static AppRelease ParseRelease(string json, Version current)
        {
            AppRelease release;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                release = (AppRelease)new DataContractJsonSerializer(typeof(AppRelease)).ReadObject(stream);
            var version = ParseVersion(release.Tag);
            if (release.Draft || release.Prerelease || version == null || version <= current) return null;
            var executable = release.Assets == null ? null : release.Assets.SingleOrDefault(a => a.Name == AssetName);
            var checksum = release.Assets == null ? null : release.Assets.SingleOrDefault(a => a.Name == AssetName + ".sha256");
            if (executable == null || checksum == null || !IsTrustedAsset(executable.Url) || !IsTrustedAsset(checksum.Url)
                || executable.Size <= 0 || executable.Size > MaxDownload || checksum.Size <= 0 || checksum.Size > 4096)
                throw new InvalidDataException("В выпуске нет подходящего файла программы или контрольной суммы.");
            return release;
        }

        private static HttpClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CheckUpdate/" + CurrentVersion);
            return client;
        }

        private static DateTimeOffset nextVersionCheck;

        public static async Task<AppRelease> CheckAsync()
        {
            if (DateTimeOffset.UtcNow < nextVersionCheck) throw new GitHubRateLimitException();
            using (var client = Client())
            using (var response = await client.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest"))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                var retryAt = RateLimitRetryTime(response, DateTimeOffset.UtcNow);
                if (retryAt.HasValue)
                {
                    nextVersionCheck = retryAt.Value;
                    throw new GitHubRateLimitException();
                }
                response.EnsureSuccessStatusCode();
                return ParseRelease(await response.Content.ReadAsStringAsync(), CurrentVersion);
            }
        }

        private static DateTimeOffset? RateLimitRetryTime(HttpResponseMessage response, DateTimeOffset now)
        {
            if (response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode != 429) return null;
            IEnumerable<string> remaining;
            bool exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out remaining) && remaining.Contains("0");
            var retry = response.Headers.RetryAfter;
            if (!exhausted && retry == null && (int)response.StatusCode != 429 &&
                (response.ReasonPhrase ?? "").IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (retry != null)
            {
                if (retry.Delta.HasValue && retry.Delta.Value > TimeSpan.Zero) return now + retry.Delta.Value;
                if (retry.Date.HasValue && retry.Date.Value > now) return retry.Date.Value;
            }
            IEnumerable<string> reset;
            long seconds;
            if (exhausted && response.Headers.TryGetValues("X-RateLimit-Reset", out reset) &&
                Int64.TryParse(reset.FirstOrDefault(), out seconds) && seconds >= 0 && seconds <= 253402300799)
            {
                var resetTime = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
                if (resetTime > now) return resetTime;
            }
            return now.AddMinutes(1);
        }

        public static string Hash(string path)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static bool ValidHash(string hash)
        {
            return hash != null && System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$");
        }

        public static void SyncInstalledVersion(string executable)
        {
            // Portable copies must never modify the installed application's entry.
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\CheckUpdate_is1", true))
                {
                    if (key == null) return;
                    string directory = key.GetValue("InstallLocation") as string;
                    if (String.IsNullOrWhiteSpace(directory)) return;
                    if (!String.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase)) return;
                    key.SetValue("DisplayVersion", System.Reflection.AssemblyName.GetAssemblyName(executable).Version.ToString(3));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        public static async Task<string> DownloadAsync(AppRelease release, IProgress<int> progress)
        {
            var asset = release.Assets.Single(a => a.Name == AssetName);
            var checksum = release.Assets.Single(a => a.Name == AssetName + ".sha256");
            if (!IsTrustedAsset(asset.Url) || !IsTrustedAsset(checksum.Url)) throw new InvalidDataException("Неизвестный источник обновления.");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckUpdate", "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, AssetName);
            try
            {
                using (var client = Client())
                {
                    var checksumText = await client.GetStringAsync(checksum.Url);
                    string expected = checksumText.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!ValidHash(expected)) throw new InvalidDataException("Контрольная сумма обновления не распознана.");
                    using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            var buffer = new byte[81920]; long total = 0; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                total += read;
                                if (total > MaxDownload || total > asset.Size) throw new InvalidDataException("Размер обновления отличается от ожидаемого.");
                                await output.WriteAsync(buffer, 0, read);
                                progress.Report((int)(total * 100 / asset.Size));
                            }
                            if (total != asset.Size) throw new InvalidDataException("Обновление скачано не полностью.");
                        }
                    }
                    if (!String.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Проверка файла обновления не пройдена. Установка отменена.");
                    if (System.Reflection.AssemblyName.GetAssemblyName(path).Version != ParseVersion(release.Tag))
                        throw new InvalidDataException("Версия скачанного файла не совпадает с выпуском.");
                }
                return path;
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }

        public static void ReplaceAndRestart(string source, string target, string expectedHash, Action<string> start)
        {
            if (!ValidHash(expectedHash) || !String.Equals(Hash(source), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка обновления не пройдена.");
            string stage = target + ".update-" + Guid.NewGuid().ToString("N");
            string backup = target + ".previous";
            try
            {
                File.Copy(source, stage, false);
                if (!String.Equals(Hash(stage), expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Проверка подготовленного файла не пройдена.");
                File.Replace(stage, target, backup, true);
                try { start(target); }
                catch { File.Copy(backup, target, true); throw; }
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }

        public static void ScheduleInstall(string downloaded)
        {
            string current = typeof(SelfUpdate).Assembly.Location;
            string helper = Path.Combine(Path.GetDirectoryName(downloaded), "update-helper.exe");
            File.Copy(current, helper, true);
            var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            info.Arguments = "--apply-update " + Process.GetCurrentProcess().Id + " \"" + current + "\" \"" + downloaded + "\" " + Hash(downloaded);
            Process.Start(info);
        }

        public static bool RunHelper(string[] args)
        {
            if (args.Length == 0 || args[0] != "--apply-update") return false;
            try
            {
                if (args.Length != 5) throw new ArgumentException("Некорректные параметры обновления.");
                try { using (var parent = Process.GetProcessById(Int32.Parse(args[1]))) if (!parent.WaitForExit(60000)) throw new TimeoutException("Приложение ещё открыто. Закройте его и повторите обновление."); }
                catch (ArgumentException) { }
                ReplaceAndRestart(args[3], args[2], args[4], path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Не удалось заменить приложение. Предыдущая версия сохранена.\n\n" + ex.Message, "Check Update");
            }
            return true;
        }
    }
}
