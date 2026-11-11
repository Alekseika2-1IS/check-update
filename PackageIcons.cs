using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace UpdateHub
{
    public static class PackageIcons
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string AppPackages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        public static void Populate(IEnumerable<Package> packages)
        {
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var list = root.OpenSubKey(Uninstall))
                    {
                        if (list == null) continue;
                        foreach (string subkey in list.GetSubKeyNames())
                        {
                            using (var entry = list.OpenSubKey(subkey))
                            {
                                if (entry == null) continue;
                                string name = entry.GetValue("DisplayName") as string;
                                string icon = entry.GetValue("DisplayIcon") as string;
                                if (!String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(icon) && !icons.ContainsKey(name))
                                    icons[name] = icon;
                            }
                        }
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            var cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
            var remaining = new List<Package>();
            foreach (var package in packages)
            {
                string reference;
                if (icons.TryGetValue(package.Name, out reference))
                {
                    ImageSource image;
                    if (!cache.TryGetValue(reference, out image))
                        cache[reference] = image = Load(reference);
                    package.Icon = image;
                }
                if (package.Icon == null) remaining.Add(package);
            }
            LoadPackagedIcons(remaining);
            var startMenuFolders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) };
            LoadShortcutIcons(remaining.Where(package => package.Icon == null).ToList(), startMenuFolders);
        }

        private static void LoadPackagedIcons(List<Package> packages)
        {
            if (packages.Count == 0) return;
            try
            {
                using (var repository = Registry.CurrentUser.OpenSubKey(AppPackages))
                {
                    if (repository == null) return;
                    var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in repository.GetSubKeyNames())
                    {
                        string identity = name.Split('_')[0];
                        using (var entry = repository.OpenSubKey(name))
                        {
                            string directory = entry == null ? null : entry.GetValue("PackageRootFolder") as string;
                            if (IsLocalPath(directory) && File.Exists(Path.Combine(directory, "AppxManifest.xml")))
                                roots[identity] = directory;
                        }
                    }
                    foreach (var package in packages)
                    {
                        string directory;
                        string identity = package.Id.Split('_')[0];
                        if (String.Equals(identity, "Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase))
                            identity = "Microsoft.DesktopAppInstaller";
                        if (roots.TryGetValue(identity, out directory))
                            package.Icon = LoadManifestIcon(directory);
                    }
                }
            }
            catch (Exception) { /* The installation registry is optional. */ }
        }

        private static ImageSource LoadManifestIcon(string directory)
        {
            try
            {
                var manifest = new XmlDocument { XmlResolver = null };
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2000000 };
                using (var reader = XmlReader.Create(Path.Combine(directory, "AppxManifest.xml"), settings))
                    manifest.Load(reader);
                var logos = new List<string>();
                foreach (XmlElement element in manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']/*[local-name()='VisualElements']"))
                    logos.Add(element.GetAttribute("Square44x44Logo"));
                var fallback = manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Logo']");
                if (fallback != null) logos.Add(fallback.InnerText);
                foreach (string logo in logos.Where(value => !String.IsNullOrWhiteSpace(value)))
                {
                    string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string file = Path.GetFullPath(Path.Combine(root, logo));
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string stem = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
                    foreach (string variant in new[] { stem + ".targetsize-32_altform-unplated.png", stem + ".targetsize-32.png", file, stem + ".scale-100.png", stem + ".scale-200.png" })
                    {
                        if (!File.Exists(variant)) continue;
                        using (var stream = File.OpenRead(variant))
                        {
                            var image = new BitmapImage();
                            image.BeginInit();
                            image.CacheOption = BitmapCacheOption.OnLoad;
                            image.DecodePixelWidth = 32;
                            image.StreamSource = stream;
                            image.EndInit();
                            image.Freeze();
                            return image;
                        }
                    }
                }
            }
            catch (Exception) { /* A missing or inaccessible asset leaves the next source available. */ }
            return null;
        }

        private static void LoadShortcutIcons(List<Package> packages, IEnumerable<string> folders)
        {
            if (packages.Count == 0) return;
            object shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return;
                var shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in folders)
                    FindShortcuts(directory, shortcuts, 0);
                shell = Activator.CreateInstance(type);
                foreach (var package in packages)
                {
                    string shortcut;
                    if (!shortcuts.TryGetValue(package.Name, out shortcut) &&
                        !shortcuts.TryGetValue(Regex.Replace(package.Name, @"\s+\d+(?:\.\d+)+(?:\s+\([^)]+\))?$", ""), out shortcut))
                        continue;
                    object link = null;
                    try
                    {
                        link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcut });
                        var linkType = link.GetType();
                        string icon = linkType.InvokeMember("IconLocation", BindingFlags.GetProperty, null, link, null) as string;
                        string target = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                        package.Icon = Load(icon) ?? Load(target);
                    }
                    catch (Exception) { /* A broken shortcut is skipped. */ }
                    finally { if (link != null) Marshal.ReleaseComObject(link); }
                }
            }
            catch (Exception) { /* The shell is optional. */ }
            finally { if (shell != null) Marshal.ReleaseComObject(shell); }
        }

        private static void FindShortcuts(string directory, Dictionary<string, string> shortcuts, int depth)
        {
            if (depth > 6 || !IsLocalPath(directory)) return;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string file in Directory.EnumerateFiles(directory, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!shortcuts.ContainsKey(name)) shortcuts[name] = file;
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                    FindShortcuts(child, shortcuts, depth + 1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsLocalPath(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && Regex.IsMatch(path, @"^[A-Za-z]:\\") &&
                new DriveInfo(path.Substring(0, 3)).DriveType == DriveType.Fixed;
        }

        private static ImageSource Load(string reference)
        {
            if (String.IsNullOrWhiteSpace(reference)) return null;
            var match = Regex.Match(Environment.ExpandEnvironmentVariables(reference.Trim()), "^\\s*(?:\"([^\"]+)\"|(.+?))(?:,\\s*(-?[0-9]+))?\\s*$");
            if (!match.Success) return null;
            string path = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            int index = 0;
            if (match.Groups[3].Success && !Int32.TryParse(match.Groups[3].Value, out index)) return null;
            try
            {
                if (!IsLocalPath(path)) return null;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".exe" && extension != ".dll" && extension != ".ico") return null;
                if (!File.Exists(path)) return null;
                var icon = new IntPtr[1];
                try
                {
                    ExtractIconEx(path, index, icon, null, 1);
                    if (icon[0] == IntPtr.Zero) return null;
                    var image = Imaging.CreateBitmapSourceFromHIcon(icon[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    image.Freeze();
                    return image;
                }
                finally { if (icon[0] != IntPtr.Zero) DestroyIcon(icon[0]); }
            }
            catch (Exception) { return null; }
        }
    }
}
