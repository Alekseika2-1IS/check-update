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
        private readonly TextBlock maintenanceSystem = Label("", 12, "#9CAABB");
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
            maintenanceAction.FontSize = 11;
            maintenanceAction.Style = (Style)FindResource("AnimatedButton");
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
            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            actions.Children.Add(update);
            maintenanceAction.Margin = new Thickness(0, 8, 0, 0);
            actions.Children.Add(maintenanceAction);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);

            var details = new StackPanel();
            maintenanceTitle.FontWeight = FontWeights.SemiBold;
            maintenanceTitle.FontSize = 21;
            maintenanceSummary.Margin = new Thickness(0, 5, 0, 3);
            details.Children.Add(maintenanceTitle);
            details.Children.Add(maintenanceSummary);
            details.Children.Add(maintenanceSystem);
            row.Children.Add(details);
            var card = Card(row, "#142638", new Thickness(22, 20, 22, 20));
            card.Background = new System.Windows.Media.LinearGradientBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#203C49"),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#19232F"), 25);
            card.BorderBrush = Paint("#3A5663");
            card.CornerRadius = new CornerRadius(15);
            card.Margin = new Thickness(0, 0, 0, 14);
            RefreshMaintenance();
            return IlluminateCard(card);
        }

        private void RefreshMaintenance()
        {
            maintenanceTitle.Text = "Всё начинается с проверки";
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
            {
                maintenanceTitle.Text = "Ищем новые версии";
                maintenanceSummary.Text = "Проверяем обновления программ…";
            }
            else if (programScanFailed)
                maintenanceSummary.Text = "Не удалось проверить программы. Повторите проверку.";
            else if (!programsScanned)
                maintenanceSummary.Text = "Запустите проверку, чтобы увидеть доступные обновления.";
            else
            {
                int available = packages.Count(p => p.CanSelect);
                maintenanceTitle.Text = available == 0 ? "Программы проверены" : "Доступно обновлений: " + available;
                maintenanceSummary.Text = available == 0 ? "Нет программ, готовых к обновлению." : "Программ к обновлению: " + available;
                if (available > 0) action = "Обновить все программы";
            }

            if (maintenanceRecovery == null) maintenanceSystem.Text = WindowsMaintenanceStatus() + "  ·  " + NvidiaMaintenanceStatus();
            else
            {
                maintenanceSystem.Text = maintenanceRecovery.Name + " — " + (maintenanceRecovery.Detail ?? maintenanceRecovery.Result);
                if (maintenanceSystem.Text.Length > 160) maintenanceSystem.Text = maintenanceSystem.Text.Substring(0, 157) + "…";
            }
            maintenanceAction.Content = action;
            ApplyButtonHelp(maintenanceAction, action);
            if (maintenanceRecovery != null)
                maintenanceAction.ToolTip += Environment.NewLine + maintenanceRecovery.Name + " — " + (maintenanceRecovery.Detail ?? maintenanceRecovery.Result);
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
            if (!nvidiaReport.CatalogRequested) return "NVIDIA: каталог не проверялся";
            return nvidiaReport.Complete ? "NVIDIA: драйвер актуален" : "NVIDIA: проверка неполная";
        }
    }
}
