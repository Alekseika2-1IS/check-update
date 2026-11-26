using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UpdateHub
{
    [DataContract]
    public sealed class ProgramList
    {
        [DataMember(IsRequired = true)] public int Format { get; set; }
        [DataMember(IsRequired = true)] public string[] Programs { get; set; }

        public static string[] Read(Stream file)
        {
            if (file.Length > 1024 * 1024)
                throw new InvalidDataException("Файл списка слишком большой.");
            var list = (ProgramList)new DataContractJsonSerializer(typeof(ProgramList)).ReadObject(file);
            if (list == null || list.Format != 1 || list.Programs == null || list.Programs.Length > 2000)
                throw new InvalidDataException("Выберите файл списка, созданный в Check Update.");
            foreach (string id in list.Programs) Winget.Quote(id);
            return list.Programs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static void Write(Stream file, IEnumerable<Package> packages)
        {
            var ids = packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length > 2000) throw new InvalidDataException("В списке больше 2000 программ.");
            foreach (string id in ids) Winget.Quote(id);
            new DataContractJsonSerializer(typeof(ProgramList)).WriteObject(file,
                new ProgramList { Format = 1, Programs = ids });
        }

        public static string[] Missing(IEnumerable<string> imported, IEnumerable<Package> installed)
        {
            var existing = new HashSet<string>(installed.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            return imported.Where(id => !existing.Contains(id)).ToArray();
        }
    }

    public partial class MainWindow
    {
        private const string ScheduleTimeKey = "DailyUpdateTime";
        private const string LastScheduledDayKey = "LastDailyUpdateDay";
        private readonly DispatcherTimer scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private Button scheduleButton;
        private bool workflowDialogOpen;

        private StackPanel BuildWorkflowControls(Action showUpdates)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var export = WorkflowButton("Сохранить список", async () => { showUpdates(); await TransferProgramsAsync(false); });
            var import = WorkflowButton("Перенести программы", async () => { showUpdates(); await TransferProgramsAsync(true); });
            scheduleButton = WorkflowButton("Расписание обновлений", () => { ConfigureSchedule(); return Task.FromResult(0); });
            panel.Children.Add(export);
            panel.Children.Add(import);
            panel.Children.Add(scheduleButton);
            return panel;
        }

        private Button WorkflowButton(string name, Func<Task> action)
        {
            var button = new Button { Content = name, FontSize = 11, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 5) };
            button.Click += async (s, e) =>
            {
                if (!busy && !downloadingApp && !workflowDialogOpen) await action();
            };
            return button;
        }

        private bool ShowWorkflowConfirmation(Window dialog)
        {
            workflowDialogOpen = true;
            try { return dialog.ShowDialog() == true; }
            finally { workflowDialogOpen = false; }
        }

        private async Task TransferProgramsAsync(bool importing)
        {
            FileDialog picker = importing ? (FileDialog)new OpenFileDialog() : new SaveFileDialog { FileName = "Check Update – список программ.json" };
            picker.Filter = "Список программ (*.json)|*.json";
            picker.DefaultExt = ".json";
            workflowDialogOpen = true;
            try
            {
                if (picker.ShowDialog(this) != true) return;
            }
            finally { workflowDialogOpen = false; }

            SetBusy(true);
            try
            {
                string[] requested = null;
                if (importing)
                    using (var file = File.OpenRead(picker.FileName)) requested = ProgramList.Read(file);
                status.Text = "Проверяем установленные программы…";
                var installed = await LoadPackagesAsync();
                if (!importing)
                {
                    if (installed.Count == 0) { status.Text = "WinGet не нашёл программ для переноса."; return; }
                    using (var file = new MemoryStream())
                    {
                        ProgramList.Write(file, installed);
                        File.WriteAllBytes(picker.FileName, file.ToArray());
                    }
                    status.Text = "Список сохранён. Откройте этот файл через «Перенести программы» на другом ПК.";
                    return;
                }

                var missing = ProgramList.Missing(requested, installed);
                if (missing.Length == 0) { status.Text = "В списке нет программ, которые нужно установить."; return; }
                var preview = new ScrollViewer { MaxHeight = 255, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = Label(String.Join(Environment.NewLine, missing), 13, "#EDF4FC") };
                var dialog = BuildConfirmation("Перенос программ", "Установить недостающие программы?",
                    "Найдено в списке для установки: " + missing.Length, preview,
                    "WinGet установит доступные программы из своего каталога. Данные и настройки программ в список не входят. Продолжая, вы принимаете условия их установщиков.",
                    "Установить", "Отмена", 650);
                if (!ShowWorkflowConfirmation(dialog)) { status.Text = "Перенос отменён."; return; }

                int completed = 0;
                int failures = 0;
                installing = true;
                foreach (string id in missing)
                {
                    ShowProgress("Устанавливаем " + id, completed, missing.Length, false);
                    var result = await Task.Run(() => Winget.Run("install --id " + Winget.Quote(id) +
                        " --exact --source winget --no-upgrade --interactive --accept-package-agreements --disable-interactivity", -1));
                    Log("Перенос " + id + ": " + result.Code + "\n" + result.Output + "\n" + result.Error, result.Code != 0);
                    if (result.Code != 0) failures++;
                    completed++;
                }
                status.Text = String.Format("Перенос завершён. Успешных команд: {0}. Ошибок: {1}. Подробности в журнале.",
                    completed - failures, failures);
                ShowProgress("Перенос завершён", completed, missing.Length, false);
                try { ReplacePackages(await LoadPackagesAsync()); }
                catch (Exception ex) { Log("Проверка после переноса: " + ex, true); }
            }
            catch (Exception ex)
            {
                Log("Перенос списка программ: " + ex, true);
                status.Text = "Не удалось обработать список программ. Подробности в журнале.";
                ShowError(status.Text);
            }
            finally { installing = false; SetBusy(false); }
        }

        private void ConfigureSchedule()
        {
            try
            {
                using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    string savedTime = settings.GetValue(ScheduleTimeKey) as string;
                    var enabled = new CheckBox { Content = "Устанавливать доступные обновления ежедневно", IsChecked = savedTime != null };
                    var time = new TextBox { Text = savedTime ?? "19:00", Width = 90, HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 7, 0, 0) };
                    System.Windows.Automation.AutomationProperties.SetName(time, "Местное время установки");
                    var fields = new StackPanel();
                    fields.Children.Add(enabled);
                    fields.Children.Add(Label("Местное время (ЧЧ:ММ)", 12, "#9CAABB"));
                    fields.Children.Add(time);
                    var dialog = BuildConfirmation("Расписание", "Ежедневные обновления",
                        "Check Update должен быть открыт. Если открыть его позже выбранного времени, проверка начнётся в тот же день.", fields,
                        "После сохранения выбранного расписания WinGet будет обновлять программы без отдельного подтверждения. Пропущенные версии исключаются. Сохраните работу перед выбранным временем: установщик может закрыть обновляемую программу или запросить права.",
                        "Сохранить", "Отмена", 620);
                    if (!ShowWorkflowConfirmation(dialog)) return;
                    DateTime parsed;
                    if (enabled.IsChecked != true) settings.DeleteValue(ScheduleTimeKey, false);
                    else if (DateTime.TryParseExact(time.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                        settings.SetValue(ScheduleTimeKey, parsed.ToString("HH:mm", CultureInfo.InvariantCulture));
                    else { ShowError("Укажите время в формате ЧЧ:ММ, например 19:00. Настройка не сохранена."); return; }
                    RefreshScheduleStatus(settings.GetValue(ScheduleTimeKey) as string);
                }
            }
            catch (Exception ex) { Log("Расписание: " + ex, true); ShowError("Не удалось сохранить расписание."); }
        }

        private void StartUpdateSchedule()
        {
            scheduleTimer.Tick += async (s, e) => await CheckScheduledUpdatesAsync();
            Closed += (s, e) => scheduleTimer.Stop();
            scheduleTimer.Start();
            ScheduleTickOnStartup();
        }

        private async void ScheduleTickOnStartup() { await CheckScheduledUpdatesAsync(); }

        private void RefreshScheduleStatus(string time)
        {
            scheduleButton.Content = time == null ? "Расписание обновлений" : "Расписание · " + time;
            scheduleButton.ToolTip = time == null ? "Расписание выключено." : "Ежедневно в " + time + ", пока Check Update открыт.";
        }

        internal static bool IsScheduleDue(DateTime now, string time, string lastDay)
        {
            DateTime parsed;
            return DateTime.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                && now.TimeOfDay >= parsed.TimeOfDay
                && lastDay != now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private async Task CheckScheduledUpdatesAsync()
        {
            if (busy || downloadingApp || workflowDialogOpen) return;
            bool started = false;
            try
            {
                if (!TryClaimSchedule(DateTime.Now)) return;
                started = true;
                SetBusy(true);
                status.Text = "Проверяем программы по расписанию…";
                ReplacePackages(await LoadPackagesAsync());
                var available = packages.Where(p => p.CanSelect).ToList();
                if (available.Count == 0) { status.Text = "Проверка по расписанию завершена. Обновлений нет."; return; }
                Log("Расписание: начата установка программ: " + available.Count);
                await InstallUpdatesAsync(available, scheduled: true);
            }
            catch (Exception ex)
            {
                Log("Обновления по расписанию: " + ex, true);
                if (started) status.Text = "Не удалось выполнить обновления по расписанию. Подробности в журнале.";
            }
            finally { if (started) SetBusy(false); }
        }

        private bool TryClaimSchedule(DateTime now)
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            using (var gate = new System.Threading.Mutex(false, @"Local\CheckUpdate.DailySchedule." + identity.User.Value))
            {
                try
                {
                    if (!gate.WaitOne(0)) return false;
                }
                catch (System.Threading.AbandonedMutexException) { /* The previous instance exited; this instance owns the mutex. */ }

                try
                {
                    using (var settings = Registry.CurrentUser.OpenSubKey(SettingsKey, true))
                    {
                        string time = settings == null ? null : settings.GetValue(ScheduleTimeKey) as string;
                        RefreshScheduleStatus(time);
                        if (settings == null || !IsScheduleDue(now, time, settings.GetValue(LastScheduledDayKey) as string)) return false;
                        settings.SetValue(LastScheduledDayKey, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        return true;
                    }
                }
                finally { gate.ReleaseMutex(); }
            }
        }
    }
}
