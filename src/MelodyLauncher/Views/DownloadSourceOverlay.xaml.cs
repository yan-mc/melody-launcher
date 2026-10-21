using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MelodyLauncher.Services;

namespace MelodyLauncher.Views
{
    public partial class DownloadSourceOverlay : UserControl
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
        public event EventHandler CloseRequested;
        public event EventHandler ConfigSaved;

        public DownloadSourceOverlay()
        {
            InitializeComponent();
            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnSave.Click += (s, e) => Save();
            BtnReset.Click += (s, e) => Reset();
            BtnTest.Click += async (s, e) => await TestAsync();
            Load();
        }

        public void Load()
        {
            var cfg = DownloadSourceConfig.Load();
            EditName.Text = cfg.Name;
            EditManifest.Text = cfg.VersionManifest;
            EditModMirror.Text = cfg.ModMirror;
            EditRules.Text = cfg.RulesText;
            switch (cfg.Mode)
            {
                case "official": RadioOfficial.IsChecked = true; break;
                case "mirror": RadioMirror.IsChecked = true; break;
                case "custom": RadioCustom.IsChecked = true; break;
                default: RadioAuto.IsChecked = true; break;
            }
            SetStatus("当前模式：" + cfg.Mode);
        }

        private string CurrentMode()
        {
            if (RadioOfficial.IsChecked == true) return "official";
            if (RadioMirror.IsChecked == true) return "mirror";
            if (RadioCustom.IsChecked == true) return "custom";
            return "auto";
        }

        private void ApplyToConfig(DownloadSourceConfig cfg)
        {
            cfg.Name = EditName.Text.Trim();
            cfg.VersionManifest = EditManifest.Text.Trim();
            cfg.ModMirror = EditModMirror.Text.Trim();
            cfg.RulesText = EditRules.Text;
            cfg.Mode = CurrentMode();
        }

        private void Save()
        {
            var cfg = DownloadSourceConfig.Load();
            ApplyToConfig(cfg);
            cfg.Save();
            SetStatus("已保存（" + cfg.Mode + "），新建任务生效");
            ConfigSaved?.Invoke(this, EventArgs.Empty);
        }

        private void Reset()
        {
            EditName.Text = "自定义下载源";
            EditManifest.Text = "";
            EditModMirror.Text = "";
            EditRules.Text = "";
            RadioAuto.IsChecked = true;
            SetStatus("已恢复默认（自动）");
        }

        private async Task TestAsync()
        {
            BtnTest.IsEnabled = false;
            try
            {
                var cfg = new DownloadSourceConfig();
                ApplyToConfig(cfg);
                string url = !string.IsNullOrEmpty(cfg.VersionManifest)
                    ? cfg.VersionManifest
                    : (cfg.Mode == "official" || cfg.Mode == "auto"
                        ? "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
                        : "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
                SetStatus("测试中… " + url);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await Http.GetAsync(url);
                var text = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                bool ok = resp.IsSuccessStatusCode && text.Contains("\"versions\"");
                SetStatus((ok ? "连接成功 " : "连接失败 ") + (int)resp.StatusCode + " · " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                SetStatus("连接失败：" + ex.Message);
            }
            finally
            {
                BtnTest.IsEnabled = true;
            }
        }

        private void SetStatus(string text)
        {
            TextStatus.Text = text;
        }
    }
}
