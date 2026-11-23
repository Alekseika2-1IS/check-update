using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: System.Reflection.AssemblyTitle("Check Update")]
[assembly: System.Reflection.AssemblyProduct("Check Update")]
[assembly: System.Reflection.AssemblyVersion("0.10.3.0")]

namespace UpdateHub
{
    public class Package : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Installed { get; set; }
        public string Available { get; set; }
        public bool CanUpdate { get { return !String.IsNullOrWhiteSpace(Available); } }
        public ImageSource Icon { get; set; }
        public HistoryEntry LastAttempt { get; set; }
        public string StatusDetail { get { return LastAttempt == null ? Status : LastAttempt.Detail ?? Status; } }
        public string Initial { get { return String.IsNullOrWhiteSpace(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant(); } }
        private bool skipped;
        public bool Skipped
        {
            get { return skipped; }
            set { skipped = value; Changed("CanSelect"); Changed("Status"); }
        }
        public bool CanSelect { get { return CanUpdate && !Skipped; } }
        private bool selected;
        public bool Selected { get { return selected; } set { selected = value; Changed("Selected"); } }
        private string status;
        public string Status { get { return Skipped ? "Версия пропущена" : status ?? (CanUpdate ? "Есть обновление" : "Без обновлений"); } set { status = value; Changed("Status"); Changed("StatusDetail"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(property)); }
    }

    public class CommandResult { public int Code; public string Output; public string Error; }

    public class VerificationResult
    {
        public List<Package> Packages;
        public int Pending;
        public string Error;
    }

    public static class UpdateVerifier
    {
        public static async Task<VerificationResult> VerifyAsync(ICollection<string> updatedIds,
            Func<Task<List<Package>>> scan, Func<int, Task> delay, Func<string, List<Package>, bool> isPending = null)
        {
            var result = new VerificationResult();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await delay(attempt * 3000);
                try
                {
                    result.Packages = await scan();
                    result.Error = null;
                    result.Pending = updatedIds.Count(id => isPending == null
                        ? result.Packages.Any(p => String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) && p.CanUpdate)
                        : isPending(id, result.Packages));
                    if (result.Pending == 0) return result;
                }
                catch (Exception ex) { result.Error = ex.Message; }
            }
            return result;
        }
    }

    public static class Winget
    {
        public static string UpgradeDetail(int code)
        {
            switch (unchecked((uint)code))
            {
                case 0x8A15002B: return "WinGet больше не видит подходящего обновления.";
                case 0x8A150101:
                case 0x8A150103: return "Закройте программу и повторите обновление.";
                case 0x8A150102: return "Дождитесь окончания другой установки и повторите обновление.";
                case 0x8A150105: return "Освободите место на диске и повторите обновление.";
                case 0x8A150107: return "Проверьте подключение к интернету и повторите обновление.";
                case 0x8A150109:
                case 0x8A15010B: return "Для завершения установки требуется перезагрузка Windows. Затем сверьте версию.";
                case 0x8A15010A: return "Перезагрузите Windows, затем повторите обновление.";
                case 0x8A15010C: return "Установку отменили. При повторе завершите шаги в окне установщика.";
                default: return String.Format("Установщик или WinGet завершился с кодом 0x{0:X8}. Подробности в журнале.", code);
            }
        }

        public static bool UpgradeNeedsVerification(int code)
        {
            return code == 0 || unchecked((uint)code) == 0x8A150109 || unchecked((uint)code) == 0x8A15010B;
        }
        public static CommandResult Run(string arguments, int timeout)
        {
            var info = new ProcessStartInfo("winget.exe", arguments) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    process.Kill(); process.WaitForExit();
                    throw new TimeoutException("Проверка заняла слишком много времени. Проверьте интернет и повторите попытку.");
                }
                Task.WaitAll(output, error);
                return new CommandResult { Code = process.ExitCode, Output = output.Result, Error = error.Result };
            }
        }

        internal static string StripTerminalFormatting(string output)
        {
            return Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        }

        public static List<Package> Parse(string output)
        {
            var clean = StripTerminalFormatting(output);
            var lines = clean.Replace("\r\n", "\n").Split('\n');
            int separator = Array.FindIndex(lines, l => Regex.IsMatch(l.Trim(), @"^-{10,}$"));
            if (separator < 1) throw new FormatException("Не удалось прочитать список программ. Подробности сохранены в журнале.");
            var columns = Regex.Matches(lines[separator - 1], @"\S+(?: \S+)*").Cast<Match>().Select(m => m.Index).ToArray();
            if (columns.Length < 3 || columns.Length > 5) throw new FormatException("Формат списка Windows не распознан.");
            var packages = new List<Package>();
            foreach (var line in lines.Skip(separator + 1))
            {
                if (line.Length <= columns[2]) continue;
                Func<int, string> cell = i => line.Length <= columns[i] ? "" : line.Substring(columns[i], Math.Min(line.Length, i + 1 < columns.Length ? columns[i + 1] : line.Length) - columns[i]).Trim();
                string id = cell(1);
                // A clipped identifier must never become an update target.
                if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$") || id.Length < 2) continue;
                packages.Add(new Package { Name = cell(0), Id = id, Installed = cell(2), Available = columns.Length >= 4 ? cell(3) : "" });
            }
            if (packages.Count == 0) throw new FormatException("Windows вернула список, но прочитать программы не удалось.");
            return packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(p => p.CanUpdate).First()).OrderBy(p => p.Name).ToList();
        }

        public static string Quote(string value)
        {
            if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")) throw new ArgumentException("Некорректный идентификатор программы.");
            return "\"" + value + "\"";
        }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Package> packages = new ObservableCollection<Package>();
        private readonly DataGrid table = new DataGrid();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock summary = new TextBlock();
        private readonly Button scan = new Button();
        private readonly Button update = new Button();
        private readonly CheckBox onlyUpdates = new CheckBox();
        private readonly CheckBox selectAll = new CheckBox();
        private readonly TextBox search = new TextBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TextBlock progressText = new TextBlock();
        private bool busy;
        private bool installing;
        private readonly Button appUpdate = new Button();
        private readonly TextBlock appVersionStatus = new TextBlock();
        private AppRelease availableRelease;
        private bool checkingVersion;
        private bool downloadingApp;
        private string downloadedApp;
        private readonly string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHub", "activity.log");

        private const string SettingsKey = @"Software\CheckUpdate";
        private const string ScanOnStartupSetting = "ScanOnStartup";

        public MainWindow(bool runStartupChecks = true)
        {
            Title = "Check Update " + SelfUpdate.CurrentVersion.ToString(3) + " — обновления программ"; Width = 1240; Height = 860; MinWidth = 1040; MinHeight = 740;
            var logo = new BitmapImage();
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CheckUpdate.Icon.png"))
            {
                logo.BeginInit(); logo.CacheOption = BitmapCacheOption.OnLoad; logo.StreamSource = stream; logo.EndInit(); logo.Freeze();
            }
            Icon = logo;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            BuildLayout(logo);
            Closing += (s, e) => { if (busy || downloadingApp) { e.Cancel = true; MessageBox.Show(this, downloadingApp ? "Дождитесь окончания скачивания обновления Check Update." : installing ? "Дождитесь окончания установки. Установщик может открыть отдельное окно." : "Дождитесь окончания проверки.", "Check Update"); } };
            if (runStartupChecks) Loaded += RunStartupChecks;
        }

        private async void RunStartupChecks(object sender, RoutedEventArgs e)
        {
            Loaded -= RunStartupChecks;
            Task programsCheck = scanOnStartup.IsChecked == true ? ScanAsync() : Task.FromResult(0);
            await Task.WhenAll(programsCheck, CheckAppVersionAsync());
        }

        private bool LoadScanOnStartup()
        {
            try
            {
                using (var settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return settings != null && Object.Equals(settings.GetValue(ScanOnStartupSetting), 1);
            }
            catch (Exception ex)
            {
                Log("Не удалось прочитать настройку автопроверки: " + ex.Message);
                return false;
            }
        }

        private void SaveScanOnStartup()
        {
            bool enabled = scanOnStartup.IsChecked == true;
            try
            {
                using (var settings = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey))
                    settings.SetValue(ScanOnStartupSetting, enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                scanOnStartup.IsChecked = !enabled;
                Log("Не удалось сохранить настройку автопроверки: " + ex.Message);
                ShowError("Не удалось сохранить настройку автопроверки.");
            }
        }

        private void AddColumn(string heading, string path, double width)
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Paint(path == "Available" ? "#89E9FC" : "#B5C2D6")));
            table.Columns.Add(new DataGridTextColumn { Header = heading, Binding = new Binding(path), IsReadOnly = true, MinWidth = 115, ElementStyle = textStyle, Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        }

        private void SetBusy(bool value)
        {
            busy = value; scan.IsEnabled = !value && !downloadingApp; update.IsEnabled = !value && !downloadingApp && packages.Any(p => p.CanSelect && p.Selected);
            RefreshSkipButton();
            RefreshRecoveryButtons();
            appUpdate.IsEnabled = !value && !checkingVersion && !downloadingApp;
            table.IsEnabled = !value; selectAll.IsEnabled = !value; onlyUpdates.IsEnabled = !value;
            if (!value) progress.IsIndeterminate = false;
            RefreshMaintenance();
        }

        private bool MatchesFilter(Package package)
        {
            string query = search.Text.Trim();
            return (onlyUpdates.IsChecked != true || package.CanSelect)
                && (query.Length == 0 || package.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || package.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void ShowProgress(string text, int completed, int total, bool indeterminate)
        {
            progressText.Text = text; progress.Maximum = Math.Max(1, total);
            progress.Value = completed; progress.IsIndeterminate = indeterminate;
        }

        private void Log(string text, bool error = false)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                File.AppendAllText(logFile, DateTime.Now.ToString("u") + " " + (error ? "[Ошибка] " : "") + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void ShowError(string message) { MessageBox.Show(this, message, "Check Update", MessageBoxButton.OK, MessageBoxImage.Information); }

        private async Task CheckAppVersionAsync()
        {
            if (checkingVersion) return;
            checkingVersion = true; appUpdate.IsEnabled = false; appVersionStatus.Text = "Проверяем новые версии…";
            try
            {
                availableRelease = await SelfUpdate.CheckAsync();
                ShowAppRelease(availableRelease);
            }
            catch (GitHubRateLimitException ex)
            {
                Log("Проверка версии приложения: " + ex.Message, true);
                appVersionStatus.Text = "GitHub временно ограничил запросы. Проверьте позже.";
            }
            catch (Exception ex) { Log("Проверка версии приложения: " + ex, true); appVersionStatus.Text = "GitHub недоступен — можно проверить позже"; }
            finally { checkingVersion = false; appUpdate.IsEnabled = !busy && !downloadingApp; }
        }

        private async Task DownloadAppAsync()
        {
            if (busy || downloadingApp || availableRelease == null) return;
            downloadingApp = true; SetBusy(false); appVersionStatus.Text = "Скачиваем обновление…";
            try
            {
                if (downloadedApp == null)
                    downloadedApp = await SelfUpdate.DownloadAsync(availableRelease, new Progress<int>(percent => appVersionStatus.Text = "Скачивание Check Update: " + percent + "%"));
                appVersionStatus.Text = "Обновление скачано и проверено"; appUpdate.FontSize = 12;
                SetButtonLabel(appUpdate, "Перезапустить");
            }
            catch (Exception ex) { Log("Скачивание приложения: " + ex, true); appVersionStatus.Text = "Скачать не удалось — повторите позже"; ShowError(ex.Message); }
            finally { downloadingApp = false; SetBusy(false); }
            if (downloadedApp != null && BuildRestartConfirmation(SelfUpdate.ParseVersion(availableRelease.Tag)).ShowDialog() == true)
            {
                try { SelfUpdate.ScheduleInstall(downloadedApp); Application.Current.Shutdown(); }
                catch (Exception ex) { Log(ex.ToString(), true); ShowError("Не удалось запустить обновление. " + ex.Message); }
            }
        }

        private async Task<List<Package>> LoadPackagesAsync()
        {
            var result = await Task.Run(() => Winget.Run("list --source winget --disable-interactivity", 120000));
            Log("Проверка: " + result.Code + "\n" + result.Output + "\n" + result.Error, result.Code != 0);
            if (result.Code != 0) throw new InvalidOperationException("Windows не смогла проверить программы. Проверьте интернет и доступность WinGet. Подробности — в журнале.");
            return await Task.Run(() =>
            {
                var found = Winget.Parse(result.Output);
                PackageIcons.Populate(found);
                return found;
            });
        }

        private void ReplacePackages(List<Package> found)
        {
            var previousAttempts = packages.Select(p => p.LastAttempt).Where(entry => entry != null).ToList();
            foreach (var p in packages) p.PropertyChanged -= SelectionChanged;
            packages.Clear();
            LoadSkippedVersions(found);
            foreach (var p in found) { p.PropertyChanged += SelectionChanged; packages.Add(p); }
            programsScanned = true;
            programScanFailed = false;
            ShowAttempts(previousAttempts.Where(entry => entry.CanRetry || entry.CanRecheck));
            selectAll.IsChecked = false;
            RefreshCounters(); RefreshView(); RefreshMaintenance();
        }

        private void ShowAttempts(IEnumerable<HistoryEntry> attempts)
        {
            foreach (var attempt in attempts)
            {
                var package = attempt.FindPackage(packages);
                if (package == null || (package.LastAttempt != null && package.LastAttempt.Time > attempt.Time)) continue;
                if (attempt.CanRetry && (!package.CanUpdate || !String.Equals(package.Available, attempt.Requested, StringComparison.OrdinalIgnoreCase))) continue;
                package.LastAttempt = attempt;
                package.Status = attempt.Result;
            }
            RefreshRecoveryButtons();
        }

        private async Task RecoverUpdateAsync(HistoryEntry attempt)
        {
            if (attempt == null || busy || downloadingApp) return;
            SetBusy(true);
            status.Text = "Проверяем текущую версию: " + attempt.Name;
            recoveryMessage.Text = status.Text;
            ShowProgress(status.Text, 0, 1, true);
            Package current = null;
            try
            {
                var selected = new HashSet<string>(packages.Where(p => p.Selected).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                var found = await LoadPackagesAsync();
                current = attempt.FindPackage(found);
                ReplacePackages(found);
                foreach (var package in packages.Where(p => p.CanSelect && selected.Contains(p.Id))) package.Selected = true;
                if (current != null && table.Items.Contains(current)) table.SelectedItem = current;
                if (attempt.CanRecheck)
                {
                    attempt.Verify(current, true);
                    if (attempt.Installed != null) SaveHistory(attempt);
                    int sessionIndex = maintenanceAttempts.FindIndex(entry => entry.Id == attempt.Id);
                    if (sessionIndex >= 0) maintenanceAttempts[sessionIndex] = attempt;
                    ShowAttempts(new[] { attempt });
                    status.Text = attempt.Name + ": " + attempt.Result + ".";
                }
                else if (current == null || !current.CanSelect)
                    status.Text = "Для «" + attempt.Name + "» не найдено доступного обновления для повтора.";
                else status.Text = "Доступная версия «" + current.Name + "» проверена.";
                ShowProgress("Проверка завершена", 1, 1, false);
            }
            catch (Exception ex)
            {
                Log("Повторная проверка: " + ex, true);
                status.Text = "Не удалось сверить версии. Попробуйте ещё раз. Подробности в журнале.";
                ShowProgress("Проверка не завершена", 0, 1, false);
                current = null;
            }
            finally
            {
                recoveryMessage.Text = status.Text;
                SetBusy(false);
            }
            if (attempt.CanRetry && current != null && current.CanSelect)
                await InstallUpdatesAsync(new List<Package> { current }, maintenanceAttempts.Any(entry => entry.Id == attempt.Id));
        }

        private async Task ScanAsync()
        {
            maintenanceAttempts.Clear();
            scanningPrograms = true;
            programScanFailed = false;
            SetBusy(true); status.Text = "Проверяем каталог Windows… Это может занять до двух минут.";
            var systemScan = CheckSystemUpdatesAsync();
            ShowProgress("Проверка доступных обновлений…", 0, 1, true);
            try
            {
                ReplacePackages(await LoadPackagesAsync());
                ShowProgress("Проверка завершена", 1, 1, false);
                status.Text = "Показаны программы, распознанные Windows.";
            }
            catch (Win32Exception ex) { programScanFailed = true; progressText.Text = "Проверка не завершена"; status.Text = "WinGet не найден. Установите или обновите «Установщик приложений» в Microsoft Store."; Log(ex.ToString(), true); }
            catch (Exception ex) { programScanFailed = true; progressText.Text = "Проверка не завершена"; Log(ex.ToString(), true); status.Text = ex.Message; }
            finally { scanningPrograms = false; SetBusy(false); }
            await systemScan;
        }

        private async Task UpdateAsync()
        {
            table.CommitEdit(); table.CommitEdit();
            var chosen = packages.Where(p => p.Selected && p.CanSelect).ToList();
            if (chosen.Count == 0) { ShowError("Отметьте программы, которые хотите обновить."); return; }
            await InstallUpdatesAsync(chosen);
        }

        private async Task InstallUpdatesAsync(List<Package> chosen, bool continueSession = false)
        {
            if (BuildProgramConfirmation(chosen).ShowDialog() != true) return;
            if (!continueSession) maintenanceAttempts.Clear();
            maintenanceQueueSize = chosen.Count;
            installing = true; SetBusy(true); int succeeded = 0; int failed = 0;
            ShowProgress("Подготовка обновлений…", 0, chosen.Count, false);
            var successfulIds = new List<string>();
            var attempts = new List<HistoryEntry>();
            try
            {
                foreach (var p in chosen)
                {
                    var history = new HistoryEntry { PackageId = p.Id, Name = p.Name, Before = p.Installed, Requested = p.Available, Result = "Не завершено" };
                    attempts.Add(history);
                    p.LastAttempt = null;
                    p.Status = "Установка…"; status.Text = "Обновляем: " + p.Name + ". Если открылся установщик, завершите его шаги.";
                    ShowProgress(String.Format("Программа {0} из {1}: {2} — установка…", succeeded + failed + 1, chosen.Count, p.Name), succeeded + failed, chosen.Count, false);
                    try
                    {
                        var result = await Task.Run(() => Winget.Run("upgrade --id " + Winget.Quote(p.Id) + " --exact --source winget --interactive --accept-package-agreements --disable-interactivity", -1));
                        bool ok = Winget.UpgradeNeedsVerification(result.Code);
                        Log("Обновление " + p.Id + ": " + result.Code + "\n" + result.Output + "\n" + result.Error, !ok);
                        if (ok)
                        {
                            succeeded++; successfulIds.Add(p.Id);
                            history.Result = "Версия не подтверждена";
                            history.Detail = result.Code == 0 ? "Установщик завершился. Windows пока не подтвердила новую версию." : Winget.UpgradeDetail(result.Code);
                        }
                        else
                        {
                            failed++;
                            history.Detail = Winget.UpgradeDetail(result.Code);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        history.Detail = "Не удалось запустить или дождаться WinGet. Подробности в журнале.";
                        Log(ex.ToString(), true);
                    }
                    p.LastAttempt = history;
                    p.Status = history.Result;
                    SaveHistory(history);
                    p.Selected = false;
                    ShowProgress(String.Format("Обработано программ: {0} из {1}", succeeded + failed, chosen.Count), succeeded + failed, chosen.Count, false);
                }
                selectAll.IsChecked = false;
                installing = false;
                status.Text = "Установка завершена. Автоматически сверяем версии… Если Windows ещё не обновила данные, проверка повторится.";
                ShowProgress("Установка завершена — сверка версий…", chosen.Count, chosen.Count, true);
                var pendingById = attempts.Where(entry => entry.CanRecheck).ToDictionary(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase);
                var verification = await UpdateVerifier.VerifyAsync(successfulIds, LoadPackagesAsync, milliseconds => Task.Delay(milliseconds),
                    (id, found) => !pendingById[id].MatchesInstalledVersion(pendingById[id].FindPackage(found)));
                foreach (var pending in attempts.Where(entry => entry.CanRecheck))
                {
                    pending.Verify(verification.Packages == null ? null : pending.FindPackage(verification.Packages), verification.Error == null);
                    if (pending.Installed != null) SaveHistory(pending);
                }
                if (verification.Packages != null)
                {
                    var selected = new HashSet<string>(packages.Where(p => p.Selected).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                    var active = table.SelectedItem as Package;
                    ReplacePackages(verification.Packages);
                    foreach (var package in packages.Where(p => p.CanSelect && selected.Contains(p.Id))) package.Selected = true;
                    if (active != null)
                        table.SelectedItem = table.Items.Cast<Package>().FirstOrDefault(p => String.Equals(p.Id, active.Id, StringComparison.OrdinalIgnoreCase));
                }
                ShowAttempts(attempts);
                string outcome = String.Format("Установлено: {0}. Не завершено: {1}. Версия не подтверждена: {2}. ",
                    attempts.Count(entry => entry.Installed != null), failed, attempts.Count(entry => entry.CanRecheck));
                if (verification.Error != null)
                {
                    Log("Автоматическая проверка: " + verification.Error, true);
                    status.Text = outcome + "Не удалось подтвердить итоговые версии. " + (verification.Packages == null ? "Список сохранён прежним. " : "Показана последняя успешная проверка. ") + "Повторите проверку позднее.";
                }
                else status.Text = outcome + "Список обновлён автоматически." + (verification.Pending > 0 ? " Неподтверждённые версии можно сверить повторно в списке или в истории." : "");
                ShowProgress(verification.Error != null ? "Установка завершена; итоговые версии не подтверждены" : "Готово — список обновлён", chosen.Count, chosen.Count, false);
            }
            finally
            {
                foreach (var attempt in attempts)
                {
                    int index = maintenanceAttempts.FindIndex(entry => String.Equals(entry.PackageId, attempt.PackageId, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) maintenanceAttempts.Add(attempt);
                    else maintenanceAttempts[index] = attempt;
                }
                maintenanceQueueSize = 0;
                installing = false;
                SetBusy(false);
                if (historyPage.Visibility == Visibility.Visible) RefreshHistory();
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (!SelfUpdate.RunHelper(args))
            {
                SelfUpdate.SyncInstalledVersion(typeof(Program).Assembly.Location);
                new Application().Run(new MainWindow());
            }
        }
    }
}
