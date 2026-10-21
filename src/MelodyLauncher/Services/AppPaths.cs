using System;
using System.IO;

namespace MelodyLauncher.Services
{
    /// <summary>
    /// 启动器路径策略：**便携优先**。
    /// 配置、缓存、任务目录都放在 exe 同目录的 data\ 下；游戏目录默认是 exe 同目录的 .minecraft。
    /// 如果 exe 所在目录不可写（例如放在 Program Files），自动回退到用户目录。
    /// </summary>
    public static class AppPaths
    {
        private static string? _exeDir;
        private static bool? _portable;
        private static string? _dataDir;

        /// <summary>exe 所在目录（单文件发布时也是真实 exe 路径，而不是临时解压目录）。</summary>
        public static string ExeDir => _exeDir ??= ResolveExeDir();

        private static string ResolveExeDir()
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir)) return dir!;
                }
            }
            catch { }
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>是否为便携模式（exe 目录可写）。</summary>
        public static bool Portable
        {
            get
            {
                if (_portable.HasValue) return _portable.Value;
                _portable = false;
                try
                {
                    var data = Path.Combine(ExeDir, "data");
                    Directory.CreateDirectory(data);
                    var probe = Path.Combine(data, ".write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    _portable = true;
                }
                catch { _portable = false; }
                return _portable.Value;
            }
        }

        /// <summary>数据目录：便携模式 = exe\data；否则 = %LocalAppData%\MelodyLauncher。</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                _dataDir = Portable
                    ? Path.Combine(ExeDir, "data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "MelodyLauncher");
                try { Directory.CreateDirectory(_dataDir); } catch { }
                return _dataDir;
            }
        }

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string ApiFile => Path.Combine(DataDir, "Api.json");
        public static string JobsDir => Path.Combine(DataDir, "jobs");
        public static string EngineDir => Path.Combine(DataDir, "engine");
        public static string CrashLogFile => Path.Combine(DataDir, "crash.log");

        /// <summary>默认游戏目录：便携时 = exe 同目录 \.minecraft，否则 %AppData%\.minecraft。</summary>
        public static string DefaultMcFolder
        {
            get
            {
                if (Portable) return Path.Combine(ExeDir, ".minecraft");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            }
        }

        /// <summary>首次启动（没有设置文件）时创建默认游戏目录。</summary>
        public static bool CreateDefaultGameDirIfMissing()
        {
            try
            {
                if (Directory.Exists(DefaultMcFolder)) return false;
                Directory.CreateDirectory(DefaultMcFolder);
                return true;
            }
            catch { return false; }
        }
    }
}
