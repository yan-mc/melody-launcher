using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MelodyLauncher.Services
{
    /// <summary>常见位置扫描 Java（javaw.exe）。</summary>
    public static class JavaScanner
    {
        public static List<string> FindJava()
        {
            var result = new List<string>();
            var roots = new List<string>
            {
                @"C:\Program Files\Java",
                @"C:\Program Files (x86)\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\AdoptOpenJDK",
                @"C:\Program Files\Semeru",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var exe in Directory.EnumerateFiles(root, "javaw.exe", SearchOption.AllDirectories).Take(40))
                    {
                        // runtime 目录较多，只保留 bin 下的
                        if (!exe.EndsWith(@"\bin\javaw.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!result.Contains(exe, StringComparer.OrdinalIgnoreCase)) result.Add(exe);
                    }
                }
                catch { }
            }
            // 按版本号降序（粗排）
            return result.OrderByDescending(p => VersionScore(p)).ToList();
        }

        private static int VersionScore(string path)
        {
            var m = Regex.Match(path, @"(?:jdk|jre|java)[-_]?(\d+)(?:[._](\d+))?", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            int major = int.Parse(m.Groups[1].Value);
            int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            return major * 100 + minor;
        }
    }
}
