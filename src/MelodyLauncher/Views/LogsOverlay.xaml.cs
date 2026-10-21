using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MelodyLauncher.Views
{
    public partial class LogsOverlay : UserControl
    {
        public event EventHandler CloseRequested;

        private static string JobsDir => MelodyLauncher.Services.AppPaths.JobsDir;

        public LogsOverlay()
        {
            InitializeComponent();
            try { var __mc = MelodyLauncher.Services.AppSettings.Load().McFolder;  } catch { }
            
            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnRefresh.Click += (s, e) => Refresh();
            BtnCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(TextLog.Text); } catch { }
            };
            BtnOpenJobs.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(JobsDir);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + JobsDir + "\"") { UseShellExecute = true });
                }
                catch { }
            };
        }

        public void Refresh()
        {
            TextLogPath.Text = JobsDir;
            var sb = new StringBuilder();
            try
            {
                if (Directory.Exists(JobsDir))
                {
                    var latest = Directory.GetDirectories(JobsDir)
                        .Select(d => new DirectoryInfo(d))
                        .OrderByDescending(d => d.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (latest != null)
                    {
                        TextLogPath.Text = "最近任务：" + latest.Name + "   " + latest.FullName;
                        var events = Path.Combine(latest.FullName, "progress.jsonl");
                        if (!File.Exists(events)) events = Path.Combine(latest.FullName, "events.jsonl");
                        if (File.Exists(events))
                        {
                            var lines = FormatEvents(ReadTail(events, 400));
                            sb.AppendLine("=== " + events + " ===");
                            foreach (var l in lines) sb.AppendLine(l);
                        }
                        var log = Directory.GetFiles(latest.FullName, "*.log").FirstOrDefault() ?? "";
                        if (File.Exists(log))
                        {
                            sb.AppendLine();
                            sb.AppendLine("=== " + log + " ===");
                            foreach (var l in ReadTail(log, 120)) sb.AppendLine(l);
                        }
                    }
                    else
                    {
                        sb.AppendLine("（还没有任务记录）");
                    }
                }
                else
                {
                    sb.AppendLine("（还没有任务目录）");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取失败：" + ex.Message);
            }
            TextLog.Text = sb.ToString();
            Scroller.ScrollToEnd();
        }

        private static string[] ReadTail(string path, int count)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var lines = new System.Collections.Generic.List<string>();
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Add(line);
                    if (lines.Count > count) lines.RemoveAt(0);
                }
                return lines.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>把 JSONL 事件格式化成可读行。</summary>
        private static string[] FormatEvents(string[] lines)
        {
            var result = new List<string>();
            foreach (var line in lines)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ts = root.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
                    if (ts.Length >= 19) ts = ts.Substring(11, 8);
                    string type = root.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    var sb = new StringBuilder();
                    sb.Append(ts).Append("  ").Append(type.PadRight(9));
                    if (root.TryGetProperty("stage", out var st)) sb.Append("[").Append(st.GetString()).Append("] ");
                    if (root.TryGetProperty("phase", out var ph)) sb.Append(ph.GetString()).Append("  ");
                    if (root.TryGetProperty("percent", out var pc)) sb.Append((pc.GetDouble() * 100).ToString("F0")).Append("%  ");
                    if (root.TryGetProperty("text", out var tx)) sb.Append(tx.GetString());
                    if (root.TryGetProperty("code", out var cd)) sb.Append("code=").Append(cd.GetString()).Append("  ");
                    if (root.TryGetProperty("ok", out var ok)) sb.Append("ok=").Append(ok.ToString()).Append("  ");
                    if (root.TryGetProperty("durationMs", out var dm)) sb.Append(dm.GetInt64()).Append("ms");
                    if (root.TryGetProperty("engine", out var eng))
                        sb.Append("engine ").Append(eng.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : "")
                          .Append(" build=").Append(eng.TryGetProperty("build", out var bl) ? bl.GetInt32().ToString() : "?");
                    result.Add(sb.ToString());
                }
                catch
                {
                    result.Add(line);
                }
            }
            return result.ToArray();
        }
    }
}
