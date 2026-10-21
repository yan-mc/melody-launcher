using System;
using System.IO;

namespace MelodyLauncher.Services.Bridge
{
    /// <summary>
    /// 定位内核可执行文件。
    /// 单文件发布时引擎会被解压到 %TEMP%\.net\... 下，路径含 '='/'+' 会让 引擎（.NET Framework）启动失败（exit=4），
    /// 因此检测到不稳定路径时自动复制到 %LocalAppData%\MelodyLauncher\engine\ 再使用。
    /// </summary>
    public static class EngineLocator
    {
        public static string Resolve()
        {
            var env = Environment.GetEnvironmentVariable("MELODY_ENGINE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "engine", "core.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "MelodyLauncher", "engine", "core.exe"),
                // dev fallback: repo work dir
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "work", "bridge-fork", "out", "core.exe")),
            };
            foreach (var c in candidates)
            {
                if (!File.Exists(c)) continue;
                return NeedsStableCopy(c) ? EnsureStableCopy(c) : c;
            }
            return candidates[0];
        }

        public static bool Exists => File.Exists(Resolve());

        private static bool NeedsStableCopy(string path)
        {
            if (path.Length > 100) return true;
            if (path.Contains(@"\.net\")) return true;
            if (path.IndexOf('=') >= 0 || path.IndexOf('+') >= 0) return true;
            return false;
        }

        private static string EnsureStableCopy(string src)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "MelodyLauncher", "engine");
                var dst = Path.Combine(dir, "core.exe");
                Directory.CreateDirectory(dir);
                var srcLen = new FileInfo(src).Length;
                if (!File.Exists(dst) || new FileInfo(dst).Length != srcLen)
                    File.Copy(src, dst, true);
                return dst;
            }
            catch
            {
                return src;
            }
        }
    }
}
