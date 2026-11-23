using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UpdateHub
{
    public partial class MainWindow
    {
        private readonly TextBlock maintenanceTitle = Label("Сеанс обслуживания", 15, "#EDF4FC");
        private readonly TextBlock maintenanceSummary = Label("", 13, "#D8E8F7");
        private readonly TextBlock maintenanceSystem = Label("", 12, "#A0B0C6");
        private readonly Button maintenanceAction = new Button { MinWidth = 142, Padding = new Thickness(12, 8, 12, 8) };
        private readonly List<HistoryEntry> maintenanceAttempts = new List<HistoryEntry>();
        private bool scanningPrograms;
        private bool programsScanned;
        private bool programScanFailed;
        private int maintenanceQueueSize;
        private HistoryEntry maintenanceRecovery;

        private Border BuildMaintenanceCard()
        {
            var row = new DockPanel();
            maintenanceAction.VerticalAlignment = VerticalAlignment.Center;
            maintenanceAction.Margin = new Thickness(14, 0, 0, 0);
            maintenanceAction.Background = Paint("#184858");
            maintenanceAction.BorderBrush = Paint("#2D7588");
            maintenanceAction.Click += async (s, e) =>
            {
                if (busy || downloadingApp) return;
                if (maintenanceRecovery != null) await RecoverUpdateAsync(maintenanceRecovery);
                else if (maintenanceAttempts.Count == 0 && programsScanned && !programScanFailed)
                {
                    var available = packages.Where(p => p.CanSelect).ToList();
                    if (available.Count > 0) await InstallUpdatesAsync(available);
                }
            };
            DockPanel.SetDock(maintenanceAction, Dock.Right);
            row.Children.Add(maintenanceAction);

            var details = new StackPanel();
            maintenanceTitle.FontWeight = FontWeights.SemiBold;
            maintenanceSummary.Margin = new Thickness(0, 5, 0, 3);
            details.Children.Add(maintenanceTitle);
            details.Children.Add(maintenanceSummary);
            details.Children.Add(maintenanceSystem);
            row.Children.Add(details);
            var card = Card(row, "#142638", new Thickness(15, 10, 15, 11));
            card.BorderBrush = Paint("#2D5368");
            card.Margin = new Thickness(0, 0, 0, 14);
            RefreshMaintenance();
            return card;
        }

        private void RefreshMaintenance()
        {
            maintenanceTitle.Text = "Сеанс обслуживания";
            maintenanceRecovery = null;
            string action = "";
            if (maintenanceQueueSize > 0)
                maintenanceSummary.Text = "В работе программ: " + maintenanceQueueSize;
            else if (maintenanceAttempts.Count > 0)
            {
                maintenanceTitle.Text = "Итог сеанса";
                int failed = maintenanceAttempts.Count(entry => entry.CanRetry);
                int pending = maintenanceAttempts.Count(entry => entry.CanRecheck);
                maintenanceSummary.Text = String.Format("Обновлено: {0} · Не удалось: {1} · Не подтверждено: {2}",
                    maintenanceAttempts.Count - failed - pending, failed, pending);
                maintenanceRecovery = maintenanceAttempts.FirstOrDefault(CanRetryInMaintenance)
                    ?? maintenanceAttempts.FirstOrDefault(entry => entry.CanRecheck);
                if (maintenanceRecovery != null)
                    action = maintenanceRecovery.CanRetry ? "Повторить обновление" : "Сверить версию";
            }
            else if (scanningPrograms)
                maintenanceSummary.Text = "Проверяем обновления программ…";
            else if (programScanFailed)
                maintenanceSummary.Text = "Не удалось проверить программы. Повторите проверку.";
            else if (!programsScanned)
                maintenanceSummary.Text = "Запустите проверку, чтобы увидеть доступные обновления.";
            else
            {
                int available = packages.Count(p => p.CanSelect);
                maintenanceSummary.Text = available == 0 ? "Нет программ, готовых к обновлению." : "Программ к обновлению: " + available;
                if (available > 0) action = "Обновить все программы";
            }

            if (maintenanceRecovery == null) maintenanceSystem.Text = WindowsMaintenanceStatus() + "  ·  " + NvidiaMaintenanceStatus();
            else
            {
                maintenanceSystem.Text = maintenanceRecovery.Name + " — " + (maintenanceRecovery.Detail ?? maintenanceRecovery.Result);
                maintenanceAction.ToolTip = maintenanceSystem.Text;
                if (maintenanceSystem.Text.Length > 160) maintenanceSystem.Text = maintenanceSystem.Text.Substring(0, 157) + "…";
            }
            maintenanceAction.Content = action;
            if (maintenanceRecovery == null)
                maintenanceAction.ToolTip = action.Length > 0 ? "Обновления Windows и драйверы NVIDIA устанавливаются отдельно." : null;
            maintenanceAction.Visibility = action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            maintenanceAction.IsEnabled = !busy && !downloadingApp;
        }

        private bool CanRetryInMaintenance(HistoryEntry entry)
        {
            if (!entry.CanRetry) return false;
            var package = entry.FindPackage(packages);
            return package != null && package.CanSelect;
        }

        private string WindowsMaintenanceStatus()
        {
            if (checkingWindows) return "Windows: проверяем…";
            if (!windowsChecked) return "Windows: ожидает проверки";
            if (windowsReport == null) return "Windows: проверка не удалась";
            int count = windowsReport.Windows.Count + windowsReport.Nvidia.Count;
            string result = windowsReport.Partial ? "неполный список"
                : count == 0 ? "обновлений нет" : "обновлений " + count;
            if (windowsReport.RestartRequired) result += ", нужна перезагрузка";
            return "Windows: " + result;
        }

        private string NvidiaMaintenanceStatus()
        {
            if (checkingNvidia) return "NVIDIA: проверяем…";
            if (!nvidiaChecked) return "NVIDIA: ожидает проверки";
            if ((nvidiaReport != null && nvidiaReport.HasUpdate) || (windowsReport != null && windowsReport.Nvidia.Count > 0))
                return "NVIDIA: доступен драйвер";
            if (nvidiaReport == null) return "NVIDIA: проверка не удалась";
            if (!nvidiaReport.HasAdapter) return "NVIDIA: видеокарта не найдена";
            return nvidiaReport.Complete ? "NVIDIA: драйвер актуален" : "NVIDIA: проверка неполная";
        }
    }
}
