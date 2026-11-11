using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
            foreach (var package in packages)
            {
                string reference;
                if (!icons.TryGetValue(package.Name, out reference)) continue;
                ImageSource image;
                if (!cache.TryGetValue(reference, out image))
                    cache[reference] = image = Load(reference);
                package.Icon = image;
            }
        }

        private static ImageSource Load(string reference)
        {
            var match = Regex.Match(Environment.ExpandEnvironmentVariables(reference.Trim()), "^\\s*(?:\"([^\"]+)\"|(.+?))(?:,\\s*(-?[0-9]+))?\\s*$");
            if (!match.Success) return null;
            string path = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            int index = 0;
            if (match.Groups[3].Success && !Int32.TryParse(match.Groups[3].Value, out index)) return null;
            try
            {
                if (!Regex.IsMatch(path, @"^[A-Za-z]:\\") || new DriveInfo(path.Substring(0, 3)).DriveType != DriveType.Fixed)
                    return null;
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
