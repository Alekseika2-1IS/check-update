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
[assembly: System.Reflection.AssemblyVersion("0.8.0.0")]

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
        public string Status { get { return Skipped ? "Версия пропущена" : status ?? (CanUpdate ? "Есть обновление" : "Без обновлений"); } set { status = value; Changed("Status"); } }
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
            Func<Task<List<Package>>> scan, Func<int, Task> delay)
        {
            var result = new VerificationResult();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await delay(attempt * 3000);
                try
                {
                    result.Packages = await scan();
                    result.Error = null;
                    result.Pending = updatedIds.Count(id => result.Packages.Any(p => String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) && p.CanUpdate));
                    if (result.Pending == 0) return result;
                }
                catch (Exception ex) { result.Error = ex.Message; }
            }
            return result;
        }
    }

    public static class Winget
    {
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

        public static List<Package> Parse(string output)
        {
            var clean = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
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
            appUpdate.IsEnabled = !value && !checkingVersion && !downloadingApp;
            table.IsEnabled = !value; selectAll.IsEnabled = !value; onlyUpdates.IsEnabled = !value;
            if (!value) progress.IsIndeterminate = false;
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

        private void Log(string text)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)); File.AppendAllText(logFile, DateTime.Now.ToString("u") + " " + text + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private void ShowError(string message) { MessageBox.Show(this, message, "Check Update", MessageBoxButton.OK, MessageBoxImage.Information); }

        private async Task CheckAppVersionAsync()
        {
            if (checkingVersion) return;
            checkingVersion = true; appUpdate.IsEnabled = false; appVersionStatus.Text = "Проверяем новые версии…";
            try
            {
                availableRelease = await SelfUpdate.CheckAsync();
                appVersionStatus.Text = availableRelease == null ? "Новых опубликованных версий нет" : "Доступна версия " + SelfUpdate.ParseVersion(availableRelease.Tag).ToString(3);
                appUpdate.Content = availableRelease == null ? "Проверить версию" : "↓ Скачать обновление";
                appUpdate.Background = availableRelease == null ? Paint("#1B304B") : Paint("#145540");
            }
            catch (Exception ex) { Log("Проверка версии приложения: " + ex); appVersionStatus.Text = "GitHub недоступен — можно проверить позже"; }
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
                appVersionStatus.Text = "Обновление скачано и проверено"; appUpdate.Content = "Перезапустить";
            }
            catch (Exception ex) { Log("Скачивание приложения: " + ex); appVersionStatus.Text = "Скачать не удалось — повторите позже"; ShowError(ex.Message); }
            finally { downloadingApp = false; SetBusy(false); }
            if (downloadedApp != null && MessageBox.Show(this, "Обновление скачано и проверено. Перезапустить Check Update и применить его?", "Обновление Check Update", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { SelfUpdate.ScheduleInstall(downloadedApp); Application.Current.Shutdown(); }
                catch (Exception ex) { Log(ex.ToString()); ShowError("Не удалось запустить обновление. " + ex.Message); }
            }
        }

        private async Task<List<Package>> LoadPackagesAsync()
        {
            var result = await Task.Run(() => Winget.Run("list --source winget --disable-interactivity", 120000));
            Log("Проверка: " + result.Code + "\n" + result.Output + "\n" + result.Error);
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
            foreach (var p in packages) p.PropertyChanged -= SelectionChanged;
            packages.Clear();
            LoadSkippedVersions(found);
            foreach (var p in found) { p.PropertyChanged += SelectionChanged; packages.Add(p); }
            selectAll.IsChecked = false;
            RefreshCounters(); RefreshView();
        }

        private async Task ScanAsync()
        {
            SetBusy(true); status.Text = "Проверяем каталог Windows… Это может занять до двух минут.";
            ShowProgress("Проверка доступных обновлений…", 0, 1, true);
            try
            {
                ReplacePackages(await LoadPackagesAsync());
                ShowProgress("Проверка завершена", 1, 1, false);
                status.Text = "Показаны программы, распознанные Windows.";
            }
            catch (Win32Exception) { progressText.Text = "Проверка не завершена"; status.Text = "WinGet не найден. Установите или обновите «Установщик приложений» в Microsoft Store."; }
            catch (Exception ex) { progressText.Text = "Проверка не завершена"; Log(ex.ToString()); status.Text = ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task UpdateAsync()
        {
            table.CommitEdit(); table.CommitEdit();
            var chosen = packages.Where(p => p.Selected && p.CanSelect).ToList();
            if (chosen.Count == 0) { ShowError("Отметьте программы, которые хотите обновить."); return; }
            string list = String.Join("\n", chosen.Select(p => "• " + p.Name + " → " + p.Available));
            if (MessageBox.Show(this, "Обновить выбранные программы?\n\n" + list + "\n\nСохраните работу в этих программах. Продолжая, вы принимаете лицензионные условия выбранных обновлений. Установщики могут запросить права администратора.", "Подтверждение обновлений", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            installing = true; SetBusy(true); int succeeded = 0; int failed = 0;
            ShowProgress("Подготовка обновлений…", 0, chosen.Count, false);
            var successfulIds = new List<string>();
            var failedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingHistory = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in chosen)
                {
                    var history = new HistoryEntry { Name = p.Name, Before = p.Installed, Requested = p.Available, Result = "Не завершено" };
                    p.Status = "Установка…"; status.Text = "Обновляем: " + p.Name + ". Если открылся установщик, завершите его шаги.";
                    ShowProgress(String.Format("Программа {0} из {1}: {2} — установка…", succeeded + failed + 1, chosen.Count, p.Name), succeeded + failed, chosen.Count, false);
                    try
                    {
                        var result = await Task.Run(() => Winget.Run("upgrade --id " + Winget.Quote(p.Id) + " --exact --source winget --interactive --accept-package-agreements --disable-interactivity", -1));
                        Log("Обновление " + p.Id + ": " + result.Code + "\n" + result.Output + "\n" + result.Error);
                        bool ok = result.Code == 0;
                        p.Status = ok ? "Установлено — сверяем версии…" : "Не завершено — см. журнал";
                        if (ok)
                        {
                            succeeded++; successfulIds.Add(p.Id);
                            history.Result = "Версия не подтверждена";
                            pendingHistory.Add(p.Id, history);
                        }
                        else { failed++; failedIds.Add(p.Id); }
                    }
                    catch (Exception ex) { failed++; failedIds.Add(p.Id); p.Status = "Ошибка — см. журнал"; Log(ex.ToString()); }
                    SaveHistory(history);
                    p.Selected = false;
                    ShowProgress(String.Format("Обработано программ: {0} из {1}", succeeded + failed, chosen.Count), succeeded + failed, chosen.Count, false);
                }
                selectAll.IsChecked = false;
                installing = false;
                status.Text = "Установка завершена. Автоматически сверяем версии… Если Windows ещё не обновила данные, проверка повторится.";
                ShowProgress("Установка завершена — сверка версий…", chosen.Count, chosen.Count, true);
                var verification = await UpdateVerifier.VerifyAsync(successfulIds, LoadPackagesAsync, milliseconds => Task.Delay(milliseconds));
                foreach (var pending in pendingHistory)
                {
                    var observed = verification.Packages == null ? null : verification.Packages.FirstOrDefault(p => String.Equals(p.Id, pending.Key, StringComparison.OrdinalIgnoreCase));
                    pending.Value.Verify(observed, verification.Error == null);
                    if (pending.Value.Installed != null) SaveHistory(pending.Value);
                }
                if (verification.Packages != null)
                {
                    ReplacePackages(verification.Packages);
                    foreach (var p in packages)
                    {
                        if (failedIds.Contains(p.Id)) p.Status = "Не завершено — см. журнал";
                        else if (successfulIds.Contains(p.Id) && p.CanUpdate) p.Status = "Обновление всё ещё доступно";
                    }
                }
                string outcome = String.Format("Успешно: {0}. Не завершено: {1}. ", succeeded, failed);
                if (verification.Error != null)
                {
                    Log("Автоматическая проверка: " + verification.Error);
                    status.Text = outcome + "Не удалось подтвердить итоговые версии. " + (verification.Packages == null ? "Список сохранён прежним. " : "Показана последняя успешная проверка. ") + "Повторите проверку позднее.";
                }
                else status.Text = outcome + "Список обновлён автоматически." + (verification.Pending > 0 ? " Для некоторых программ обновление по-прежнему доступно — Windows могла ещё не обновить данные или установщик не применил новую версию." : "");
                ShowProgress(verification.Error != null ? "Установка завершена; итоговые версии не подтверждены" : "Готово — список обновлён", chosen.Count, chosen.Count, false);
            }
            finally
            {
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
