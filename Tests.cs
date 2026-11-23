using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UpdateHub;

public static class Tests
{
    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static string Row(string name, string id, string version, string available) { return name.PadRight(25) + id.PadRight(30) + version.PadRight(18) + available; }
    static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(stream);
    }
    [STAThread]
    public static void Main(string[] args)
    {
        CheckRateLimitResponse();
        CheckShortcutIcon();
        Check(NvidiaUpdates.InstalledVersion("32.0.16.2124") == new Version(621, 24)
            && NvidiaUpdates.InstalledVersion("32.0.15.6094") == new Version(560, 94)
            && NvidiaUpdates.InstalledVersion("32.0") == null, "NVIDIA Windows driver versions convert to catalog versions");
        string hardware = "2486_10DE_87C6_1043";
        string offer = "{\"criteria\":{\"IsSupported\":{\"state\":\"true\",\"DeviceID\":\"2486\",\"Vendor\":\"10DE\",\"SubSystemID\":\"87C6\",\"SubVendorID\":\"1043\"}},\"DriverAttributes\":{\"Version\":\"621.24\",\"IsCRD\":1,\"IsBeta\":0,\"IsDC\":1}}";
        Check(NvidiaUpdates.ReadOffer(offer, hardware, true) == new Version(621, 24)
            && NvidiaUpdates.ReadOffer(offer, hardware, false) == null
            && NvidiaUpdates.ReadOffer(offer, "2486_10DE_0000_1043", true) == null
            && NvidiaUpdates.ReadOffer(offer.Replace("\"IsBeta\":0", "\"IsBeta\":1"), hardware, true) == null,
            "NVIDIA offers must match adapter and channel and exclude beta");
        Check(Winget.UpgradeNeedsVerification(unchecked((int)0x8A150109))
            && !Winget.UpgradeNeedsVerification(unchecked((int)0x8A15010A)), "WinGet distinguishes a pending restart from an installation that should be retried");
        string historyPath = Path.Combine(Path.GetTempPath(), "CheckUpdate-history-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var history = new UpdateHistory(historyPath);
            var entry = new HistoryEntry { Name = "Пример", Before = "1.0", Requested = "2.0", Result = "Версия не подтверждена" };
            history.Append(entry);
            entry.Verify(new Package { Installed = "1.0", Available = "2.0" }, true);
            Check(entry.Installed == null, "stale version is not recorded as installed");
            File.AppendAllText(historyPath, "{");
            entry.Verify(new Package { Installed = "2.0" }, true);
            history.Append(entry);
            var failed = new HistoryEntry { Name = "Другая программа", Before = "2.0", Requested = "3.0", Result = "Не завершено" };
            history.Append(failed);
            var reopened = new UpdateHistory(historyPath).Read();
            Check(reopened.Count == 2 && reopened.Any(row => row.Id == entry.Id && row.Target == "2.0" && row.Result == "Установлено")
                && reopened.Any(row => row.Id == failed.Id && row.Result == "Не завершено"), "history survives restart and a truncated line without duplicate attempts");
            var matching = new Package { Name = "Другая программа", Id = "Vendor.App" };
            var other = new Package { Name = matching.Name, Id = "Other.App" };
            Check(failed.CanRetry && !failed.CanRecheck && failed.FindPackage(new[] { matching, other }) == null,
                "legacy recovery rejects ambiguous program names");
            failed.PackageId = matching.Id;
            Check(failed.FindPackage(new[] { matching, other }) == matching, "recovery uses the saved package ID");
        }
        finally { File.Delete(historyPath); }
        string header = Row("Name", "Id", "Version", "Available") + "\n" + new string('-', 90) + "\n";
        var parsed = Winget.Parse(header + Row("Пример приложения", "Vendor.Example", "< 2.0", "3.0") + "\n" + Row("Other", "Vendor.Other", "1.0", ""));
        Check(parsed.Count == 2 && parsed.Any(p => p.Id == "Vendor.Example" && p.Available == "3.0" && p.Installed == "< 2.0"), "localized names and versions");
        Check(parsed.Count(p => p.CanUpdate) == 1 && parsed.All(p => !p.Selected), "updates optional and unselected by default");
        parsed = Winget.Parse(header + Row("Truncated", "Vendor.Long…", "1", "2") + "\n" + Row("Safe", "Vendor.Safe", "1", "2"));
        Check(parsed.Count == 1 && parsed[0].Id == "Vendor.Safe", "clipped identifiers excluded");
        parsed = Winget.Parse(header + Row("One", "Vendor.Same", "1", "") + "\n" + Row("One", "Vendor.Same", "1", "2"));
        Check(parsed.Count == 1 && parsed[0].CanUpdate, "duplicate targets merged");
        string three = "Name                     Id                            Version\n" + new string('-', 80) + "\n" + Row("Example", "Vendor.Example", "1", "");
        Check(!Winget.Parse(three)[0].CanUpdate, "table without update column");
        bool rejected = false; try { Winget.Parse("Unexpected response"); } catch (FormatException) { rejected = true; }
        Check(rejected, "unexpected output fails visibly");
        rejected = false; try { Winget.Quote("Vendor.App --all"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid target rejected");
        int scans = 0; int waits = 0;
        var verified = UpdateVerifier.VerifyAsync(new[] { "Vendor.App" }, () => {
            scans++;
            return Task.FromResult(new System.Collections.Generic.List<Package> { new Package { Id = "Vendor.App", Installed = scans == 1 ? "1" : "2", Available = scans == 1 ? "2" : "" } });
        }, milliseconds => { waits++; return Task.FromResult(0); }).GetAwaiter().GetResult();
        Check(scans == 2 && waits == 1 && verified.Pending == 0 && !verified.Packages[0].CanUpdate, "delayed Windows data refreshed automatically");
        scans = 0;
        verified = UpdateVerifier.VerifyAsync(new[] { "Vendor.App" }, () => {
            scans++;
            return Task.FromResult(new System.Collections.Generic.List<Package> { new Package { Id = "Vendor.App", Available = "2" } });
        }, milliseconds => Task.FromResult(0)).GetAwaiter().GetResult();
        Check(scans == 4 && verified.Pending == 1, "unconfirmed updates remain visible with bounded retries");
        scans = 0;
        verified = UpdateVerifier.VerifyAsync(new[] { "Vendor.App" }, () => {
            scans++; throw new IOException("offline");
        }, milliseconds => Task.FromResult(0)).GetAwaiter().GetResult();
        Check(scans == 4 && verified.Packages == null && verified.Error != null, "scan failure does not fabricate successful removal");
        scans = 0;
        verified = UpdateVerifier.VerifyAsync(new[] { "Vendor.App" }, () => {
            scans++;
            if (scans == 1) throw new IOException("temporary");
            return Task.FromResult(new System.Collections.Generic.List<Package> { new Package { Id = "Vendor.App", Installed = "2", Available = "" } });
        }, milliseconds => Task.FromResult(0)).GetAwaiter().GetResult();
        Check(scans == 2 && verified.Error == null && verified.Pending == 0, "temporary scan failure recovers");
        Check(SelfUpdate.ParseVersion("v1.2.3") == new Version(1, 2, 3, 0) && SelfUpdate.ParseVersion("v1.2.3-beta") == null, "stable release version parsing");
        Check(!SelfUpdate.IsTrustedAsset("https://github.com/other/app/releases/download/v1/a.exe") && !SelfUpdate.IsTrustedAsset("http://github.com/" + SelfUpdate.Repository + "/releases/download/v1/a.exe"), "foreign update sources rejected");
        string releaseJson = "{\"tag_name\":\"v9.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Check-Update.exe\",\"size\":100,\"browser_download_url\":\"https://github.com/" + SelfUpdate.Repository + "/releases/download/v9.0.0/Check-Update.exe\"},{\"name\":\"Check-Update.exe.sha256\",\"size\":64,\"browser_download_url\":\"https://github.com/" + SelfUpdate.Repository + "/releases/download/v9.0.0/Check-Update.exe.sha256\"}]}";
        Check(SelfUpdate.ParseRelease(releaseJson, new Version(1, 0, 0, 0)) != null, "new release with expected assets detected");
        Check(SelfUpdate.ParseRelease(releaseJson, new Version(9, 0, 0, 0)) == null && SelfUpdate.ParseRelease(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(1, 0)) == null, "current versions and previews ignored");
        rejected = false; try { SelfUpdate.ParseRelease(releaseJson.Replace("Check-Update.exe.sha256", "wrong.txt"), new Version(1, 0)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "release missing checksum rejected");
        string temp = Path.Combine(Path.GetTempPath(), "CheckUpdate-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        string source = Path.Combine(temp, "new.exe"), destination = Path.Combine(temp, "app.exe");
        File.WriteAllText(source, "new version"); File.WriteAllText(destination, "old version");
        bool started = false;
        SelfUpdate.ReplaceAndRestart(source, destination, SelfUpdate.Hash(source), path => { started = File.ReadAllText(path) == "new version"; });
        Check(started && File.ReadAllText(destination + ".previous") == "old version", "update replaces file and preserves previous version");
        File.WriteAllText(destination, "restore this version");
        try { SelfUpdate.ReplaceAndRestart(source, destination, SelfUpdate.Hash(source), path => { throw new IOException("restart failed"); }); } catch (IOException) { }
        Check(File.ReadAllText(destination) == "restore this version", "restart failure restores previous executable");
        rejected = false; try { SelfUpdate.ReplaceAndRestart(source, destination, new string('0', 64), path => { }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllText(destination) == "restore this version", "checksum mismatch leaves installed file intact");
        File.Delete(source); File.Delete(destination); File.Delete(destination + ".previous"); Directory.Delete(temp);
        bool offline = args.Contains("--offline");
        var real = parsed;
        if (!offline)
        {
            var result = Winget.Run("list --source winget --disable-interactivity", 120000);
            Check(result.Code == 0, "real read-only winget scan");
            real = Winget.Parse(result.Output);
            Check(real.Count > 0, "real packages parsed");
        }
        Console.WriteLine("Recognized: " + real.Count + ", with updates: " + real.Count(p => p.CanUpdate));
        var app = new Application();
        var window = new MainWindow(!offline) { ShowActivated = false, Left = -10000, Top = -10000 };
        if (!offline)
        {
            var startup = (System.Windows.Controls.CheckBox)typeof(MainWindow).GetField("scanOnStartup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            startup.IsChecked = true;
        }
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        window.Show();
        Capture(window, "preview-empty.png");
        if (!offline)
        {
            var frame = new DispatcherFrame();
            var busyField = typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
            var deadline = DateTime.UtcNow.AddSeconds(130);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (sender, e) =>
            {
                if (!(bool)busyField.GetValue(window) || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Check(!(bool)busyField.GetValue(window), "startup scan finishes");
        }
        else
        {
            typeof(MainWindow).GetMethod("ReplacePackages", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { real });
            typeof(MainWindow).GetMethod("ShowProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { "Проверка завершена", 1, 1, false });
        }
        var field = typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(((System.Collections.ICollection)field.GetValue(window)).Count > 0, offline ? "window displays offline fixture" : "startup scan fills the list without a manual check");
        var items = (System.Collections.ObjectModel.ObservableCollection<Package>)field.GetValue(window);
        var search = (System.Windows.Controls.TextBox)typeof(MainWindow).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
        var target = items.First();
        var onlyUpdates = (System.Windows.Controls.CheckBox)typeof(MainWindow).GetField("onlyUpdates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        if (!items.Any(p => p.CanUpdate)) { onlyUpdates.IsChecked = false; view.Refresh(); }
        else target = items.First(p => p.CanUpdate);
        search.Text = target.Id.ToLowerInvariant();
        Check(view.Cast<Package>().Count() == 1 && view.Cast<Package>().First().Id == target.Id, "search finds identifier regardless of case");
        var select = (System.Windows.Controls.CheckBox)typeof(MainWindow).GetField("selectAll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        select.IsChecked = true; select.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(items.Count(p => p.Selected) == (target.CanUpdate ? 1 : 0) && target.Selected == target.CanUpdate, "select all only selects visible updateable results");
        var selectedCounter = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("selectedCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        Check(selectedCounter.Text == items.Count(p => p.Selected).ToString(), "selected counter follows selection through search");
        CheckUpdateControls(window, select);
        foreach (var p in items) p.Selected = false; select.IsChecked = false;
        search.Text = "no-such-program-xyz";
        Check(!view.Cast<Package>().Any(), "unmatched search returns empty view");
        Capture(window, "preview-search-empty.png");
        search.Clear();
        Check(view.Cast<Package>().Count() == items.Count(p => onlyUpdates.IsChecked != true || p.CanUpdate), "clearing search restores active filter");
        if (target.CanUpdate)
        {
            target.Skipped = true;
            onlyUpdates.IsChecked = true;
            view.Refresh();
            Check(!target.CanSelect && !view.Cast<Package>().Contains(target), "skipped version is hidden from updates and excluded from selection");
            onlyUpdates.IsChecked = false;
            view.Refresh();
            Check(view.Cast<Package>().Contains(target), "skipped version remains accessible in all programs");
            target.Skipped = false;
            onlyUpdates.IsChecked = true;
            view.Refresh();
            Check(target.CanSelect && view.Cast<Package>().Contains(target), "restoring skipped version returns it to updates");
        }
        var meter = (System.Windows.Controls.ProgressBar)typeof(MainWindow).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        Check(!meter.IsIndeterminate && meter.Value == meter.Maximum, "scan progress completes");
        typeof(MainWindow).GetMethod("ShowProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { "Queue test", 1, 3, false });
        Check(meter.Value == 1 && meter.Maximum == 3 && !meter.IsIndeterminate, "queue progress measures completed programs");
        typeof(MainWindow).GetMethod("ShowProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { "Проверка завершена", 1, 1, false });
        Capture(window, "preview.png");
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        Capture(window, "preview-small.png");
        CheckVisualEffects(window);
        var table = (System.Windows.Controls.DataGrid)typeof(MainWindow).GetField("table", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        var action = (System.Windows.Controls.Button)typeof(MainWindow).GetField("rowRecovery", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        var attempt = new HistoryEntry { PackageId = target.Id, Requested = target.Available, Result = "Не завершено", Detail = "Закройте программу." };
        target.LastAttempt = attempt;
        table.SelectedItem = null;
        table.SelectedItem = target;
        Check(action.Visibility == Visibility.Visible && (string)action.Content == "Повторить обновление", "failed program row offers individual retry");
        attempt.Result = "Версия не подтверждена";
        typeof(MainWindow).GetMethod("RefreshRecoveryButtons", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
        Check((string)action.Content == "Сверить версию", "unconfirmed program row offers verification");
        if (!offline)
        {
            var checkingSystem = typeof(MainWindow).GetField("checkingSystem", BindingFlags.Instance | BindingFlags.NonPublic);
            var deadline = DateTime.UtcNow.AddSeconds(130);
            while ((bool)checkingSystem.GetValue(window) && DateTime.UtcNow < deadline) WaitForAnimation(100);
            Check(!(bool)checkingSystem.GetValue(window), "system checks finish independently of the program scan");
        }
        if (target.CanUpdate) CheckMaintenanceCard(window, target);
        CheckJournal(window);
        CheckSidebarNavigation(window);
        CheckConfirmations(window);
        window.Close(); app.Shutdown();
        Console.WriteLine("All " + checks + " checks passed. No updates installed.");
    }

    private static void CheckJournal(MainWindow window)
    {
        var file = Path.Combine(Path.GetTempPath(), "CheckUpdate-journal-" + Guid.NewGuid().ToString("N") + ".log");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pathField = typeof(MainWindow).GetField("logFile", flags);
        var logPath = pathField.GetValue(window);
        string fixture = "2026-11-24 10:15:10Z Проверка: 0\r\nКаталог успешно прочитан.\r\n" +
            "2026-11-24 10:15:22Z Обновление Example.App: 1603\r\n\u001b[31mУстановщик вернул ошибку.\u001b[0m\r\nСохраните работу и закройте программу.\r\n" +
            "2026-11-24 10:16:12Z [Ошибка] Проверка Windows Update: нет подключения.\r\n";
        try
        {
            Check(JournalSnapshot.Read(file).Entries.Count == 0, "missing activity log is an empty journal");
            File.WriteAllText(file, fixture);
            var snapshot = JournalSnapshot.Read(file);
            Check(snapshot.Entries.Count == 3 && !snapshot.Entries[0].IsError && snapshot.Entries.Skip(1).All(entry => entry.IsError)
                && snapshot.Entries[1].Message.Contains("Сохраните работу") && !snapshot.Entries[1].Message.Contains("\u001b"),
                "journal keeps multiline details and detects legacy and marked errors");
            Check(!new JournalEntry(null, "Обновление Example.App: -1978334967").IsError,
                "journal does not flag a successful installation that requires a restart");
            File.WriteAllText(file, new string('я', 150000) + "\r\n" + fixture);
            snapshot = JournalSnapshot.Read(file);
            Check(snapshot.Truncated && snapshot.Entries.Count == 3 && snapshot.Entries[0].Timestamp != null,
                "large logs show recent complete entries");
            File.WriteAllText(file, fixture);
            pathField.SetValue(window, file);

            var shell = (System.Windows.Controls.Grid)window.Content;
            var sidebar = (System.Windows.Controls.DockPanel)((System.Windows.Controls.Border)shell.Children[0]).Child;
            var links = ((System.Windows.Controls.StackPanel)sidebar.Children[2]).Children.OfType<System.Windows.Controls.Button>().ToArray();
            links.Last().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var reading = typeof(MainWindow).GetField("readingJournal", flags);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((bool)reading.GetValue(window) && DateTime.UtcNow < deadline) WaitForAnimation(30);
            Check(!(bool)reading.GetValue(window) && shell.Children[4].IsVisible
                && System.Windows.Automation.AutomationProperties.GetItemStatus(links.Last()) == "Выбран раздел",
                "journal opens and finishes loading inside the main window");
            var editor = (System.Windows.Controls.RichTextBox)typeof(MainWindow).GetField("journalText", flags).GetValue(window);
            var errorHeading = (System.Windows.Documents.Run)((System.Windows.Documents.Paragraph)editor.Document.Blocks.LastBlock).Inlines.FirstInline;
            Check(editor.Document.Blocks.Count == 3 && errorHeading.Foreground.ToString() == "#FFFFBF77",
                "journal highlights errors in the log viewer");
            Capture(window, "preview-journal.png");
            var errors = (System.Windows.Controls.CheckBox)typeof(MainWindow).GetField("journalErrorsOnly", flags).GetValue(window);
            errors.IsChecked = true;
            errors.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(editor.Document.Blocks.Count == 2, "journal errors-only filter excludes successful records");
            var search = (System.Windows.Controls.TextBox)typeof(MainWindow).GetField("journalSearch", flags).GetValue(window);
            search.Text = "сохраните работу";
            Check(editor.Document.Blocks.Count == 1, "journal search matches multiline content while keeping the error filter");
            search.Clear();
            errors.IsChecked = false;
            errors.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            File.AppendAllText(file, "2026-11-24 10:17:10Z Проверка: 0\r\n");
            var refresh = (System.Windows.Controls.Button)typeof(MainWindow).GetField("journalRefresh", flags).GetValue(window);
            refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(5);
            while ((bool)reading.GetValue(window) && DateTime.UtcNow < deadline) WaitForAnimation(30);
            Check(editor.Document.Blocks.Count == 4, "journal refresh loads newly appended records");
            links[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!shell.Children[4].IsVisible, "leaving the journal restores the programs page");
        }
        finally
        {
            pathField.SetValue(window, logPath);
            File.Delete(file);
        }
    }

    private static void CheckConfirmations(MainWindow owner)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queue = Enumerable.Range(1, 28).Select(i => new Package
        {
            Name = i == 2 ? "Microsoft Visual C++ 2015–2022 Redistributable (x64) — компонент с длинным названием" : "Программа " + i,
            Installed = "1.2." + i,
            Available = "1.3." + i
        }).ToList();
        queue[0].Name = "Пример обновления программы";
        var programs = (Window)typeof(MainWindow).GetMethod("BuildProgramConfirmation", flags).Invoke(owner, new object[] { queue });
        Check(!ExerciseConfirmation(programs, false, (body, actions) =>
        {
            var list = (System.Windows.Controls.DataGrid)((System.Windows.Controls.Border)body.Children[2]).Child;
            Check(list.Items.Count == queue.Count && list.Columns.Count == 3
                && ((System.Windows.Controls.TextBlock)body.Children[1]).Text.Contains(queue.Count.ToString()),
                "program confirmation displays the complete queue and version columns");
            Check(((System.Windows.Controls.Button)actions.Children[1]).Content.Equals("Обновить")
                && actions.IsVisible && actions.TranslatePoint(new Point(0, actions.ActualHeight), body).Y <= body.ActualHeight,
                "program confirmation actions fit beneath the scrollable list");
            Capture(programs, "preview-confirm-programs.png");
            list.ScrollIntoView(queue.Last());
            programs.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            Check(list.ItemContainerGenerator.ContainerFromItem(queue.Last()) is System.Windows.Controls.DataGridRow
                && actions.IsVisible, "long program queue scrolls to the last entry with actions still visible");
        }), "program confirmation cancels without accepting the queue");

        var restart = (Window)typeof(MainWindow).GetMethod("BuildRestartConfirmation", flags).Invoke(owner, new object[] { new Version(0, 10, 2, 0) });
        Check(ExerciseConfirmation(restart, true, (body, actions) =>
        {
            var versions = (System.Windows.Controls.Grid)((System.Windows.Controls.Border)body.Children[2]).Child;
            var current = (System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)versions.Children[0]).Children[1];
            var next = (System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)versions.Children[1]).Children[1];
            Check(current.Text == SelfUpdate.CurrentVersion.ToString(3) && next.Text == "0.10.2", "restart confirmation compares installed and downloaded versions");
            Capture(restart, "preview-confirm-restart.png");
        }), "restart confirmation accepts an explicit button click");
    }

    private static bool ExerciseConfirmation(Window dialog, bool confirm, Action<System.Windows.Controls.StackPanel, System.Windows.Controls.StackPanel> inspect)
    {
        Exception failure = null;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.Loaded += (sender, args) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                var body = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)dialog.Content).Child;
                var actions = (System.Windows.Controls.StackPanel)body.Children[4];
                inspect(body, actions);
                var button = (System.Windows.Controls.Button)actions.Children[confirm ? 1 : 0];
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            catch (Exception ex)
            {
                failure = ex;
                dialog.DialogResult = false;
            }
        }));
        bool result = dialog.ShowDialog() == true;
        if (failure != null) throw new InvalidOperationException("Confirmation window check failed.", failure);
        return result;
    }

    private static void CheckMaintenanceCard(MainWindow window, Package target)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var action = (System.Windows.Controls.Button)type.GetField("maintenanceAction", flags).GetValue(window);
        var headline = (System.Windows.Controls.TextBlock)type.GetField("maintenanceTitle", flags).GetValue(window);
        var summary = (System.Windows.Controls.TextBlock)type.GetField("maintenanceSummary", flags).GetValue(window);
        var system = (System.Windows.Controls.TextBlock)type.GetField("maintenanceSystem", flags).GetValue(window);
        var attempts = (System.Collections.Generic.List<HistoryEntry>)type.GetField("maintenanceAttempts", flags).GetValue(window);
        var refresh = type.GetMethod("RefreshMaintenance", flags);
        var table = (System.Windows.Controls.DataGrid)type.GetField("table", flags).GetValue(window);
        table.SelectedItem = null;
        var status = (System.Windows.Controls.TextBlock)type.GetField("status", flags).GetValue(window);
        status.Text = "Проверка завершена.";
        var windows = new SystemUpdateReport { RestartRequired = true };
        windows.Windows.Add("Обновление Windows");
        type.GetField("windowsReport", flags).SetValue(window, windows);
        type.GetField("windowsChecked", flags).SetValue(window, true);
        type.GetField("nvidiaReport", flags).SetValue(window, new NvidiaUpdateReport { HasAdapter = true, HasUpdate = true });
        type.GetField("nvidiaChecked", flags).SetValue(window, true);
        refresh.Invoke(window, null);
        Check(summary.Text.Contains("Программ к обновлению") && (string)action.Content == "Обновить все программы"
            && system.Text.Contains("нужна перезагрузка") && system.Text.Contains("NVIDIA: доступен драйвер"),
            "maintenance card summarizes available programs, restart and NVIDIA driver");
        Capture(window, "preview-maintenance.png");

        type.GetField("windowsReport", flags).SetValue(window, null);
        type.GetField("nvidiaReport", flags).SetValue(window, new NvidiaUpdateReport { HasAdapter = true, Complete = false });
        refresh.Invoke(window, null);
        Check(system.Text.Contains("Windows: проверка не удалась") && system.Text.Contains("NVIDIA: проверка неполная"),
            "maintenance card distinguishes failed and partial checks from up-to-date results");

        var failed = new HistoryEntry { PackageId = target.Id, Name = target.Name, Result = "Не завершено", Detail = "Закройте программу и повторите обновление." };
        var pending = new HistoryEntry { Name = "Вторая программа", Result = "Версия не подтверждена" };
        attempts.Add(failed);
        attempts.Add(pending);
        attempts.Add(new HistoryEntry { Name = "Третья программа", Result = "Установлено" });
        refresh.Invoke(window, null);
        Check(headline.Text == "Итог сеанса" && summary.Text == "Обновлено: 1 · Не удалось: 1 · Не подтверждено: 1"
            && (string)action.Content == "Повторить обновление" && system.Text.Contains(failed.Detail),
            "maintenance result shows the three outcomes and the next retry");
        status.Text = "Список обновлён автоматически.";
        Capture(window, "preview-maintenance-result.png");
        target.Skipped = true;
        refresh.Invoke(window, null);
        Check((string)action.Content == "Сверить версию", "maintenance card skips ignored versions when offering recovery");
        attempts.Remove(pending);
        refresh.Invoke(window, null);
        Check(action.Visibility == Visibility.Collapsed, "maintenance card does not offer recovery for an ignored update");
        target.Skipped = false;
    }

    private static void CheckUpdateControls(MainWindow window, System.Windows.Controls.CheckBox select)
    {
        window.UpdateLayout();
        var box = (System.Windows.Controls.Border)select.Template.FindName("Box", select);
        var mark = (System.Windows.Shapes.Path)select.Template.FindName("Mark", select);
        var pen = new Pen(mark.Stroke, mark.StrokeThickness)
        {
            StartLineCap = mark.StrokeStartLineCap, EndLineCap = mark.StrokeEndLineCap, LineJoin = mark.StrokeLineJoin
        };
        foreach (int thickness in new[] { 1, 2 })
        {
            box.BorderThickness = new Thickness(thickness);
            window.UpdateLayout();
            var ink = mark.RenderedGeometry.GetRenderBounds(pen);
            var center = mark.TranslatePoint(new Point(ink.Left + ink.Width / 2, ink.Top + ink.Height / 2), box);
            Check(mark.IsVisible && Math.Abs(center.X - box.ActualWidth / 2) < 0.1 && Math.Abs(center.Y - box.ActualHeight / 2) < 0.1,
                "select shown checkmark is centered with border " + thickness);
        }
        box.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scan = (System.Windows.Controls.Button)typeof(MainWindow).GetField("scan", flags).GetValue(window);
        var download = (System.Windows.Controls.Button)typeof(MainWindow).GetField("appUpdate", flags).GetValue(window);
        var showRelease = typeof(MainWindow).GetMethod("ShowAppRelease", flags);
        showRelease.Invoke(window, new object[] { new AppRelease { Tag = "v0.10.1" } });
        Check(UpdateIcon(scan) != UpdateIcon(download)
            && System.Windows.Automation.AutomationProperties.GetName(download) == "Скачать обновление",
            "scan and download have distinct vector icons and an accessible download name");
        Capture(window, "preview-update-controls.png");
        var buttonContent = (System.Windows.Controls.StackPanel)download.Content;
        var label = (System.Windows.Controls.TextBlock)buttonContent.Children[1];
        label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
        Check(label.DesiredSize.Width + buttonContent.Children[0].DesiredSize.Width + download.Padding.Left + download.Padding.Right
            <= download.ActualWidth - download.BorderThickness.Left - download.BorderThickness.Right, "download label fits inside its button");
        showRelease.Invoke(window, new[] { typeof(MainWindow).GetField("availableRelease", flags).GetValue(window) });
        select.IsChecked = false;
        Check(mark.Visibility == Visibility.Collapsed, "unchecked box hides the checkmark");
    }

    private static string UpdateIcon(System.Windows.Controls.Button button)
    {
        var row = (System.Windows.Controls.StackPanel)button.Content;
        var viewbox = (System.Windows.Controls.Viewbox)row.Children[0];
        var canvas = (System.Windows.Controls.Canvas)viewbox.Child;
        return ((System.Windows.Shapes.Path)canvas.Children[0]).Data.ToString();
    }

    private static void CheckSidebarNavigation(MainWindow window)
    {
        var shell = (System.Windows.Controls.Grid)window.Content;
        var sidebar = (System.Windows.Controls.DockPanel)((System.Windows.Controls.Border)shell.Children[0]).Child;
        var list = (System.Windows.Controls.StackPanel)sidebar.Children[2];
        var links = list.Children.OfType<System.Windows.Controls.Button>().ToArray();
        var pages = new[] { shell.Children[1], shell.Children[1], shell.Children[2], shell.Children[3] };
        string[] names = { "Обновления", "Все программы", "История", "Windows и NVIDIA", "Журнал" };
        Check(links.Select(System.Windows.Automation.AutomationProperties.GetName).SequenceEqual(names),
            "sidebar buttons expose their labels to accessibility tools");
        for (int i = 0; i < pages.Length; i++)
        {
            links[i].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(pages[i].IsVisible &&
                links.Where(link => System.Windows.Automation.AutomationProperties.GetItemStatus(link) == "Выбран раздел").SequenceEqual(new[] { links[i] }),
                "sidebar opens and highlights " + names[i]);
            if (names[i] == "Windows и NVIDIA") Capture(window, "preview-system.png");
        }
    }

    private static void CheckRateLimitResponse()
    {
        var calculate = typeof(SelfUpdate).GetMethod("RateLimitRetryTime", BindingFlags.Static | BindingFlags.NonPublic);
        var epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = epoch.AddSeconds(1000);
        using (var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden))
        {
            Check(calculate.Invoke(null, new object[] { response, now }) == null, "ordinary forbidden response is not reported as a request limit");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1120");
            Check((DateTimeOffset)calculate.Invoke(null, new object[] { response, now }) == now.AddMinutes(2),
                "GitHub rate limit honors the reset time");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            Check((DateTimeOffset)calculate.Invoke(null, new object[] { response, now }) == now.AddSeconds(45),
                "explicit GitHub retry delay takes priority");
        }
    }

    private sealed class EffectButton : System.Windows.Controls.Button
    {
        public void SetPressed(bool pressed) { IsPressed = pressed; }
    }

    private static void WaitForAnimation(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (sender, e) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void CheckVisualEffects(MainWindow window)
    {
        var panel = new System.Windows.Controls.StackPanel();
        var style = (Style)window.FindResource("StatusBadge");
        var available = new System.Windows.Controls.ContentControl { Content = "Есть обновление", Tag = true, Style = style };
        var installed = new System.Windows.Controls.ContentControl { Content = "Установлено", Tag = true, Style = style };
        var current = new System.Windows.Controls.ContentControl { Content = "Без обновлений", Tag = true, Style = style };
        var metric = (System.Windows.Controls.Border)typeof(MainWindow).GetMethod("Metric", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, new object[] { new System.Windows.Controls.TextBlock(), "Программ найдено", "#EDF4FC" });
        var metricSheen = (System.Windows.Controls.Border)((System.Windows.Controls.Grid)metric.Child).Children[0];
        metricSheen.Tag = true;
        var button = new EffectButton { Content = "Проверить обновления", Tag = true, Style = (Style)window.FindResource("AnimatedButton") };
        panel.Children.Add(available);
        panel.Children.Add(installed);
        panel.Children.Add(current);
        panel.Children.Add(metric);
        panel.Children.Add(button);
        var preview = new Window { Content = panel, Width = 300, Height = 280, ShowActivated = false, Left = -10000, Top = -10000 };
        preview.Show();
        preview.UpdateLayout();
        WaitForAnimation(100);
        try
        {
            var blue = (System.Windows.Controls.Border)available.Template.FindName("BadgeShimmer", available);
            var green = (System.Windows.Controls.Border)installed.Template.FindName("BadgeShimmer", installed);
            var silver = (System.Windows.Controls.Border)current.Template.FindName("BadgeShimmer", current);
            var blueBrush = (LinearGradientBrush)blue.Background;
            var greenBrush = (LinearGradientBrush)green.Background;
            Check(blue.IsVisible && green.IsVisible && blueBrush.GradientStops[1].Color != greenBrush.GradientStops[1].Color,
                "available and installed badges display distinct highlights");
            Check(blueBrush.RelativeTransform.HasAnimatedProperties && greenBrush.RelativeTransform.HasAnimatedProperties,
                "both status highlights animate");
            Check(silver.IsVisible && ((LinearGradientBrush)silver.Background).RelativeTransform.HasAnimatedProperties, "current version badge has its own shimmer");
            Check(metricSheen.Opacity > 0 && ((LinearGradientBrush)metricSheen.Background).RelativeTransform.HasAnimatedProperties, "summary card shimmer runs");
            available.Content = "Не завершено";
            installed.Tag = false;
            metricSheen.Tag = false;
            Check(blue.Visibility == Visibility.Collapsed && green.Visibility == Visibility.Collapsed && metricSheen.Opacity == 0,
                "inactive statuses and disabled motion suppress shimmer");
            button.SetPressed(true);
            WaitForAnimation(140);
            var shell = (System.Windows.Controls.Border)button.Template.FindName("Shell", button);
            var pulse = (System.Windows.Controls.Border)button.Template.FindName("PressLight", button);
            Check(((ScaleTransform)shell.RenderTransform).ScaleX < 1 && pulse.Opacity > 0,
                "primary button scales and lights on press");
            button.SetPressed(false);
            WaitForAnimation(240);
            Check(((ScaleTransform)shell.RenderTransform).ScaleX > 0.99 && pulse.Opacity < 0.01,
                "primary button returns to rest on release");
        }
        finally { preview.Close(); }
    }

    static void CheckShortcutIcon()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CheckUpdate-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string shortcut = Path.Combine(folder, "Icon test.lnk");
        object shell = null;
        object link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            shell = Activator.CreateInstance(type);
            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcut });
            link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { typeof(MainWindow).Assembly.Location });
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            var rows = new System.Collections.Generic.List<Package>
            {
                new Package { Name = "Icon test 1.0", Id = "Fixture.Icon" },
                new Package { Name = "Differently named program", Id = "Fixture.Directory" },
                new Package { Name = "Sibling program", Id = "Fixture.Sibling" }
            };
            string targetDirectory = Path.GetDirectoryName(typeof(MainWindow).Assembly.Location);
            var locations = new System.Collections.Generic.Dictionary<string, string>
            {
                { rows[1].Name, targetDirectory },
                { rows[2].Name, targetDirectory.Substring(0, targetDirectory.Length - 1) }
            };
            typeof(PackageIcons).GetMethod("LoadShortcutIcons", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { rows, new[] { folder }, locations });
            Check(rows[0].Icon != null && rows[0].Icon.IsFrozen, "missing icon is recovered from a matching Start menu shortcut target");
            Check(rows[1].Icon != null && rows[2].Icon == null, "differently named shortcut matches the install directory without matching a partial path");
        }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            File.Delete(shortcut);
            Directory.Delete(folder);
        }
    }
}
