using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UpdateHub
{
    public sealed class SystemUpdateReport
    {
        public readonly List<string> Windows = new List<string>();
        public readonly List<string> Nvidia = new List<string>();
        public bool Partial;
        public bool RestartRequired;

        public static SystemUpdateReport Scan()
        {
            dynamic session = null, searcher = null, result = null, updates = null, systemInfo = null;
            try
            {
                session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
                session.ClientApplicationID = "Check Update";
                searcher = session.CreateUpdateSearcher();
                searcher.CanAutomaticallyUpgradeService = false;
                result = searcher.Search("IsInstalled=0 and IsHidden=0");
                int code = result.ResultCode;
                if (code != 2 && code != 3)
                    throw new InvalidOperationException("Windows Update search result: " + code);

                var report = new SystemUpdateReport { Partial = code == 3 };
                updates = result.Updates;
                int count = updates.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = updates.Item(i);
                    try
                    {
                        string title = item.Title;
                        int type = (int)item.Type;
                        bool nvidia = type == 2 && title.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (type == 2 && !nvidia)
                        {
                            try
                            {
                                string manufacturer = item.DriverManufacturer;
                                nvidia = manufacturer != null && manufacturer.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                            }
                            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                            catch (COMException) { }
                        }
                        (nvidia ? report.Nvidia : report.Windows).Add(title);
                    }
                    finally { Release((object)item); }
                }
                try
                {
                    systemInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true));
                    report.RestartRequired = systemInfo.RebootRequired;
                }
                catch (COMException) { }
                return report;
            }
            finally
            {
                Release((object)systemInfo);
                Release((object)updates);
                Release((object)result);
                Release((object)searcher);
                Release((object)session);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    public partial class MainWindow
    {
        private readonly DockPanel systemPage = new DockPanel { Margin = new Thickness(28, 26, 28, 22), Visibility = Visibility.Collapsed };
        private readonly Button systemScanButton = new Button { Content = "Проверить Windows и NVIDIA" };
        private readonly Button nvidiaOnlineButton = new Button { Content = "Проверить каталог NVIDIA", Margin = new Thickness(0, 0, 8, 4) };
        private readonly TextBlock systemStatus = Label("Проверка ещё не запускалась.", 13, "#9CAFC7");
        private readonly StackPanel windowsResults = new StackPanel();
        private readonly StackPanel nvidiaResults = new StackPanel();
        private readonly StackPanel nvidiaCatalogResults = new StackPanel();
        private readonly StackPanel nvidiaNotices = new StackPanel();
        private bool checkingSystem;
        private bool checkingWindows;
        private bool checkingNvidia;
        private bool windowsChecked;
        private bool nvidiaChecked;
        private SystemUpdateReport windowsReport;
        private NvidiaUpdateReport nvidiaReport;

        private void BuildSystemPage()
        {
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            header.Children.Add(Label("Windows и NVIDIA", 27, "#F2F7FF"));
            systemScanButton.HorizontalAlignment = HorizontalAlignment.Left;
            ApplyButtonHelp(systemScanButton, (string)systemScanButton.Content);
            systemScanButton.Margin = new Thickness(0, 10, 0, 8);
            systemScanButton.Click += async (s, e) => await CheckSystemUpdatesAsync();
            header.Children.Add(systemScanButton);
            header.Children.Add(systemStatus);
            DockPanel.SetDock(header, Dock.Top);
            systemPage.Children.Add(header);

            var content = new StackPanel();
            content.Children.Add(SystemCard("Обновления Windows", windowsResults,
                SystemLink("Открыть Windows Update", "ms-settings:windowsupdate")));
            var nvidia = new StackPanel();
            nvidia.Children.Add(nvidiaCatalogResults);
            nvidia.Children.Add(nvidiaNotices);
            nvidia.Children.Add(nvidiaResults);
            nvidia.Children.Add(Label("Запрос к NVIDIA передаёт идентификаторы модели и подсистемы видеокарты, сборку Windows и тип ПК. Он отправляется по кнопке «Проверить каталог NVIDIA».", 12, "#9CAFC7"));
            ApplyButtonHelp(nvidiaOnlineButton, (string)nvidiaOnlineButton.Content);
            nvidiaOnlineButton.Click += async (s, e) => await CheckNvidiaAsync(true);
            var openNvidia = new Button { Content = "Открыть NVIDIA App", Margin = new Thickness(0, 0, 8, 4) };
            ApplyButtonHelp(openNvidia, (string)openNvidia.Content);
            openNvidia.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", @"shell:AppsFolder\com.nvidia.nvapp"); }
                catch (Exception ex) { Log("Открытие NVIDIA App: " + ex); ShowError("Не удалось открыть NVIDIA App. Можно перейти на сайт NVIDIA."); }
            };
            content.Children.Add(SystemCard("Драйверы NVIDIA", nvidia, nvidiaOnlineButton, openNvidia,
                SystemLink("Драйверы на сайте NVIDIA", "https://www.nvidia.com/ru-ru/drivers/")));
            nvidiaCatalogResults.Children.Add(Label("Установленную версию можно проверить локально. Список новых выпусков доступен в каталоге NVIDIA.", 12, "#9CAFC7"));
            var logResults = new StackPanel();
            var logStatus = Label("Короткий замер журнала NVIDIA. Для проверки фоновой записи закройте игру и оставьте компьютер без нагрузки.", 13, "#9CAFC7");
            logResults.Children.Add(logStatus);
            var checkLog = new Button { Content = "Проверить запись журнала" };
            ApplyButtonHelp(checkLog, (string)checkLog.Content);
            checkLog.Click += async (s, e) =>
            {
                checkLog.IsEnabled = false;
                logStatus.Text = "Наблюдаем за журналом NVIDIA 30 секунд…";
                try { logStatus.Text = await Task.Run(() => NvidiaUpdates.CheckLogWritesAsync()); }
                catch (Exception ex)
                {
                    Log("Проверка журнала NVIDIA: " + ex, true);
                    logStatus.Text = "Не удалось прочитать журнал NVIDIA. Подробности в журнале Check Update.";
                }
                finally { checkLog.IsEnabled = true; }
            };
            content.Children.Add(SystemCard("Запись журнала NVIDIA", logResults, checkLog));
            systemPage.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private static Border SystemCard(string title, StackPanel results, params Button[] buttons)
        {
            var content = new StackPanel();
            content.Children.Add(Label(title, 18, "#EDF4FC"));
            results.Margin = new Thickness(0, 8, 0, 10);
            content.Children.Add(results);
            var actions = new WrapPanel();
            foreach (var button in buttons) actions.Children.Add(button);
            content.Children.Add(actions);
            var card = Card(content, "#171C23", new Thickness(14));
            card.Margin = new Thickness(0, 0, 0, 12);
            return card;
        }

        private Button SystemLink(string label, string destination)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4) };
            ApplyButtonHelp(button, label);
            button.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
                catch (Exception ex) { Log("Открытие " + destination + ": " + ex); ShowError("Не удалось открыть «" + label + "»."); }
            };
            return button;
        }

        private async Task CheckSystemUpdatesAsync()
        {
            if (checkingSystem) return;
            checkingSystem = true;
            systemScanButton.IsEnabled = false;
            try { await Task.WhenAll(CheckWindowsAsync(), CheckNvidiaAsync(false)); }
            finally { systemScanButton.IsEnabled = true; checkingSystem = false; }
        }

        private async Task CheckWindowsAsync()
        {
            checkingWindows = true;
            windowsReport = null;
            RefreshMaintenance();
            systemStatus.Text = "Windows Update проверяет доступные обновления…";
            windowsResults.Children.Clear();
            nvidiaResults.Children.Clear();
            var systemSearch = Task.Run(() => SystemUpdateReport.Scan());
            try
            {
                if (await Task.WhenAny(systemSearch, Task.Delay(TimeSpan.FromMinutes(2))) != systemSearch)
                    systemStatus.Text = "Windows Update отвечает дольше обычного. Проверка ещё идёт.";
                var report = windowsReport = await systemSearch;
                systemStatus.Text = (report.Partial ? "Windows Update вернул неполный список." : "Проверка Windows Update завершена.")
                    + (report.RestartRequired ? " Windows требуется перезагрузка." : "");
                ShowSystemResults(windowsResults, report.Windows, report.Partial ? "" : "Доступных обновлений не найдено.");
                if (report.Nvidia.Count > 0)
                {
                    nvidiaResults.Children.Add(Label("Также через Windows Update", 13, "#9CAFC7"));
                    foreach (var title in report.Nvidia) nvidiaResults.Children.Add(Label("• " + title, 13, "#C8D7E9"));
                    nvidiaResults.Children.Add(SystemLink("Дополнительные обновления Windows", "ms-settings:windowsupdate-optionalupdates"));
                }
            }
            catch (Exception ex)
            {
                Log("Проверка Windows Update: " + ex);
                systemStatus.Text = "Не удалось проверить Windows Update. Повторите проверку или откройте настройки Windows.";
                nvidiaResults.Children.Clear();
            }
            finally { checkingWindows = false; windowsChecked = true; RefreshMaintenance(); }
        }

        private async Task CheckNvidiaAsync(bool queryCatalog)
        {
            if (checkingNvidia) return;
            checkingNvidia = true;
            nvidiaOnlineButton.IsEnabled = false;
            nvidiaReport = null;
            RefreshMaintenance();
            nvidiaCatalogResults.Children.Clear();
            nvidiaNotices.Children.Clear();
            nvidiaCatalogResults.Children.Add(Label(queryCatalog ? "Проверяем каталог NVIDIA…" : "Проверяем установленный драйвер…", 13, "#9CAFC7"));
            try
            {
                nvidiaReport = await Task.Run(() => NvidiaUpdates.ScanAsync(queryCatalog));
                ShowSystemResults(nvidiaCatalogResults, nvidiaReport.Lines, "");
                foreach (var version in nvidiaReport.InstalledVersions)
                {
                    string notice = NvidiaUpdates.LoggingNotice(version);
                    if (notice != null) nvidiaNotices.Children.Add(Label(notice, 13, "#EBD3A0"));
                }
                if (nvidiaNotices.Children.Count > 0)
                    nvidiaNotices.Children.Add(SystemLink("Список исправлений NVIDIA", NvidiaUpdates.LoggingFixUrl));
            }
            catch (Exception ex)
            {
                Log("Проверка NVIDIA: " + ex);
                nvidiaCatalogResults.Children.Clear();
                nvidiaCatalogResults.Children.Add(Label("Не удалось проверить драйвер NVIDIA. Повторите проверку или откройте NVIDIA App.", 13, "#9CAFC7"));
            }
            finally { checkingNvidia = false; nvidiaChecked = true; nvidiaOnlineButton.IsEnabled = true; RefreshMaintenance(); }
        }

        private static void ShowSystemResults(StackPanel panel, List<string> titles, string emptyMessage)
        {
            panel.Children.Clear();
            if (titles.Count == 0 && emptyMessage.Length > 0) panel.Children.Add(Label(emptyMessage, 13, "#9CAFC7"));
            foreach (string title in titles) panel.Children.Add(Label("• " + title, 13, "#C8D7E9"));
        }
    }
}
