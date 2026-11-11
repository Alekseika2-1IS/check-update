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
    static void Capture(MainWindow window, string name)
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
        CheckShortcutIcon();
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
        window.Close(); app.Shutdown();
        Console.WriteLine("All " + checks + " checks passed. No updates installed.");
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
