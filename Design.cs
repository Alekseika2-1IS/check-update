using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace UpdateHub
{
    public class StatusColor : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture)
        {
            string text = value as string ?? "";
            string color = text.Contains("Ошибка") || text.Contains("Не завершено") || text.Contains("всё ещё") || text.Contains("не подтверждена") ? "#FFBF77"
                : text.Contains("Установлено") ? "#72E7A8" : text.Contains("Без обновлений") ? "#9CAFC7" : "#4FD6F5";
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    public partial class MainWindow
    {
        private readonly TextBlock totalCount = new TextBlock();
        private readonly TextBlock updateCount = new TextBlock();
        private readonly TextBlock selectedCount = new TextBlock();
        private readonly TextBlock emptyTitle = new TextBlock();
        private readonly TextBlock emptyDescription = new TextBlock();
        private readonly StackPanel emptyState = new StackPanel();
        private readonly DockPanel historyPage = new DockPanel { Margin = new Thickness(28, 26, 28, 22), Visibility = Visibility.Collapsed };
        private readonly DataGrid historyTable = new DataGrid();
        private readonly TextBlock historyMessage = new TextBlock();
        private readonly TextBlock recoveryMessage = Label("", 12, "#A0B0C6");
        private readonly TextBlock rowAttemptDetail = Label("", 12, "#A0B0C6");
        private readonly Button rowRecovery = new Button();
        private readonly Button historyRecovery = new Button();
        private readonly UpdateHistory updateHistory = new UpdateHistory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHub", "history.jsonl"));
        private bool historyWriteFailed;
        private readonly Button skipVersion = new Button { Content = "Пропустить версию", IsEnabled = false };
        private readonly CheckBox scanOnStartup = new CheckBox();
        private const string SkippedVersionsKey = @"Software\CheckUpdate\SkippedVersions";
        private static Brush Paint(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private static void UseDarkTitleBar(Window window)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static Border Card(UIElement child, string color, Thickness padding)
        {
            return new Border { Child = child, Background = Paint(color), BorderBrush = Paint("#29354A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = padding };
        }

        private static TextBlock Label(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Paint(color), TextWrapping = TextWrapping.Wrap };
        }

        private const string RefreshIcon = "M4,10 A8,8 0 0 1 18.5,7.5 L20,9 M20,4 V9 H15 M20,14 A8,8 0 0 1 5.5,16.5 L4,15 M4,20 V15 H9";
        private const string DownloadIcon = "M12,4 V14 M8,10 L12,14 L16,10 M5,15 V18 A2,2 0 0 0 7,20 H17 A2,2 0 0 0 19,18 V15";

        private static Button NavigationButton(string title, string outline)
        {
            var button = new Button
            {
                Padding = new Thickness(12, 0, 10, 0),
                Margin = new Thickness(0, 0, 0, 5),
                MinHeight = 44,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            SetButtonLabel(button, title, outline, 24, 10);
            return button;
        }

        private static void SetButtonLabel(Button button, string title, string outline = null, double size = 18, double spacing = 7)
        {
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            if (outline == null)
            {
                button.Content = title;
                return;
            }
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(outline),
                StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            var iconSlot = new Canvas { Width = 24, Height = 24 };
            iconSlot.Children.Add(icon);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Viewbox
            {
                Width = size, Height = size, Child = iconSlot,
                Margin = new Thickness(0, 0, spacing, 0), VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }

        private void ShowAppRelease(AppRelease release)
        {
            appVersionStatus.Text = release == null ? "Новых опубликованных версий нет"
                : "Доступна версия " + SelfUpdate.ParseVersion(release.Tag).ToString(3);
            appUpdate.FontSize = release == null ? 12 : 11;
            SetButtonLabel(appUpdate, release == null ? "Проверить версию" : "Скачать обновление",
                release == null ? null : DownloadIcon, 17, 5);
            appUpdate.Background = release == null ? Paint("#1B304B") : Paint("#145540");
        }

        private Border Metric(TextBlock value, string caption, string accent)
        {
            var panel = new StackPanel();
            panel.Children.Add(Label(caption, 12, "#B5C2D6"));
            value.Text = "—";
            value.FontSize = 30;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = Paint(accent);
            value.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(value);
            var content = new Grid();
            var tint = (Color)ColorConverter.ConvertFromString(accent);
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0.3), EndPoint = new Point(1, 0.7), RelativeTransform = new TranslateTransform(-0.75, 0) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0.28));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(36, tint.R, tint.G, tint.B), 0.5));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0.72));
            content.Children.Add(new Border { Background = sheen, Style = (Style)FindResource("MetricShimmer") });
            panel.Margin = new Thickness(16, 12, 16, 12);
            content.Children.Add(panel);
            var card = Card(content, "#151E2E", new Thickness(0));
            card.Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#1A2639"),
                (Color)ColorConverter.ConvertFromString("#131C2B"), 35);
            return card;
        }

        private void BuildLayout(ImageSource logo)
        {
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CheckUpdate.Theme.xaml"))
                Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            Background = Paint("#0E1420"); Foreground = Paint("#EDF4FC");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            SourceInitialized += (s, e) => UseDarkTitleBar(this);

            var shell = new Grid { Background = Background }; shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(218) }); shell.ColumnDefinitions.Add(new ColumnDefinition()); Content = shell;
            var sidebar = new DockPanel { Margin = new Thickness(20, 26, 20, 22) };
            var sidebarSurface = new Border { Background = Paint("#111A29"), BorderBrush = Paint("#29354A"), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
            shell.Children.Add(sidebarSurface);

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 30) };
            brand.Children.Add(new Image { Source = logo, Width = 38, Height = 38 });
            var name = Label("Check Update", 18, "#F2F7FF");
            name.FontWeight = FontWeights.SemiBold;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(10, 0, 0, 0);
            brand.Children.Add(name);
            DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);

            var appCardContent = new StackPanel();
            var version = Label("Версия " + SelfUpdate.CurrentVersion.ToString(3), 15, "#EDF4FC");
            version.FontWeight = FontWeights.SemiBold;
            version.Margin = new Thickness(0, 0, 0, 8);
            appCardContent.Children.Add(version);
            appVersionStatus.Text = "Обновления приложения через GitHub"; appVersionStatus.TextWrapping = TextWrapping.Wrap; appVersionStatus.FontSize = 12; appVersionStatus.Foreground = Paint("#A0B0C6"); appVersionStatus.Margin = new Thickness(0, 0, 0, 12);
            appCardContent.Children.Add(appVersionStatus);
            SetButtonLabel(appUpdate, "Проверить версию"); appUpdate.Padding = new Thickness(8, 10, 8, 10); appUpdate.FontSize = 12;
            appUpdate.Click += async (s, e) => { if (availableRelease == null) await CheckAppVersionAsync(); else await DownloadAppAsync(); };
            appCardContent.Children.Add(appUpdate);
            var appCard = Card(appCardContent, "#172234", new Thickness(13)); DockPanel.SetDock(appCard, Dock.Bottom); sidebar.Children.Add(appCard);

            var navigation = new StackPanel();
            var updatesLink = NavigationButton("Обновления", RefreshIcon);
            var allLink = NavigationButton("Все программы",
                "M5,4 H9 A1,1 0 0 1 10,5 V9 A1,1 0 0 1 9,10 H5 A1,1 0 0 1 4,9 V5 A1,1 0 0 1 5,4 Z " +
                "M15,4 H19 A1,1 0 0 1 20,5 V9 A1,1 0 0 1 19,10 H15 A1,1 0 0 1 14,9 V5 A1,1 0 0 1 15,4 Z " +
                "M5,14 H9 A1,1 0 0 1 10,15 V19 A1,1 0 0 1 9,20 H5 A1,1 0 0 1 4,19 V15 A1,1 0 0 1 5,14 Z " +
                "M15,14 H19 A1,1 0 0 1 20,15 V19 A1,1 0 0 1 19,20 H15 A1,1 0 0 1 14,19 V15 A1,1 0 0 1 15,14 Z");
            var historyLink = NavigationButton("История",
                "M8,4 H16 A2,2 0 0 1 18,6 V18 A2,2 0 0 1 16,20 H8 A2,2 0 0 1 6,18 V6 A2,2 0 0 1 8,4 Z M9.5,8 H14.5 M9.5,12 H14.5 M9.5,16 H13");
            var systemLink = NavigationButton("Windows и NVIDIA",
                "M5,4.5 H19 A2,2 0 0 1 21,6.5 V14.5 A2,2 0 0 1 19,16.5 H5 A2,2 0 0 1 3,14.5 V6.5 A2,2 0 0 1 5,4.5 Z M12,16.5 V20 M8,20 H16");
            var journalLink = NavigationButton("Журнал",
                "M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M7,9 L10,12 L7,15 M13,15 H17");
            var main = new DockPanel { Margin = new Thickness(28, 26, 28, 22) };
            Grid.SetColumn(main, 1); shell.Children.Add(main);
            BuildHistoryPage();
            Grid.SetColumn(historyPage, 1); shell.Children.Add(historyPage);
            BuildSystemPage();
            Grid.SetColumn(systemPage, 1); shell.Children.Add(systemPage);
            BuildJournalPage();
            Grid.SetColumn(journalPage, 1); shell.Children.Add(journalPage);
            var maintenanceCard = BuildMaintenanceCard();
            Action<Button, bool> highlight = (button, selected) =>
            {
                button.Background = selected ? Paint("#123B50") : Brushes.Transparent;
                button.BorderBrush = selected ? Paint("#20617B") : Brushes.Transparent;
                button.Foreground = selected ? Paint("#89E9FC") : Paint("#B5C2D6");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "Выбран раздел" : "");
            };
            Action refreshNavigation = () => {
                bool filtered = onlyUpdates.IsChecked == true;
                maintenanceCard.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
                highlight(updatesLink, main.Visibility == Visibility.Visible && filtered);
                highlight(allLink, main.Visibility == Visibility.Visible && !filtered);
                highlight(historyLink, historyPage.Visibility == Visibility.Visible);
                highlight(systemLink, systemPage.Visibility == Visibility.Visible);
                highlight(journalLink, journalPage.Visibility == Visibility.Visible);
            };
            var pages = new[] { main, historyPage, systemPage, journalPage };
            Action<DockPanel> showPage = selected =>
            {
                foreach (var page in pages) page.Visibility = page == selected ? Visibility.Visible : Visibility.Collapsed;
                refreshNavigation();
            };
            Action<bool> showPrograms = filtered =>
            {
                onlyUpdates.IsChecked = filtered;
                showPage(main); RefreshView();
            };
            updatesLink.Click += (s, e) => showPrograms(true);
            allLink.Click += (s, e) => showPrograms(false);
            historyLink.Click += (s, e) =>
            {
                showPage(historyPage);
                RefreshHistory();
            };
            systemLink.Click += (s, e) => showPage(systemPage);
            Func<System.Threading.Tasks.Task> showJournal = async () =>
            {
                showPage(journalPage);
                await RefreshJournalAsync();
            };
            journalLink.Click += async (s, e) => await showJournal();
            navigation.Children.Add(updatesLink); navigation.Children.Add(allLink); navigation.Children.Add(historyLink);
            navigation.Children.Add(systemLink); navigation.Children.Add(journalLink);
            var settingsLabel = Label("НАСТРОЙКИ", 10, "#758EAE");
            settingsLabel.Margin = new Thickness(0, 24, 0, 10);
            navigation.Children.Add(settingsLabel);
            scanOnStartup.Content = Label("Проверять обновления\nпри запуске", 11, "#A0B0C6");
            scanOnStartup.IsChecked = LoadScanOnStartup();
            scanOnStartup.ToolTip = "При открытии Check Update загрузится список доступных обновлений программ.";
            scanOnStartup.Click += (s, e) => SaveScanOnStartup();
            navigation.Children.Add(scanOnStartup);
            sidebar.Children.Add(navigation);

            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
            SetButtonLabel(scan, "Проверить обновления", RefreshIcon, 20, 7);
            scan.Padding = new Thickness(16, 12, 16, 12); scan.VerticalAlignment = VerticalAlignment.Center;
            scan.Background = Paint("#79DFF4");
            scan.Foreground = Paint("#102332");
            scan.BorderBrush = Paint("#79DFF4");
            scan.Style = (Style)FindResource("AnimatedButton");
            scan.Click += async (s, e) => await ScanAsync(); DockPanel.SetDock(scan, Dock.Right); heading.Children.Add(scan);
            var headingText = new StackPanel();
            var title = Label("Обновления программ", 29, "#F2F7FF");
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 2, 12, 2);
            headingText.Children.Add(title);
            heading.Children.Add(headingText); DockPanel.SetDock(heading, Dock.Top); main.Children.Add(heading);
            DockPanel.SetDock(maintenanceCard, Dock.Top); main.Children.Add(maintenanceCard);

            var metrics = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
            var totalCard = Metric(totalCount, "Программ найдено", "#EDF4FC"); totalCard.Margin = new Thickness(0, 0, 12, 0); metrics.Children.Add(totalCard);
            var updatesCard = Metric(updateCount, "Доступно обновлений", "#3BD4F4"); updatesCard.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(updatesCard, 1); metrics.Children.Add(updatesCard);
            var selectedCard = Metric(selectedCount, "Выбрано для установки", "#74E6A6"); Grid.SetColumn(selectedCard, 2); metrics.Children.Add(selectedCard);
            DockPanel.SetDock(metrics, Dock.Top); main.Children.Add(metrics);

            var actions = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var searchRow = new DockPanel();
            update.Content = "Обновить выбранное"; update.Background = Paint("#184858"); update.Foreground = Paint("#C5F4FC"); update.BorderBrush = Paint("#2D7588"); update.Margin = new Thickness(14, 0, 0, 0); update.Padding = new Thickness(18, 11, 18, 11); update.IsEnabled = false;
            update.Style = (Style)FindResource("AnimatedButton");
            update.Click += async (s, e) => await UpdateAsync(); DockPanel.SetDock(update, Dock.Right); searchRow.Children.Add(update);
            var clearSearch = new Button { Content = "×", FontSize = 19, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(7, 0, 0, 0), ToolTip = "Очистить поиск" };
            clearSearch.Click += (s, e) => { search.Clear(); search.Focus(); }; DockPanel.SetDock(clearSearch, Dock.Right); searchRow.Children.Add(clearSearch);
            var searchHost = new Grid();
            search.ToolTip = "Поиск по названию программы или идентификатору";
            System.Windows.Automation.AutomationProperties.SetName(search, "Поиск программы");
            search.TextChanged += (s, e) => RefreshView(); searchHost.Children.Add(search);
            var placeholder = Label("Поиск программы…", 13, "#8299B5"); placeholder.Margin = new Thickness(13, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
            search.TextChanged += (s, e) => placeholder.Visibility = String.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
            searchHost.Children.Add(placeholder); searchRow.Children.Add(searchHost); actions.Children.Add(searchRow);
            var filters = new DockPanel { Margin = new Thickness(0, 15, 0, 0) };
            summary.Foreground = Paint("#8299B5"); summary.FontSize = 11; summary.VerticalAlignment = VerticalAlignment.Center; summary.Text = "Список ещё не загружен";
            DockPanel.SetDock(summary, Dock.Right); filters.Children.Add(summary);
            var options = new StackPanel { Orientation = Orientation.Horizontal };
            onlyUpdates.Content = "С обновлениями"; onlyUpdates.IsChecked = true;
            onlyUpdates.Checked += (s, e) => refreshNavigation();
            onlyUpdates.Unchecked += (s, e) => refreshNavigation();
            onlyUpdates.Click += (s, e) => RefreshView(); options.Children.Add(onlyUpdates);
            selectAll.Content = "Выбрать показанные"; selectAll.Margin = new Thickness(20, 0, 0, 0);
            selectAll.Click += (s, e) => { foreach (var p in packages.Where(MatchesFilter)) p.Selected = p.CanSelect && selectAll.IsChecked == true; };
            options.Children.Add(selectAll);
            skipVersion.Margin = new Thickness(16, 0, 0, 0);
            skipVersion.Padding = new Thickness(10, 5, 10, 5);
            skipVersion.FontSize = 11;
            skipVersion.ToolTip = "Щёлкните по строке программы, чтобы пропустить эту версию или вернуть её в список.";
            skipVersion.Click += (s, e) => ToggleSkippedVersion();
            options.Children.Add(skipVersion);
            filters.Children.Add(options); actions.Children.Add(filters);
            DockPanel.SetDock(actions, Dock.Top); main.Children.Add(actions);

            var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            var logButton = new Button { Content = "Журнал", Padding = new Thickness(12, 8, 12, 8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 0, 0), FontSize = 12 };
            logButton.Click += async (s, e) => await showJournal();
            DockPanel.SetDock(logButton, Dock.Right); footer.Children.Add(logButton);
            var activity = new StackPanel();
            rowRecovery.HorizontalAlignment = HorizontalAlignment.Left;
            rowRecovery.Margin = new Thickness(0, 4, 0, 6);
            rowRecovery.Click += async (s, e) =>
            {
                var selected = table.SelectedItem as Package;
                if (selected != null) await RecoverUpdateAsync(selected.LastAttempt);
            };
            activity.Children.Add(rowAttemptDetail);
            activity.Children.Add(rowRecovery);
            RefreshRecoveryButtons();
            progressText.Text = "Готово к проверке"; progressText.FontWeight = FontWeights.SemiBold; progressText.Foreground = Paint("#74E6A6"); progressText.TextWrapping = TextWrapping.Wrap; activity.Children.Add(progressText);
            progress.Height = 4; progress.Maximum = 1; progress.Foreground = Paint("#22CBEE"); progress.Background = Paint("#20324A"); progress.BorderThickness = new Thickness(0); progress.Margin = new Thickness(0, 9, 0, 8); activity.Children.Add(progress);
            status.Text = "Проверьте обновления, чтобы загрузить список программ."; status.Foreground = Paint("#92A7C2"); status.FontSize = 11; status.TextWrapping = TextWrapping.Wrap; activity.Children.Add(status);
            footer.Children.Add(activity); DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);

            table.ItemsSource = packages; table.AutoGenerateColumns = false; table.CanUserAddRows = false; table.CanUserDeleteRows = false; table.CanUserReorderColumns = false;
            table.RowHeaderWidth = 0; table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; table.HorizontalGridLinesBrush = Paint("#273449");
            table.HeadersVisibility = DataGridHeadersVisibility.Column; table.RowHeight = 54; table.ColumnHeaderHeight = 40;
            table.Background = Paint("#151E2E"); table.BorderThickness = new Thickness(0); table.SelectionMode = DataGridSelectionMode.Single;
            table.Foreground = Foreground;
            table.SelectionChanged += (s, e) => { RefreshSkipButton(); RefreshRecoveryButtons(); };
            ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
            var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkFactory.SetBinding(CheckBox.IsEnabledProperty, new Binding("CanSelect"));
            checkFactory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkFactory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkFactory.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("Name") { StringFormat = "Выбрать {0}" });
            table.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(44), CellTemplate = new DataTemplate { VisualTree = checkFactory } });
            var namePanel = new FrameworkElementFactory(typeof(StackPanel));
            var programCell = new FrameworkElementFactory(typeof(DockPanel));
            var iconPanel = new FrameworkElementFactory(typeof(Grid));
            iconPanel.SetValue(DockPanel.DockProperty, Dock.Left);
            iconPanel.SetValue(FrameworkElement.WidthProperty, 32.0);
            iconPanel.SetValue(FrameworkElement.HeightProperty, 32.0);
            iconPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            iconPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var initial = new FrameworkElementFactory(typeof(TextBlock));
            initial.SetBinding(TextBlock.TextProperty, new Binding("Initial"));
            initial.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            initial.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            initial.SetValue(TextBlock.ForegroundProperty, Paint("#64D8EF"));
            initial.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            var fallback = new FrameworkElementFactory(typeof(Border));
            fallback.SetValue(Border.BackgroundProperty, Paint("#1A334A"));
            fallback.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            var fallbackStyle = new Style(typeof(Border));
            fallbackStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            var missingIcon = new DataTrigger { Binding = new Binding("Icon"), Value = null };
            missingIcon.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
            fallbackStyle.Triggers.Add(missingIcon);
            fallback.SetValue(FrameworkElement.StyleProperty, fallbackStyle);
            fallback.AppendChild(initial);
            iconPanel.AppendChild(fallback);
            var icon = new FrameworkElementFactory(typeof(Image));
            icon.SetBinding(Image.SourceProperty, new Binding("Icon"));
            icon.SetValue(Image.StretchProperty, Stretch.Uniform);
            iconPanel.AppendChild(icon);
            programCell.AppendChild(iconPanel);
            programCell.AppendChild(namePanel);
            var packageName = new FrameworkElementFactory(typeof(TextBlock)); packageName.SetBinding(TextBlock.TextProperty, new Binding("Name")); packageName.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); packageName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); namePanel.AppendChild(packageName);
            var packageId = new FrameworkElementFactory(typeof(TextBlock)); packageId.SetBinding(TextBlock.TextProperty, new Binding("Id")); packageId.SetValue(TextBlock.ForegroundProperty, Paint("#8299B5")); packageId.SetValue(TextBlock.FontSizeProperty, 10.5); packageId.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0)); packageId.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); namePanel.AppendChild(packageId);
            table.Columns.Add(new DataGridTemplateColumn { Header = "ПРОГРАММА", MinWidth = 220, Width = new DataGridLength(2.3, DataGridLengthUnitType.Star), SortMemberPath = "Name", CellTemplate = new DataTemplate { VisualTree = programCell } });
            AddColumn("УСТАНОВЛЕНО", "Installed", 1); AddColumn("ДОСТУПНО", "Available", 1);
            table.Columns.Add(new DataGridTemplateColumn { Header = "СОСТОЯНИЕ", MinWidth = 170, Width = new DataGridLength(1.65, DataGridLengthUnitType.Star), SortMemberPath = "Status", CellTemplate = StatusBadge("Status") });
            var list = new Grid(); list.Children.Add(table);
            emptyState.VerticalAlignment = VerticalAlignment.Center; emptyState.HorizontalAlignment = HorizontalAlignment.Center; emptyState.Margin = new Thickness(28); emptyState.IsHitTestVisible = false;
            emptyState.Children.Add(new Image { Source = logo, Width = 64, Height = 64, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 18) });
            emptyTitle.Text = "Начнём с проверки"; emptyTitle.FontSize = 20; emptyTitle.FontWeight = FontWeights.SemiBold; emptyTitle.TextAlignment = TextAlignment.Center; emptyState.Children.Add(emptyTitle);
            emptyDescription.Text = "Нажмите «Проверить обновления».\nМы найдём доступные версии ваших программ."; emptyDescription.Foreground = Paint("#92A7C2"); emptyDescription.TextAlignment = TextAlignment.Center; emptyDescription.TextWrapping = TextWrapping.Wrap; emptyDescription.Margin = new Thickness(0, 9, 0, 0); emptyState.Children.Add(emptyDescription);
            list.Children.Add(emptyState);
            var listCard = Card(list, "#151E2E", new Thickness(1)); listCard.ClipToBounds = true; main.Children.Add(listCard);
            CollectionViewSource.GetDefaultView(packages).Filter = o => MatchesFilter((Package)o);
            refreshNavigation();
        }

        private void RefreshView()
        {
            var view = CollectionViewSource.GetDefaultView(packages); view.Refresh();
            int visible = view.Cast<Package>().Count();
            if (packages.Count > 0)
            {
                summary.Text = "Показано: " + visible;
                emptyTitle.Text = "Ничего не найдено";
                emptyDescription.Text = "Попробуйте изменить поиск или фильтр.";
                if (visible == 0 && String.IsNullOrWhiteSpace(search.Text) && onlyUpdates.IsChecked == true)
                {
                    emptyTitle.Text = "Всё проверено";
                    emptyDescription.Text = packages.Any(p => p.Skipped)
                        ? "Пропущенные версии можно вернуть в разделе «Все программы»."
                        : "В каталоге нет доступных обновлений.\nМожно посмотреть все найденные программы.";
                }
            }
            emptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshCounters()
        {
            totalCount.Text = packages.Count.ToString(); updateCount.Text = packages.Count(p => p.CanSelect).ToString(); selectedCount.Text = packages.Count(p => p.Selected).ToString();
            update.IsEnabled = !busy && !downloadingApp && packages.Any(p => p.Selected && p.CanSelect);
        }

        private void LoadSkippedVersions(IEnumerable<Package> found)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SkippedVersionsKey))
                {
                    if (key == null) return;
                    foreach (var package in found.Where(p => p.CanUpdate))
                        package.Skipped = String.Equals(key.GetValue(package.Id) as string, package.Available, StringComparison.Ordinal);
                }
            }
            catch (Exception ex) { Log("Не удалось прочитать пропущенные версии: " + ex.Message); }
        }

        private void RefreshSkipButton()
        {
            var package = table.SelectedItem as Package;
            skipVersion.IsEnabled = !busy && !downloadingApp && package != null && package.CanUpdate;
            skipVersion.Content = package != null && package.Skipped ? "Вернуть обновление" : "Пропустить версию";
        }

        private void ToggleSkippedVersion()
        {
            var package = table.SelectedItem as Package;
            if (busy || downloadingApp || package == null || !package.CanUpdate) return;
            bool skip = !package.Skipped;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SkippedVersionsKey))
                {
                    if (skip) key.SetValue(package.Id, package.Available);
                    else key.DeleteValue(package.Id, false);
                }
            }
            catch (Exception ex)
            {
                Log("Не удалось сохранить пропущенную версию: " + ex.Message);
                ShowError("Не удалось сохранить выбор. Подробности в журнале.");
                return;
            }
            if (skip) package.Selected = false;
            package.Skipped = skip;
            selectAll.IsChecked = false;
            RefreshView();
            RefreshCounters();
            RefreshMaintenance();
            RefreshSkipButton();
            status.Text = skip ? package.Name + " " + package.Available + " пропущена. Вернуть её можно в разделе «Все программы»."
                : "Обновление для " + package.Name + " снова доступно.";
        }

        private void SelectionChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == "Selected") RefreshCounters();
        }

        private void BuildHistoryPage()
        {
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            var title = Label("История обновлений", 27, "#F2F7FF");
            title.FontWeight = FontWeights.SemiBold;
            header.Children.Add(title);
            header.Children.Add(Label("Попытки обновления программ через Check Update", 13, "#9CAFC7"));
            DockPanel.SetDock(header, Dock.Top);
            historyPage.Children.Add(header);
            historyMessage.Foreground = Paint("#A0B0C6");
            historyMessage.TextWrapping = TextWrapping.Wrap;
            historyMessage.Margin = new Thickness(0, 0, 0, 14);
            DockPanel.SetDock(historyMessage, Dock.Top);
            historyPage.Children.Add(historyMessage);
            historyRecovery.HorizontalAlignment = HorizontalAlignment.Left;
            historyRecovery.Margin = new Thickness(0, 4, 0, 8);
            historyRecovery.Click += async (s, e) => await RecoverUpdateAsync(historyTable.SelectedItem as HistoryEntry);
            var historyActions = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            historyActions.Children.Add(recoveryMessage);
            historyActions.Children.Add(historyRecovery);
            DockPanel.SetDock(historyActions, Dock.Top);
            historyPage.Children.Add(historyActions);
            historyTable.SelectionMode = DataGridSelectionMode.Single;
            historyTable.SelectionChanged += (s, e) =>
            {
                var selected = historyTable.SelectedItem as HistoryEntry;
                recoveryMessage.Text = selected == null ? "" : selected.Detail ?? "";
                RefreshRecoveryButtons();
            };

            historyTable.AutoGenerateColumns = false;
            historyTable.IsReadOnly = true;
            historyTable.CanUserAddRows = false;
            historyTable.RowHeaderWidth = 0;
            historyTable.RowHeight = 48;
            historyTable.ColumnHeaderHeight = 42;
            historyTable.HeadersVisibility = DataGridHeadersVisibility.Column;
            historyTable.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            historyTable.HorizontalGridLinesBrush = Paint("#273449");
            historyTable.Background = Paint("#151E2E");
            historyTable.BorderThickness = new Thickness(0);
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            string[] headings = { "ДАТА И ВРЕМЯ", "ПРОГРАММА", "БЫЛА", "НОВАЯ ВЕРСИЯ", "РЕЗУЛЬТАТ" };
            string[] paths = { "Date", "Name", "Before", "Target", "Result" };
            int[] widths = { 135, 145, 75, 125, 175 };
            for (int i = 0; i < headings.Length; i++)
            {
                if (paths[i] == "Result")
                {
                    historyTable.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = headings[i], CellTemplate = StatusBadge("Result"), SortMemberPath = "Result",
                        MinWidth = widths[i], Width = new DataGridLength(widths[i], DataGridLengthUnitType.Star)
                    });
                    continue;
                }
                var style = new Style(typeof(TextBlock), textStyle);
                style.Setters.Add(new Setter(ToolTipProperty, new Binding(paths[i])));
                historyTable.Columns.Add(new DataGridTextColumn
                {
                    Header = headings[i], Binding = new Binding(paths[i]), ElementStyle = style,
                    MinWidth = widths[i], Width = new DataGridLength(widths[i], DataGridLengthUnitType.Star)
                });
            }
            historyPage.Children.Add(Card(historyTable, "#151E2E", new Thickness(1)));
        }

        private void RefreshRecoveryButtons()
        {
            var selected = table.SelectedItem as Package;
            var attempt = selected == null ? null : selected.LastAttempt;
            rowAttemptDetail.Text = attempt == null ? "" : attempt.Detail ?? "";
            rowAttemptDetail.Visibility = String.IsNullOrEmpty(rowAttemptDetail.Text) ? Visibility.Collapsed : Visibility.Visible;
            ConfigureRecoveryButton(rowRecovery, attempt);
            ConfigureRecoveryButton(historyRecovery, historyTable.SelectedItem as HistoryEntry);
        }

        private void ConfigureRecoveryButton(Button button, HistoryEntry attempt)
        {
            button.Visibility = attempt != null && (attempt.CanRetry || attempt.CanRecheck) ? Visibility.Visible : Visibility.Collapsed;
            button.Content = attempt != null && attempt.CanRecheck ? "Сверить версию" : "Повторить обновление";
            button.IsEnabled = !busy && !downloadingApp;
        }

        private DataTemplate StatusBadge(string path)
        {
            var badge = new FrameworkElementFactory(typeof(ContentControl));
            badge.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("StatusBadge"));
            badge.SetBinding(ContentControl.ContentProperty, new Binding(path));
            badge.SetBinding(Control.ForegroundProperty, new Binding(path) { Converter = new StatusColor() });
            badge.SetBinding(ToolTipProperty, new Binding(path == "Status" ? "StatusDetail" : path));
            return new DataTemplate { VisualTree = badge };
        }

        private void SaveHistory(HistoryEntry entry)
        {
            try { updateHistory.Append(entry); }
            catch (Exception ex)
            {
                historyWriteFailed = true;
                Log("Не удалось записать историю обновлений: " + ex.Message);
            }
            if (historyPage.Visibility == Visibility.Visible) RefreshHistory();
        }

        private void RefreshHistory()
        {
            try
            {
                var entries = updateHistory.Read();
                var selected = historyTable.SelectedItem as HistoryEntry;
                historyTable.ItemsSource = entries;
                if (selected != null) historyTable.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selected.Id);
                historyMessage.Text = historyWriteFailed ? "Часть записей не удалось сохранить. Подробности в журнале."
                    : entries.Count == 0 ? "Здесь появятся записи после первой попытки обновления программы."
                    : "«Цель» означает версию, которую пытались установить.";
            }
            catch (Exception ex)
            {
                historyMessage.Text = "Не удалось прочитать историю. Откройте раздел ещё раз.";
                Log("Не удалось прочитать историю обновлений: " + ex.Message);
            }
        }
    }
}
