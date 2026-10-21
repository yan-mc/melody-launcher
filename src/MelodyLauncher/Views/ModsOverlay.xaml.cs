using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public partial class ModsOverlay : UserControl
    {
        private sealed class ModEntry
        {
            public string File = "";
            public bool Enabled;
            public long Size;
        }

        private readonly List<ModEntry> _mods = new();
        private EngineClient? _backend;
        private string? _selected;

        public event EventHandler CloseRequested;

        public ModsOverlay()
        {
            InitializeComponent();
            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnScan.Click += async (s, e) => await ScanAsync();
            BtnEnable.Click += async (s, e) => await SetEnabledAsync(true);
            BtnDisable.Click += async (s, e) => await SetEnabledAsync(false);
            BtnOpenMods.Click += (s, e) => OpenModsDir();
            BtnOpenGame.Click += (s, e) => OpenDir(AppSettings.Load().McFolder);
        }

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve(),
            WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MelodyLauncher", "jobs")
        });

        public void Refresh()
        {
            ComboVersion.Items.Clear();
            var mc = AppSettings.Load().McFolder;
            try
            {
                var dir = Path.Combine(mc, "versions");
                if (Directory.Exists(dir))
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var id = Path.GetFileName(d);
                        if (File.Exists(Path.Combine(d, id + ".json"))) ComboVersion.Items.Add(id);
                    }
            }
            catch { }
            if (ComboVersion.Items.Count > 0)
            {
                var last = AppSettings.Load().LastVersion;
                ComboVersion.SelectedItem = ComboVersion.Items.Contains(last) ? last : ComboVersion.Items[0];
            }
            var modsDir = Path.Combine(mc, "mods");
            TextStatus.Text = Directory.Exists(modsDir) ? "点「扫描」读取 Mod 列表" : "该版本目录下还没有 mods 文件夹";
        }

        private void OpenModsDir()
        {
            var mc = AppSettings.Load().McFolder;
            var mods = Path.Combine(mc, "mods");
            try { Directory.CreateDirectory(mods); } catch { }
            OpenDir(mods);
        }

        private static void OpenDir(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        private async Task ScanAsync()
        {
            if (ComboVersion.SelectedItem is not string version)
            {
                TextStatus.Text = "请先选择版本";
                return;
            }
            TextStatus.Text = "扫描中…";
            try
            {
                var spec = EngineTaskBuilder.ModScan(version, AppSettings.Load().McFolder);
                var result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                _mods.Clear();
                ModList.Children.Clear();
                _selected = null;
                if (!result.Ok)
                {
                    TextStatus.Text = "扫描失败：" + result.Code;
                    return;
                }
                if (result.Data != null && result.Data.Value.TryGetProperty("mods", out var arr))
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var e = new ModEntry
                        {
                            File = m.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                            Enabled = m.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                            Size = m.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0
                        };
                        _mods.Add(e);
                        ModList.Children.Add(BuildRow(e));
                    }
                }
                TextStatus.Text = $"共 {_mods.Count} 个 Mod（启用 {_mods.Count(x => x.Enabled)}）";
            }
            catch (Exception ex)
            {
                TextStatus.Text = "异常：" + ex.Message;
            }
        }

        private Border BuildRow(ModEntry e)
        {
            var g = new Grid { Height = 24 };
            var cb = new CheckBox
            {
                IsChecked = e.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                IsHitTestVisible = false
            };
            g.Children.Add(cb);
            var name = new TextBlock
            {
                Text = e.File,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x32, 0x3D)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28, 0, 0, 0)
            };
            g.Children.Add(name);
            var size = new TextBlock
            {
                Text = e.Size > 0 ? (e.Size / 1024 / 1024.0).ToString("F1") + " MB" : "",
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0x5A, 0x66)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            g.Children.Add(size);
            var bd = new Border { Child = g, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            bd.MouseEnter += (s, ev) => bd.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            bd.MouseLeave += (s, ev) => { if (_selected != e.File) bd.Background = Brushes.Transparent; };
            bd.MouseLeftButtonUp += (s, ev) =>
            {
                _selected = e.File;
                foreach (var c in ModList.Children.OfType<Border>()) c.Background = Brushes.Transparent;
                bd.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
                TextStatus.Text = "已选中：" + e.File;
            };
            return bd;
        }

        private async Task SetEnabledAsync(bool enabled)
        {
            if (_selected == null) { TextStatus.Text = "请先选中一个 Mod"; return; }
            if (ComboVersion.SelectedItem is not string version) return;
            TextStatus.Text = (enabled ? "启用中…" : "禁用中…");
            try
            {
                var spec = new EngineTaskSpec
                {
                    Type = EngineProtocol.ModSetEnabled,
                    Params = new Dictionary<string, object?>
                    {
                        ["version"] = version,
                        ["file"] = _selected,
                        ["enabled"] = enabled
                    },
                    Options = new EngineTaskOptions
                    {
                        EngineAppName = "MelodyEngine",
                        McFolder = AppSettings.Load().McFolder,
                        RequireEngine = new EngineRequirement { MinBuild = 1 }
                    }
                };
                var result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                TextStatus.Text = result.Ok ? "已" + (enabled ? "启用" : "禁用") + "：" + _selected : "操作失败：" + result.Code;
                await ScanAsync();
            }
            catch (Exception ex)
            {
                TextStatus.Text = "异常：" + ex.Message;
            }
        }
    }
}
