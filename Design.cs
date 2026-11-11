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
        private readonly UpdateHistory updateHistory = new UpdateHistory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHub", "history.jsonl"));
        private bool historyWriteFailed;
        private readonly Button skipVersion = new Button { Content = "Пропустить версию", IsEnabled = false };
        private const string SkippedVersionsKey = @"Software\CheckUpdate\SkippedVersions";
        private static Brush Paint(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private static Border Card(UIElement child, string color, Thickness padding)
        {
            return new Border { Child = child, Background = Paint(color), BorderBrush = Paint("#263951"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = padding };
        }

        private static TextBlock Label(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Paint(color), TextWrapping = TextWrapping.Wrap };
        }

        private static Border Metric(TextBlock value, string caption, string accent)
        {
            var panel = new StackPanel();
            value.Text = "—"; value.FontSize = 30; value.FontWeight = FontWeights.SemiBold; value.Foreground = Paint(accent);
            panel.Children.Add(value);
            var label = Label(caption, 12, "#A0B0C6"); label.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(label);
            return Card(panel, "#111F33", new Thickness(18, 12, 18, 12));
        }

        private void BuildLayout(ImageSource logo)
        {
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CheckUpdate.Theme.xaml"))
                Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            Background = Paint("#0A1423"); Foreground = Paint("#EDF4FC");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            SourceInitialized += (s, e) => { try { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } };

            var shell = new Grid { Background = Background }; shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(218) }); shell.ColumnDefinitions.Add(new ColumnDefinition()); Content = shell;
            var sidebar = new DockPanel { Margin = new Thickness(20, 26, 20, 22) };
            var sidebarSurface = new Border { Background = Paint("#0D1A2C"), BorderBrush = Paint("#21334B"), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
            shell.Children.Add(sidebarSurface);

            var brand = new StackPanel { Margin = new Thickness(0, 0, 0, 30) };
            brand.Children.Add(new Image { Source = logo, Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Left });
            var name = Label("Check Update", 22, "#F2F7FF"); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 14, 0, 5); brand.Children.Add(name);
            brand.Children.Add(Label("Обновления\nпод контролем", 12, "#94A9C3"));
            DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);

            var appCardContent = new StackPanel();
            var appCaption = Label("↻  CHECK UPDATE", 11, "#56D7EF"); appCaption.FontWeight = FontWeights.SemiBold; appCardContent.Children.Add(appCaption);
            var version = Label("Версия " + SelfUpdate.CurrentVersion.ToString(3), 16, "#EDF4FC"); version.Margin = new Thickness(0, 10, 0, 8); appCardContent.Children.Add(version);
            appVersionStatus.Text = "Обновления приложения через GitHub"; appVersionStatus.TextWrapping = TextWrapping.Wrap; appVersionStatus.FontSize = 12; appVersionStatus.Foreground = Paint("#A0B0C6"); appVersionStatus.Margin = new Thickness(0, 0, 0, 12);
            appCardContent.Children.Add(appVersionStatus);
            appUpdate.Content = "Проверить версию"; appUpdate.Padding = new Thickness(8, 10, 8, 10); appUpdate.FontSize = 12;
            appUpdate.Click += async (s, e) => { if (availableRelease == null) await CheckAppVersionAsync(); else await DownloadAppAsync(); };
            appCardContent.Children.Add(appUpdate);
            var appCard = Card(appCardContent, "#14243A", new Thickness(13)); DockPanel.SetDock(appCard, Dock.Bottom); sidebar.Children.Add(appCard);

            var navigation = new StackPanel();
            navigation.Children.Add(Label("РАБОЧАЯ ОБЛАСТЬ", 10, "#758EAE"));
            var updatesLink = new Button { Content = "↻   Обновления", HorizontalContentAlignment = HorizontalAlignment.Left, Background = Paint("#123B50"), BorderBrush = Paint("#20617B"), Margin = new Thickness(0, 12, 0, 8) };
            var allLink = new Button { Content = "▤   Все программы", Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            var historyLink = new Button { Content = "◷   История", Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 8, 0, 0) };
            var main = new DockPanel { Margin = new Thickness(28, 26, 28, 22) };
            Grid.SetColumn(main, 1); shell.Children.Add(main);
            BuildHistoryPage();
            Grid.SetColumn(historyPage, 1); shell.Children.Add(historyPage);
            Action<Button, bool> highlight = (button, selected) =>
            {
                button.Background = selected ? Paint("#123B50") : Brushes.Transparent;
                button.BorderBrush = selected ? Paint("#20617B") : Brushes.Transparent;
            };
            Action refreshNavigation = () => {
                bool filtered = onlyUpdates.IsChecked == true;
                bool showingHistory = historyPage.Visibility == Visibility.Visible;
                highlight(updatesLink, !showingHistory && filtered);
                highlight(allLink, !showingHistory && !filtered);
                highlight(historyLink, showingHistory);
            };
            Action<bool> showPrograms = filtered =>
            {
                historyPage.Visibility = Visibility.Collapsed;
                main.Visibility = Visibility.Visible;
                onlyUpdates.IsChecked = filtered;
                RefreshView(); refreshNavigation();
            };
            updatesLink.Click += (s, e) => showPrograms(true);
            allLink.Click += (s, e) => showPrograms(false);
            historyLink.Click += (s, e) =>
            {
                main.Visibility = Visibility.Collapsed;
                historyPage.Visibility = Visibility.Visible;
                RefreshHistory(); refreshNavigation();
            };
            navigation.Children.Add(updatesLink); navigation.Children.Add(allLink); navigation.Children.Add(historyLink);
            var catalogNote = Label("Проверка программ\nчерез каталог Windows", 11, "#758EAE"); catalogNote.Margin = new Thickness(0, 24, 0, 0); navigation.Children.Add(catalogNote);
            sidebar.Children.Add(navigation);

            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
            scan.Content = "↻  Проверить обновления"; scan.Padding = new Thickness(16, 12, 16, 12); scan.VerticalAlignment = VerticalAlignment.Center;
            scan.Click += async (s, e) => await ScanAsync(); DockPanel.SetDock(scan, Dock.Right); heading.Children.Add(scan);
            var headingText = new StackPanel();
            headingText.Children.Add(Label("ВАШ КОМПЬЮТЕР", 10, "#5FCBE7"));
            var title = Label("Обновления программ", 27, "#F2F7FF"); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 5, 12, 4); headingText.Children.Add(title);
            headingText.Children.Add(Label("Новые версии — в одном месте", 12, "#9CAFC7"));
            heading.Children.Add(headingText); DockPanel.SetDock(heading, Dock.Top); main.Children.Add(heading);

            var metrics = new Grid { Margin = new Thickness(0, 0, 0, 22) };
            for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
            var totalCard = Metric(totalCount, "Программ найдено", "#EDF4FC"); totalCard.Margin = new Thickness(0, 0, 12, 0); metrics.Children.Add(totalCard);
            var updatesCard = Metric(updateCount, "Доступно обновлений", "#3BD4F4"); updatesCard.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(updatesCard, 1); metrics.Children.Add(updatesCard);
            var selectedCard = Metric(selectedCount, "Выбрано для установки", "#74E6A6"); Grid.SetColumn(selectedCard, 2); metrics.Children.Add(selectedCard);
            DockPanel.SetDock(metrics, Dock.Top); main.Children.Add(metrics);

            var actions = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var searchRow = new DockPanel();
            update.Content = "Обновить выбранное"; update.Background = Paint("#20C9EB"); update.Foreground = Paint("#06202C"); update.BorderBrush = Paint("#20C9EB"); update.Margin = new Thickness(14, 0, 0, 0); update.Padding = new Thickness(18, 11, 18, 11); update.IsEnabled = false;
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
            onlyUpdates.Click += (s, e) => { RefreshView(); refreshNavigation(); }; options.Children.Add(onlyUpdates);
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
            logButton.Click += (s, e) => { try { Log("Журнал открыт"); System.Diagnostics.Process.Start("notepad.exe", "\"" + logFile + "\""); } catch (Exception ex) { ShowError(ex.Message); } };
            DockPanel.SetDock(logButton, Dock.Right); footer.Children.Add(logButton);
            var activity = new StackPanel();
            progressText.Text = "Готово к проверке"; progressText.FontWeight = FontWeights.SemiBold; progressText.Foreground = Paint("#74E6A6"); progressText.TextWrapping = TextWrapping.Wrap; activity.Children.Add(progressText);
            progress.Height = 4; progress.Maximum = 1; progress.Foreground = Paint("#22CBEE"); progress.Background = Paint("#20324A"); progress.BorderThickness = new Thickness(0); progress.Margin = new Thickness(0, 9, 0, 8); activity.Children.Add(progress);
            status.Text = "Проверьте обновления, чтобы загрузить список программ."; status.Foreground = Paint("#92A7C2"); status.FontSize = 11; status.TextWrapping = TextWrapping.Wrap; activity.Children.Add(status);
            footer.Children.Add(activity); DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);

            table.ItemsSource = packages; table.AutoGenerateColumns = false; table.CanUserAddRows = false; table.CanUserDeleteRows = false; table.CanUserReorderColumns = false;
            table.RowHeaderWidth = 0; table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; table.HorizontalGridLinesBrush = Paint("#21324A");
            table.HeadersVisibility = DataGridHeadersVisibility.Column; table.RowHeight = 64; table.ColumnHeaderHeight = 42;
            table.Background = Paint("#111F33"); table.BorderThickness = new Thickness(0); table.SelectionMode = DataGridSelectionMode.Single;
            table.Foreground = Foreground;
            table.SelectionChanged += (s, e) => RefreshSkipButton();
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
            var badge = new FrameworkElementFactory(typeof(Border)); badge.SetValue(Border.BackgroundProperty, Paint("#162F41")); badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); badge.SetValue(Border.PaddingProperty, new Thickness(9, 5, 9, 5)); badge.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            var badgeText = new FrameworkElementFactory(typeof(TextBlock)); badgeText.SetBinding(TextBlock.TextProperty, new Binding("Status")); badgeText.SetBinding(TextBlock.ForegroundProperty, new Binding("Status") { Converter = new StatusColor() }); badgeText.SetValue(TextBlock.FontSizeProperty, 11.0); badgeText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); badgeText.SetBinding(TextBlock.ToolTipProperty, new Binding("Status")); badge.AppendChild(badgeText);
            table.Columns.Add(new DataGridTemplateColumn { Header = "СОСТОЯНИЕ", MinWidth = 170, Width = new DataGridLength(1.65, DataGridLengthUnitType.Star), SortMemberPath = "Status", CellTemplate = new DataTemplate { VisualTree = badge } });
            var list = new Grid(); list.Children.Add(table);
            emptyState.VerticalAlignment = VerticalAlignment.Center; emptyState.HorizontalAlignment = HorizontalAlignment.Center; emptyState.Margin = new Thickness(28); emptyState.IsHitTestVisible = false;
            emptyState.Children.Add(new Image { Source = logo, Width = 64, Height = 64, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 18) });
            emptyTitle.Text = "Начнём с проверки"; emptyTitle.FontSize = 20; emptyTitle.FontWeight = FontWeights.SemiBold; emptyTitle.TextAlignment = TextAlignment.Center; emptyState.Children.Add(emptyTitle);
            emptyDescription.Text = "Нажмите «Проверить обновления».\nМы найдём доступные версии ваших программ."; emptyDescription.Foreground = Paint("#92A7C2"); emptyDescription.TextAlignment = TextAlignment.Center; emptyDescription.TextWrapping = TextWrapping.Wrap; emptyDescription.Margin = new Thickness(0, 9, 0, 0); emptyState.Children.Add(emptyDescription);
            list.Children.Add(emptyState);
            var listCard = Card(list, "#111F33", new Thickness(0)); listCard.ClipToBounds = true; main.Children.Add(listCard);
            CollectionViewSource.GetDefaultView(packages).Filter = o => MatchesFilter((Package)o);
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

            historyTable.AutoGenerateColumns = false;
            historyTable.IsReadOnly = true;
            historyTable.CanUserAddRows = false;
            historyTable.RowHeaderWidth = 0;
            historyTable.RowHeight = 48;
            historyTable.ColumnHeaderHeight = 42;
            historyTable.HeadersVisibility = DataGridHeadersVisibility.Column;
            historyTable.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            historyTable.HorizontalGridLinesBrush = Paint("#21324A");
            historyTable.Background = Paint("#111F33");
            historyTable.BorderThickness = new Thickness(0);
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            string[] headings = { "ДАТА И ВРЕМЯ", "ПРОГРАММА", "БЫЛА", "НОВАЯ ВЕРСИЯ", "РЕЗУЛЬТАТ" };
            string[] paths = { "Date", "Name", "Before", "Target", "Result" };
            int[] widths = { 135, 145, 75, 125, 175 };
            for (int i = 0; i < headings.Length; i++)
            {
                var style = new Style(typeof(TextBlock), textStyle);
                style.Setters.Add(new Setter(ToolTipProperty, new Binding(paths[i])));
                if (i == 4) style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("Result") { Converter = new StatusColor() }));
                historyTable.Columns.Add(new DataGridTextColumn
                {
                    Header = headings[i], Binding = new Binding(paths[i]), ElementStyle = style,
                    MinWidth = widths[i], Width = new DataGridLength(widths[i], DataGridLengthUnitType.Star)
                });
            }
            historyPage.Children.Add(Card(historyTable, "#111F33", new Thickness(0)));
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
                historyTable.ItemsSource = entries;
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
