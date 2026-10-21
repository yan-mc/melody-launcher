using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MelodyLauncher.Core.Bridge;
using MelodyLauncher.Services;
using MelodyLauncher.Services.Bridge;

namespace MelodyLauncher.Views
{
    public partial class DownloadOverlay : UserControl
    {
        private sealed class VersionEntry
        {
            public string Id = "";
            public string Type = "";
            public string ReleaseTime = "";
            public string Category = "";
            public Border? Row;
        }

        /// <summary>引擎 认定的愚人节版本 ID（小写比较）。</summary>
        private static readonly HashSet<string> FoolIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "20w14infinite", "20w14∞", "3d shareware v1.34", "1.rv-pre1", "15w14a", "2.0",
            "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"
        };

        public event EventHandler CloseRequested;
        public event EventHandler<string> DownloadRequested;   // 选中版本 → 打开加载器选择页
        public event EventHandler CommunityRequested;          // 打开社区资源子页

        private readonly List<VersionEntry> _all = new();
        private EngineClient? _backend;
        private string? _selected;
        private Task<bool>? _loadTask;

        public DownloadOverlay()
        {
            InitializeComponent();
            Loaded += async (s, e) => await EnsureLoadedAsync();
            BtnPageClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnCommunity.Click += (s, e) => CommunityRequested?.Invoke(this, EventArgs.Empty);
            BtnDownload.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_selected)) { SetStatus("请先选择一个版本", true); return; }
                DownloadRequested?.Invoke(this, _selected);
            };
            RadioRelease.Checked += (s, e) => ApplyFilter();
            RadioSnapshot.Checked += (s, e) => ApplyFilter();
            RadioOld.Checked += (s, e) => ApplyFilter();
            RadioFool.Checked += (s, e) => ApplyFilter();
        }

        private string McFolder => AppSettings.Load().McFolder;

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve(),
            WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MelodyLauncher", "jobs")
        });

        private void SetStatus(string text, bool error = false)
        {
            HeadStatus.Text = text;
            HeadStatus.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0x8B, 0x1A, 0x1A)
                : Color.FromRgb(0x10, 0x22, 0x2F));
        }

        // ---------------------------------------------------------------- load
        private Task<bool> EnsureLoadedAsync()
        {
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadVersionsAsync();
            return _loadTask;
        }

        private async Task<bool> LoadVersionsAsync()
        {
            if (!EngineLocator.Exists)
            {
                SetStatus("未找到内核 engine\\core.exe", true);
                return false;
            }
            SetStatus("正在获取版本列表…");
            try
            {
                var spec = EngineTaskBuilder.ListVersions(McFolder);
                ApplySourceConfig(spec);
                var result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                if (!result.Ok)
                {
                    SetStatus("获取失败：" + (result.Code ?? "未知错误"), true);
                    return false;
                }
                if (result.Data == null || !result.Data.Value.TryGetProperty("versions", out var arr))
                {
                    SetStatus("返回数据为空", true);
                    return false;
                }
                _all.Clear();
                foreach (var v in arr.EnumerateArray())
                {
                    var e = new VersionEntry
                    {
                        Id = v.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Type = v.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                        ReleaseTime = v.TryGetProperty("releaseTime", out var rt) ? rt.GetString() ?? "" : ""
                    };
                    e.Category = Classify(e);
                    _all.Add(e);
                }
                ApplyFilter();
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("内核调用失败：" + ex.GetType().Name + " " + ex.Message, true);
                return false;
            }
        }

        /// <summary>与 引擎 PageDownloadInstall.LoadMinecraft_OnFinish 一致的分类。</summary>
        private static string Classify(VersionEntry v)
        {
            var type = v.Type;
            var idLower = v.Id.ToLowerInvariant();
            if (type == "release") return "正式版";
            if (type == "snapshot")
            {
                // Mojang 误分类：1.x 的 snapshot 其实是正式版
                if (idLower.StartsWith("1.") &&
                    !idLower.Contains("combat") && !idLower.Contains("rc") &&
                    !idLower.Contains("experimental") && !idLower.Contains("pre"))
                    return "正式版";
                if (FoolIds.Contains(v.Id)) return "愚人节版";
                // 4/1 自动视作愚人节版（UTC+2）
                if (DateTime.TryParse(v.ReleaseTime, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var rt))
                {
                    var local = rt.ToUniversalTime().AddHours(2);
                    if (local.Month == 4 && local.Day == 1) return "愚人节版";
                }
                return "预览版";
            }
            if (type == "special") return "愚人节版";
            return "远古版";
        }

        private void ApplyFilter()
        {
            RowHost.Children.Clear();
            _selected = null;
            var category = RadioSnapshot.IsChecked == true ? "预览版"
                : RadioOld.IsChecked == true ? "远古版"
                : RadioFool.IsChecked == true ? "愚人节版"
                : "正式版";
            var list = _all.Where(v => v.Category == category)
                .OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal)
                .ToList();
            int count = 0;
            foreach (var v in list)
            {
                RowHost.Children.Add(BuildRow(v));
                count++;
                if (count >= 400) break;
            }
            SetStatus($"{category} · 共 {list.Count} 个（显示 {count}）");
        }

        private Border BuildRow(VersionEntry v)
        {
            var g = new Grid { Height = 26 };
            g.Children.Add(Tb(v.Id, 5));
            g.Children.Add(Tb(v.Id, 56.6));
            g.Children.Add(Tb(Describe(v), 154.6, 500));
            var bd = new Border
            {
                Height = 26,
                Background = Brushes.Transparent,
                Child = g,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            v.Row = bd;
            bd.MouseEnter += (s, e) => { if (_selected != v.Id) bd.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)); };
            bd.MouseLeave += (s, e) => { if (_selected != v.Id) bd.Background = Brushes.Transparent; };
            bd.MouseLeftButtonUp += (s, e) => Select(v);
            return bd;
        }

        private static string Describe(VersionEntry v)
        {
            try
            {
                if (DateTime.TryParse(v.ReleaseTime, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var rt))
                    return v.Category + " " + rt.ToLocalTime().ToString("yyyy/M/d HH:mm:ss");
            }
            catch { }
            return v.Category;
        }

        private void Select(VersionEntry v)
        {
            foreach (var x in _all)
                if (x.Row != null) x.Row.Background = Brushes.Transparent;
            _selected = v.Id;
            v.Row!.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            SetStatus("已选择 " + v.Id + " · 点「下载」安装");
        }

        private static TextBlock Tb(string text, double left, double maxWidth = 100)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x32, 0x3D)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = maxWidth
            };
        }

        /// <summary>把下载源配置写进任务 options.download（与 DownloadSourceOverlay 共用）。</summary>
        public static void ApplySourceConfig(EngineTaskSpec spec)
        {
            try
            {
                var cfg = MelodyLauncher.Services.DownloadSourceConfig.Load();
                spec.Options.Download ??= new EngineDownloadOptions();
                spec.Options.Download.Source = cfg.Mode == "custom" ? "auto" : cfg.Mode;
                spec.Options.Download.VersionListSource = cfg.Mode == "custom" ? "auto" : cfg.Mode;
                if (!string.IsNullOrEmpty(cfg.VersionManifest))
                    spec.Options.Download.VersionManifestUrl = cfg.VersionManifest;
                if (cfg.Rules.Count > 0)
                {
                    spec.Options.Download.UrlRewriteRules = new List<EngineUrlRewriteRule>();
                    foreach (var r in cfg.Rules)
                        spec.Options.Download.UrlRewriteRules.Add(new EngineUrlRewriteRule { From = r.From, To = r.To });
                }
            }
            catch { }
        }

        /// <summary>测试钩子：加载列表并选中某个版本，返回版本号。</summary>
        public async Task<string?> SelectForTestAsync(string version)
        {
            await EnsureLoadedAsync();
            var entry = _all.FirstOrDefault(v => v.Id == version);
            if (entry == null) { SetStatus("测试：未找到版本 " + version, true); return null; }
            switch (entry.Category)
            {
                case "预览版": RadioSnapshot.IsChecked = true; break;
                case "远古版": RadioOld.IsChecked = true; break;
                case "愚人节版": RadioFool.IsChecked = true; break;
                default: RadioRelease.IsChecked = true; break;
            }
            ApplyFilter();
            var row = _all.FirstOrDefault(v => v.Id == version);
            if (row != null) Select(row);
            return version;
        }

        public void ShowStatus(string text, bool error = false) => SetStatus(text, error);
    }
}
