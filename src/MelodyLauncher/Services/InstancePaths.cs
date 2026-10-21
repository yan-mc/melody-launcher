using System;
using System.IO;

namespace MelodyLauncher.Services
{
    /// <summary>
    /// 版本目录解析。默认「版本隔离」：mods/资源包/光影/存档等位于
    /// .minecraft/versions/&lt;版本&gt;/ 下，而不是 .minecraft/ 根目录。
    /// 全项目统一从这里取目录，保证启动器各页面与内核读到的是同一份文件。
    /// </summary>
    public static class InstancePaths
    {
        public static string VersionDir(string mcFolder, string version)
            => Path.Combine(mcFolder, "versions", version);

        /// <summary>当前是否启用版本隔离（跟随 AppSettings，默认 true）。</summary>
        public static bool IsIsolated => AppSettings.Load().VersionIsolation;

        /// <summary>当前选中的已安装版本（LastVersion）。</summary>
        public static string CurrentVersion => AppSettings.Load().LastVersion;

        /// <summary>解析某个子目录（mods/resourcepacks/...）的真实位置。</summary>
        public static string Resolve(string mcFolder, string? version, string subDir)
        {
            var s = AppSettings.Load();
            var ver = string.IsNullOrEmpty(version) ? s.LastVersion : version;
            if (s.VersionIsolation && !string.IsNullOrEmpty(ver))
                return Path.Combine(VersionDir(mcFolder, ver), subDir);
            return Path.Combine(mcFolder, subDir);
        }

        public static string Mods(string mcFolder) => Resolve(mcFolder, null, "mods");
        public static string ResourcePacks(string mcFolder) => Resolve(mcFolder, null, "resourcepacks");
        public static string ShaderPacks(string mcFolder) => Resolve(mcFolder, null, "shaderpacks");
        public static string Saves(string mcFolder) => Resolve(mcFolder, null, "saves");
        public static string Screenshots(string mcFolder) => Resolve(mcFolder, null, "screenshots");
        public static string Logs(string mcFolder) => Resolve(mcFolder, null, "logs");
        public static string Datapacks(string mcFolder) => Resolve(mcFolder, null, "datapacks");
    }
}
