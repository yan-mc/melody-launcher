using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MelodyLauncher.Core.Bridge;
using MelodyLauncher.Services;
using MelodyLauncher.Services.Bridge;

namespace MelodyLauncher.Views
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings = AppSettings.Load();
        private EngineClient? _backend;
        private string _selectedVersion = "";
        private string? _launching;

        public MainWindow()
        {
            InitializeComponent();
            BtnClose.Click += (s, e) => Close();
            BtnSettings.Click += (s, e) => OpenSettings();
            BtnUpdate.Click += (s, e) => OpenDownload();
            BtnLaunch.Click += async (s, e) => await LaunchSelectedAsync();
            BtnVersionSettings.Click += (s, e) => OpenVersionSettings();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && OverlayHost.Visibility == Visibility.Visible)
                {
                    CloseOverlays();
                    e.Handled = true;
                }
            };

            DownloadPanel.CloseRequested += (s, e) => CloseOverlays();
            DownloadPanel.DownloadRequested += (s, ver) => OpenLoader(ver);
            DownloadPanel.CommunityRequested += (s, e) => OpenResources();
            LoaderPanel.CloseRequested += (s, e) => OpenDownload();
            LoaderPanel.Installed += (s, ver) =>
            {
                _settings = AppSettings.Load();
                _settings.LastVersion = ver;
                _settings.Save();
                LoadInstalledVersions();
                CloseOverlays();
                ShowMessage("安装完成", "版本 " + ver + " 已安装，可直接点击「启动游戏」。");
            };
            SettingsPanel.CloseRequested += (s, e) => CloseOverlays();
            SettingsPanel.SettingsSaved += (s, e) => { _settings = AppSettings.Load(); LoadInstalledVersions(); };
            SettingsPanel.MessageRequested += (s, msg) =>
            {
                var parts = msg.Split('\u0001');
                ShowMessage(parts[0], parts.Length > 1 ? parts[1] : "");
            };
            AboutPanel.CloseRequested += (s, e) => CloseOverlays();
            LogsPanel.CloseRequested += (s, e) => CloseOverlays();
            ModsPanel.CloseRequested += (s, e) => CloseOverlays();
            ResourcePanel.CloseRequested += (s, e) => CloseOverlays();
            AccountPanel.CloseRequested += (s, e) => CloseOverlays();
            VersionPanel.CloseRequested += (s, e) => CloseOverlays();
            AccountPanel.AccountChanged += (s, e) => { };
            SettingsPanel.AccountRequested += (s, e) => OpenAccount();
            SourcePanel.CloseRequested += (s, e) => CloseOverlays();
            SourcePanel.ConfigSaved += (s, e) => { };
            MsgBox.Dismissed += (s, e) => MsgHost.Visibility = Visibility.Collapsed;
            MenuDownload.Click += (s, e) => OpenDownload();
            MenuVersionSettings.Click += (s, e) => OpenVersionSettings();
            MenuAccount.Click += (s, e) => OpenAccount();
            MenuSource.Click += (s, e) => OpenSourceApi();
            MenuMods.Click += (s, e) => OpenMods();
            MenuResources.Click += (s, e) => OpenResources();
            MenuLogs.Click += (s, e) => OpenLogs();
            MenuAbout.Click += (s, e) => OpenAbout();
            MenuOpenMc.Click += (s, e) => OpenMcFolder();
            MenuCloseOverlay.Click += (s, e) => CloseOverlays();
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };
            Loaded += async (s, e) =>
            {
                LoadInstalledVersions();
                var args = Environment.GetCommandLineArgs();
                if (args.Contains("--open-settings")) OpenSettings();
                else if (args.Contains("--open-download")) OpenDownload();
                else if (args.Contains("--open-source")) OpenSourceApi();
                else if (args.Contains("--open-about")) OpenAbout();
                else if (args.Contains("--open-logs")) OpenLogs();
                else if (args.Contains("--open-mods")) OpenMods();
                else if (args.Contains("--open-resources")) OpenResources();
                else if (args.Contains("--open-version")) OpenVersionSettings();
                else if (args.Contains("--open-account")) OpenAccount();
                else if (args.Contains("--open-loader"))
                {
                    var li = Array.IndexOf(args, "--open-loader");
                    var lv = li >= 0 && li + 1 < args.Length ? args[li + 1] : _selectedVersion;
                    if (string.IsNullOrEmpty(lv)) lv = "1.20.1";
                    OpenDownload();
                    OpenLoader(lv);
                }
                else if (args.Contains("--launch-test")) await LaunchSelectedAsync();
                else
                {
                    var e2eIdx = Array.IndexOf(args, "--e2e");
                    if (e2eIdx >= 0 && e2eIdx + 1 < args.Length)
                    {
                        var ver = args[e2eIdx + 1];
                        OpenDownload();
                        var selected = await DownloadPanel.SelectForTestAsync(ver);
                        if (selected != null)
                        {
                            OpenLoader(selected);
                            await LoaderPanel.InstallVanillaForTestAsync(selected);
                            _settings = AppSettings.Load();
                            _settings.LastVersion = selected;
                            _settings.Save();
                            LoadInstalledVersions();
                            _selectedVersion = selected;
                            await LaunchSelectedAsync();
                        }
                    }
                }
            };
        }

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve(),
            WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MelodyLauncher", "jobs")
        });

        // ---------- installed versions ----------
        private void LoadInstalledVersions()
        {
            var versionsDir = Path.Combine(_settings.McFolder, "versions");
            var list = new System.Collections.Generic.List<string>();
            try
            {
                if (Directory.Exists(versionsDir))
                    foreach (var d in Directory.GetDirectories(versionsDir))
                    {
                        var id = Path.GetFileName(d);
                        if (File.Exists(Path.Combine(d, id + ".json"))) list.Add(id);
                    }
            }
            catch { }
            list.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));

            if (list.Count == 0)
            {
                VersionText.Text = "未安装版本";
                _selectedVersion = "";
            }
            else
            {
                var preferred = list.Contains(_settings.LastVersion) ? _settings.LastVersion : list[0];
                _selectedVersion = preferred;
                VersionText.Text = preferred;
            }
        }

        // ---------- actions ----------
        private async Task LaunchSelectedAsync()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                ShowMessage("警告", "没有选择游戏，请先下载安装一个版本");
                return;
            }
            if (_launching != null) return;
            _launching = _selectedVersion;
            BtnLaunch.IsEnabled = false;
            try
            {
                var account = AppSettings.Load();
                var useMs = !string.IsNullOrEmpty(account.MsRefreshToken);
                var login = useMs
                    ? new EngineLoginOptions
                    {
                        Type = "microsoft",
                        OAuthRefreshToken = account.MsRefreshToken,
                        UserName = account.MsUserName,
                        Uuid = account.MsUuid
                    }
                    : new EngineLoginOptions { Type = "offline", UserName = _settings.OfflineName };
                var spec = EngineTaskBuilder.Launch(_selectedVersion, _settings.McFolder, login,
                    _settings.MemoryMB, (_settings.WindowWidth, _settings.WindowHeight));
                var fresh = AppSettings.Load();
                if (!string.IsNullOrEmpty(fresh.JavaPath))
                    spec.Options.Java = new EngineJavaOptions { Path = fresh.JavaPath, AllowAutoDownload = true };
                if (!string.IsNullOrEmpty(fresh.OfflineName)) login.UserName = fresh.OfflineName;
                spec.Options.MemoryMB = fresh.MemoryMB;
                if (useMs) spec.Options.OAuthClientId = string.IsNullOrEmpty(fresh.MsClientId) ? null : fresh.MsClientId;
                var result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                // Java 校验失败时回退为自动选择 Java 再试一次
                if (!result.Ok && !string.IsNullOrEmpty(fresh.JavaPath))
                {
                    _settings.JavaPath = "";
                    _settings.Save();
                    var autoLogin = new EngineLoginOptions { Type = "offline", UserName = fresh.OfflineName };
                    var autoSpec = EngineTaskBuilder.Launch(_selectedVersion, fresh.McFolder, autoLogin,
                        fresh.MemoryMB, (fresh.WindowWidth, fresh.WindowHeight));
                    ShowMessage("提示", "指定的 Java 无法使用，已改为自动选择 Java 重新启动。");
                    result = await Backend.RunTaskAsync(autoSpec, onEvent: null, ct: CancellationToken.None);
                }
                if (!result.Ok)
                    ShowMessage("启动失败", result.Code + "\n" + (result.CrashReport ?? ""));
            }
            catch (Exception ex)
            {
                ShowMessage("启动异常", ex.Message);
            }
            finally
            {
                _launching = null;
                BtnLaunch.IsEnabled = true;
            }
        }

        private void OpenMcFolder()
        {
            try
            {
                var dir = _settings.McFolder;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { ShowMessage("打开失败", ex.Message); }
        }

        public void ShowMessage(string title, string body)
        {
            MsgBox.Show(title, body);
            MsgHost.Visibility = Visibility.Visible;
        }

        private void SetMainEnabled(bool enabled)
        {
            BtnLaunch.IsEnabled = enabled;
            BtnUpdate.IsEnabled = enabled;
            BtnSettings.IsEnabled = enabled;
            BtnVersionSettings.IsEnabled = enabled;
        }

        private void ShowOnly(UIElement panel)
        {
            OverlayHost.Visibility = Visibility.Visible;
            foreach (var child in OverlayHost.Children)
                if (child is UIElement el) el.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
            SetMainEnabled(false);
        }

        public void OpenSettings() => ShowOnly(SettingsPanel);
        public void OpenDownload()
        {
            ShowOnly(DownloadPanel);
        }

        public void OpenLoader(string mcVersion)
        {
            LoaderPanel.Prepare(mcVersion);
            ShowOnly(LoaderPanel);
        }

        public void OpenVersionSettings()
        {
            ShowOnly(VersionPanel);
            VersionPanel.Refresh();
        }

        public void OpenAccount()
        {
            ShowOnly(AccountPanel);
            AccountPanel.Refresh();
        }

        public void OpenResources()
        {
            ShowOnly(ResourcePanel);
            ResourcePanel.Refresh();
        }

        public void OpenMods()
        {
            ShowOnly(ModsPanel);
            ModsPanel.Refresh();
        }

        public void OpenLogs()
        {
            ShowOnly(LogsPanel);
            LogsPanel.Refresh();
        }

        public void OpenAbout()
        {
            ShowOnly(AboutPanel);
            AboutPanel.Refresh();
        }

        public void OpenSourceApi()
        {
            ShowOnly(SourcePanel);
            SourcePanel.Load();
        }

        public void CloseOverlays()
        {
            OverlayHost.Visibility = Visibility.Collapsed;
            foreach (var child in OverlayHost.Children)
                if (child is UIElement el) el.Visibility = Visibility.Collapsed;
            SetMainEnabled(true);
            _settings = AppSettings.Load();
            LoadInstalledVersions();
        }
    }
}
