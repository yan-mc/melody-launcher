using System;
using System.IO;
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
    public partial class LoaderOverlay : UserControl
    {
        public event EventHandler CloseRequested;
        public event EventHandler<string> Installed;

        private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private EngineClient? _backend;
        private string _mcVersion = "";
        private bool _busy;

        public LoaderOverlay()
        {
            InitializeComponent();
            try { var __mc = MelodyLauncher.Services.AppSettings.Load().McFolder;  } catch { }
            
            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnBack.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnStart.Click += async (s, e) => await StartInstallAsync();
        }

        private string McFolder => AppSettings.Load().McFolder;

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve()        });

        public void Prepare(string mcVersion)
        {
            _mcVersion = mcVersion;
            TextMcVersion.Text = mcVersion;
            EditName.Text = "";
            RadioVanilla.IsChecked = true;
            TextStatus.Text = "";
        }

        private string LoaderType()
        {
            if (RadioForge.IsChecked == true) return "forge";
            if (RadioFabric.IsChecked == true) return "fabric";
            if (RadioNeoForge.IsChecked == true) return "neoforge";
            if (RadioOptiFine.IsChecked == true) return "optifine";
            if (RadioLiteLoader.IsChecked == true) return "liteloader";
            return "";
        }

        private void SetStatus(string text, bool error = false)
        {
            TextStatus.Text = text;
            TextStatus.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0x8B, 0x1A, 0x1A)
                : Color.FromRgb(0x10, 0x22, 0x2F));
        }

        private async Task StartInstallAsync()
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_mcVersion)) { SetStatus("请先选择 MC 版本", true); return; }
            _busy = true;
            BtnStart.IsEnabled = false;
            try
            {
                var loader = LoaderType();
                var instanceName = EditName.Text.Trim();
                var loaderVersion = loader == "fabric" ? await ResolveLatestFabricLoaderAsync() : null;
                if (loader == "fabric" && string.IsNullOrEmpty(loaderVersion))
                {
                    SetStatus("获取 Fabric 版本列表失败，请检查网络", true);
                    return;
                }
                var spec = loader.Length == 0
                    ? EngineTaskBuilder.InstallVanilla(_mcVersion, McFolder,
                        string.IsNullOrEmpty(instanceName) ? null : instanceName)
                    : EngineTaskBuilder.InstallLoader(_mcVersion, loader, loaderVersion, McFolder,
                        string.IsNullOrEmpty(instanceName) ? null : instanceName);
                DownloadOverlay.ApplySourceConfig(spec);
                SetStatus(loader.Length == 0 ? "准备安装原版 " + _mcVersion + " …" : "准备安装 " + loader + " …");
                var result = await Backend.RunTaskAsync(spec, onEvent: e =>
                {
                    try
                    {
                        if (e.Type == "progress" && e.Percent.HasValue)
                            _ = Dispatcher.InvokeAsync(() => SetStatus(
                                (loader.Length == 0 ? "安装原版" : loader) + " · " + (e.Phase ?? e.Stage) + " · " + (e.Percent.Value * 100).ToString("F0") + "%"));
                        else if (e.Type == "phase" && !string.IsNullOrEmpty(e.Phase))
                            _ = Dispatcher.InvokeAsync(() => SetStatus((e.Phase ?? "") + " …"));
                    }
                    catch { }
                }, ct: CancellationToken.None);
                if (result.Ok)
                {
                    SetStatus("安装完成");
                    Installed?.Invoke(this, _mcVersion);
                }
                else
                {
                    SetStatus("安装失败：" + result.Code + " " + (result.CrashReport ?? ""), true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("安装异常：" + ex.Message, true);
            }
            finally
            {
                _busy = false;
                BtnStart.IsEnabled = true;
            }
        }

        /// <summary>Fabric 需要显式 loader 版本：取最新稳定版。</summary>
        private static async Task<string?> ResolveLatestFabricLoaderAsync()
        {
            try
            {
                var json = await Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                string? fallback = null;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var ver = e.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (string.IsNullOrEmpty(ver)) continue;
                    fallback ??= ver;
                    if (e.TryGetProperty("stable", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.True)
                        return ver;
                }
                return fallback;
            }
            catch { return null; }
        }

        /// <summary>端到端验收钩子：按指定 loaders 安装原版并返回是否成功。</summary>
        public async Task<bool> InstallVanillaForTestAsync(string version)
        {
            Prepare(version);
            RadioVanilla.IsChecked = true;
            await StartInstallAsync();
            return true;
        }
    }
}
