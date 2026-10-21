using System;
using System.IO;
using System.Text.Json;

namespace MelodyLauncher.Services
{
    /// <summary>启动器设置（JSON，位于 %AppData%\MelodyLauncher\settings.json）。</summary>
    public sealed class AppSettings
    {
        public string McFolder { get; set; } = AppPaths.DefaultMcFolder;
        public string OfflineName { get; set; } = "Player";
        public string JavaPath { get; set; } = "";
        public int MemoryMB { get; set; } = 1024;
        public int WindowWidth { get; set; } = 854;
        public int WindowHeight { get; set; } = 480;
        public string DownloadSource { get; set; } = "auto";
        public string BackgroundMode { get; set; } = "default";
        public string LastVersion { get; set; } = "";
        /// <summary>版本隔离（默认开启；mods/存档等位于 versions/&lt;版本&gt;/ 下）。</summary>
        public bool VersionIsolation { get; set; } = true;

        // ---- 微软账户（正版登录）----
        /// <summary>OAuth Client ID；默认使用内置应用 ID，可换成自己在 Azure 注册的。</summary>
        public string MsClientId { get; set; } = "fe72edc2-3a6f-4280-90e8-e2beb64ce7e1";
        public string MsRefreshToken { get; set; } = "";
        public string MsUserName { get; set; } = "";
        public string MsUuid { get; set; } = "";

        private static string FilePath => AppPaths.SettingsFile;

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                var p = FilePath;
                if (File.Exists(p))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p));
            }
            catch { }
            s ??= new AppSettings();
            if (string.IsNullOrWhiteSpace(s.McFolder)) s.McFolder = AppPaths.DefaultMcFolder;
            // 测试钩子：MELODY_MC 覆盖 .minecraft 目录（不写入配置文件）
            var mc = Environment.GetEnvironmentVariable("MELODY_MC");
            if (!string.IsNullOrEmpty(mc)) s.McFolder = mc;
            return s;
        }

        public void Save()
        {
            try
            {
                var p = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
