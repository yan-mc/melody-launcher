using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MelodyLauncher.Services
{
    /// <summary>下载源 / API 配置（与内核 Api.json 同构，便于社区互抄）。</summary>
    public sealed class DownloadSourceConfig
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "自定义下载源";
        [JsonPropertyName("version_manifest")] public string VersionManifest { get; set; } = "";
        [JsonPropertyName("mod_mirror")] public string ModMirror { get; set; } = "";
        [JsonPropertyName("rules")] public List<Rule> Rules { get; set; } = new();

        /// <summary>auto | official | mirror（映射到后端 options.download.source）</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "auto";

        public sealed class Rule
        {
            [JsonPropertyName("from")] public string From { get; set; } = "";
            [JsonPropertyName("to")] public string To { get; set; } = "";
        }

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MelodyLauncher", "Api.json");

        public static DownloadSourceConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<DownloadSourceConfig>(File.ReadAllText(FilePath))
                           ?? new DownloadSourceConfig();
            }
            catch { }
            return new DownloadSourceConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public string RulesText
        {
            get
            {
                var lines = new List<string>();
                foreach (var r in Rules) lines.Add(r.From + " = " + r.To);
                return string.Join("\n", lines);
            }
            set
            {
                Rules.Clear();
                foreach (var raw in (value ?? "").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var from = line.Substring(0, idx).Trim();
                    var to = line.Substring(idx + 1).Trim();
                    if (from.Length > 0 && to.Length > 0) Rules.Add(new Rule { From = from, To = to });
                }
            }
        }
    }
}
