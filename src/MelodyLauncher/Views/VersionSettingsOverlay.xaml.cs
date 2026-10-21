using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MelodyLauncher.Core.Bridge;
using MelodyLauncher.Services;
using MelodyLauncher.Services.Bridge;

namespace MelodyLauncher.Views
{
    /// <summary>
    /// 版本设置浮层：左侧为已安装版本列表，右侧为自绘标签页
    /// （概览 / Mod / 资源包 / 光影 / 存档 / 截图 / 日志 / 设置）。
    /// 对外只暴露 <see cref="Refresh"/> 与 <see cref="CloseRequested"/>，主窗口可安全嵌入 OverlayHost。
    /// </summary>
    public partial class VersionSettingsOverlay : UserControl
    {
        private enum FileTabKind
        {
            ResourcePacks,
            ShaderPacks,
            Saves,
            Screenshots
        }

        private sealed class ModEntry
        {
            public string File = "";
            public string FullPath = "";
            public bool Enabled;
            public long Size;
        }

        private const int LogTailLines = 200;
        private const int TabCount = 8;

        // ---- 复用的画刷（避免每行都分配新对象） ----
        private static readonly Brush StatusBrush = FromRgb(0x17, 0x3C, 0x44);
        private static readonly Brush ErrorBrush = FromRgb(0xA8, 0x35, 0x2A);
        private static readonly Brush RowTextBrush = FromRgb(0x17, 0x32, 0x3D);
        private static readonly Brush NoteTextBrush = FromRgb(0x43, 0x5A, 0x66);
        private static readonly Brush EnabledBrush = FromRgb(0x2E, 0x6F, 0x48);
        private static readonly Brush DisabledBrush = FromRgb(0x8A, 0x64, 0x64);
        private static readonly Brush HoverBrush = FromArgb(0x22, 0xFF, 0xFF, 0xFF);
        private static readonly Brush SelectedRowBrush = FromArgb(0x55, 0xFF, 0xFF, 0xFF);

        private readonly List<(Border Row, string Version)> _versionRows = new();
        private readonly List<ModEntry> _mods = new();

        private AppSettings _settings;
        private EngineClient _backend;
        private string _selectedVersion;
        private string _selectedMod;
        private string _modsFolder = "";
        private int _tabIndex;
        private int _overviewToken;

        /// <summary>右上角 ✕ / 面板要求关闭。</summary>
        public event EventHandler CloseRequested;

        public VersionSettingsOverlay()
        {
            InitializeComponent();
            _settings = AppSettings.Load();

            BtnPanelClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnRefreshVersions.Click += (s, e) => ReloadVersions();

            // ---- 标签页 ----
            TabOverview.Checked += Tab_Checked;
            TabMod.Checked += Tab_Checked;
            TabResourcePacks.Checked += Tab_Checked;
            TabShaderPacks.Checked += Tab_Checked;
            TabSaves.Checked += Tab_Checked;
            TabScreenshots.Checked += Tab_Checked;
            TabLog.Checked += Tab_Checked;
            TabSettings.Checked += Tab_Checked;

            // ---- 概览 ----
            BtnOpenVersionDir.Click += (s, e) => OpenVersionDir();
            BtnOpenGameDir.Click += (s, e) => OpenDir(McFolder);
            BtnRenameVersion.Click += (s, e) => RenameVersion();
            BtnDeleteVersion.Click += (s, e) => DeleteVersion();

            // ---- Mod ----
            BtnModScan.Click += async (s, e) => await GuardAsync(ScanModsAsync);
            BtnModOpenDir.Click += (s, e) => OpenModsDir();
            BtnModEnable.Click += async (s, e) => await GuardAsync(() => SetModEnabledAsync(true));
            BtnModDisable.Click += async (s, e) => await GuardAsync(() => SetModEnabledAsync(false));

            // ---- 资源包 / 光影 / 存档 / 截图 ----
            BtnResourcePacksRefresh.Click += (s, e) => LoadFileTab(FileTabKind.ResourcePacks);
            BtnResourcePacksOpen.Click += (s, e) => OpenFileTab(FileTabKind.ResourcePacks);
            BtnShaderPacksRefresh.Click += (s, e) => LoadFileTab(FileTabKind.ShaderPacks);
            BtnShaderPacksOpen.Click += (s, e) => OpenFileTab(FileTabKind.ShaderPacks);
            BtnSavesRefresh.Click += (s, e) => LoadFileTab(FileTabKind.Saves);
            BtnSavesOpen.Click += (s, e) => OpenFileTab(FileTabKind.Saves);
            BtnScreenshotsRefresh.Click += (s, e) => LoadFileTab(FileTabKind.Screenshots);
            BtnScreenshotsOpen.Click += (s, e) => OpenFileTab(FileTabKind.Screenshots);

            // ---- 日志 ----
            BtnLogRefresh.Click += (s, e) => LoadLog();
            BtnLogCopy.Click += (s, e) => CopyLog();
            BtnLogOpen.Click += (s, e) => OpenLogDir();
            BtnCrashAnalyze.Click += async (s, e) => await GuardAsync(AnalyzeCrashAsync);

            // ---- 全局设置 ----
            BtnSaveGlobalSettings.Click += (s, e) => SaveGlobalSettings();
            BtnBrowseJava.Click += (s, e) => BrowseJava();
            BtnFindJava.Click += (s, e) => FindJava();

            LoadGlobalSettings();
            SelectTab(0);
            ReloadVersions();
        }

        // =====================================================================
        // 对外接口
        // =====================================================================

        /// <summary>重新读取设置与已安装版本列表（主窗口打开浮层时调用）。</summary>
        public void Refresh()
        {
            _settings = AppSettings.Load();
            LoadGlobalSettings();
            ReloadVersions();
        }

        // =====================================================================
        // 标签页
        // =====================================================================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton button && int.TryParse(button.Tag as string, out var index))
                SelectTab(index);
        }

        private void SelectTab(int index)
        {
            if (index < 0 || index >= TabCount) return;
            _tabIndex = index;
            var pages = new FrameworkElement[]
            {
                PageOverview, PageMod, PageResourcePacks, PageShaderPacks,
                PageSaves, PageScreenshots, PageLog, PageSettings
            };
            for (var i = 0; i < pages.Length; i++)
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            LoadCurrentTab();
        }

        private void LoadCurrentTab()
        {
            if (_tabIndex == 7)
            {
                LoadGlobalSettings();
                SetStatus("全局设置：内存 / Java / 窗口尺寸（保存后对所有版本生效）");
                return;
            }
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择一个版本", true);
                return;
            }
            switch (_tabIndex)
            {
                case 0:
                    StartOverviewInfo(_selectedVersion);
                    break;
                case 1:
                    if (_mods.Count == 0) SetStatus("点「扫描」读取该版本的 Mod 列表（也可本地扫描）");
                    break;
                case 2:
                    LoadFileTab(FileTabKind.ResourcePacks);
                    break;
                case 3:
                    LoadFileTab(FileTabKind.ShaderPacks);
                    break;
                case 4:
                    LoadFileTab(FileTabKind.Saves);
                    break;
                case 5:
                    LoadFileTab(FileTabKind.Screenshots);
                    break;
                case 6:
                    LoadLog();
                    break;
            }
        }

        // =====================================================================
        // 左侧版本列表
        // =====================================================================

        private void ReloadVersions(string preferVersion = null)
        {
            var settings = AppSettings.Load();
            _settings = settings;
            var previous = preferVersion ?? _selectedVersion;

            _versionRows.Clear();
            ListVersions.Children.Clear();

            var ids = new List<string>();
            try
            {
                var root = Path.Combine(settings.McFolder, "versions");
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var id = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(id) && File.Exists(Path.Combine(dir, id + ".json")))
                            ids.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("读取版本列表失败：" + ex.Message, true);
            }

            ids.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
            foreach (var id in ids)
                ListVersions.Children.Add(BuildVersionRow(id));

            var target = previous;
            if (string.IsNullOrEmpty(target) || !ids.Contains(target))
                target = ids.Contains(settings.LastVersion) ? settings.LastVersion : ids.FirstOrDefault();

            if (string.IsNullOrEmpty(target))
            {
                ClearVersionUi();
                SetStatus("没有已安装版本：请先通过「更新游戏」安装一个版本（" +
                          Path.Combine(settings.McFolder, "versions") + "）", true);
                return;
            }
            SelectVersion(target);
        }

        private Border BuildVersionRow(string id)
        {
            var text = new TextBlock
            {
                Text = id,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 12,
                Foreground = RowTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var grid = new Grid();
            grid.Children.Add(text);
            var row = new Border
            {
                Child = grid,
                Height = 24,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = id
            };
            row.MouseEnter += (s, e) =>
            {
                if (!string.Equals(_selectedVersion, id, StringComparison.Ordinal)) row.Background = HoverBrush;
            };
            row.MouseLeave += (s, e) =>
            {
                if (!string.Equals(_selectedVersion, id, StringComparison.Ordinal)) row.Background = Brushes.Transparent;
            };
            row.MouseLeftButtonUp += (s, e) => SelectVersion(id);
            _versionRows.Add((row, id));
            return row;
        }

        private void SelectVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            var dir = VersionDir(version);
            if (!Directory.Exists(dir))
            {
                SetStatus("版本目录不存在：" + dir, true);
                return;
            }

            _selectedVersion = version;
            foreach (var (row, id) in _versionRows)
                row.Background = string.Equals(id, version, StringComparison.Ordinal)
                    ? SelectedRowBrush : Brushes.Transparent;

            TextVersionPath.Text = dir;
            EditNewName.Text = version;

            _mods.Clear();
            _selectedMod = null;
            _modsFolder = "";
            ListMods.Children.Clear();
            TextModCount.Text = "";
            ClearFileList(FileTabKind.ResourcePacks);
            ClearFileList(FileTabKind.ShaderPacks);
            ClearFileList(FileTabKind.Saves);
            ClearFileList(FileTabKind.Screenshots);
            TextLogPath.Text = "";
            TextLogContent.Text = "";
            TextOverviewInfo.Text = "";

            SetStatus("已选择版本：" + version);
            LoadCurrentTab();
        }

        private void ClearVersionUi()
        {
            _selectedVersion = null;
            TextVersionPath.Text = "";
            EditNewName.Text = "";
            TextOverviewInfo.Text = "";
            _mods.Clear();
            _selectedMod = null;
            _modsFolder = "";
            ListMods.Children.Clear();
            TextModCount.Text = "";
            ClearFileList(FileTabKind.ResourcePacks);
            ClearFileList(FileTabKind.ShaderPacks);
            ClearFileList(FileTabKind.Saves);
            ClearFileList(FileTabKind.Screenshots);
            TextLogPath.Text = "";
            TextLogContent.Text = "";
        }

        // =====================================================================
        // 概览
        // =====================================================================

        private async void StartOverviewInfo(string version)
        {
            try
            {
                await LoadOverviewInfoAsync(version);
            }
            catch (Exception ex)
            {
                SetStatus("读取版本信息失败：" + ex.Message, true);
            }
        }

        private async Task LoadOverviewInfoAsync(string version)
        {
            var token = ++_overviewToken;
            var dir = VersionDir(version);
            TextVersionPath.Text = dir;

            var builder = new StringBuilder();
            var jsonPath = Path.Combine(dir, version + ".json");
            builder.Append(File.Exists(jsonPath) ? "版本 JSON：已就绪" : "版本 JSON：缺失（该版本可能不完整）");
            builder.Append("　实例方式：").Append(IsVersionIsolated(version) ? "版本隔离" : "全局共享");

            var stats = await Task.Run(() => GetDirStats(dir));
            if (token != _overviewToken || !string.Equals(_selectedVersion, version, StringComparison.Ordinal))
                return;

            builder.AppendLine();
            builder.Append("目录内容：").Append(stats.Files).Append(" 个文件 / ").Append(stats.Dirs).Append(" 个文件夹");
            if (stats.Bytes > 0) builder.Append("，占用 ").Append(FormatSize(stats.Bytes));
            builder.AppendLine();
            builder.Append("提示：重命名会同时修改版本文件夹与版本 JSON 文件名；删除版本为递归删除，不可恢复。");
            var text = builder.ToString();
            RunOnUi(() =>
            {
                if (string.Equals(_selectedVersion, version, StringComparison.Ordinal))
                    TextOverviewInfo.Text = text;
            });
        }

        private void OpenVersionDir()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            OpenDir(VersionDir(_selectedVersion));
        }

        private void RenameVersion()
        {
            var oldName = _selectedVersion;
            if (string.IsNullOrEmpty(oldName))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var newName = (EditNewName.Text ?? "").Trim();
            if (newName.Length == 0)
            {
                SetStatus("请输入新的版本名称", true);
                return;
            }
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                SetStatus("新名称与当前名称相同", true);
                return;
            }
            if (!IsSafeVersionName(newName))
            {
                SetStatus("名称不合法：不能包含 \\ / : * ? \" < > | 等字符或为 . / ..", true);
                return;
            }

            var oldDir = VersionDir(oldName);
            var newDir = VersionDir(newName);
            if (!Directory.Exists(oldDir))
            {
                SetStatus("版本目录不存在：" + oldDir, true);
                return;
            }
            if (Directory.Exists(newDir) || File.Exists(newDir))
            {
                SetStatus("目标版本已存在：" + newName, true);
                return;
            }
            if (!IsPathInside(oldDir, Path.Combine(McFolder, "versions")))
            {
                SetStatus("拒绝重命名：版本目录不在 versions 下", true);
                return;
            }

            var oldJson = Path.Combine(oldDir, oldName + ".json");
            var newJson = Path.Combine(oldDir, newName + ".json");
            var jsonRenamed = false;
            try
            {
                if (File.Exists(oldJson))
                {
                    File.Move(oldJson, newJson);
                    jsonRenamed = true;
                }
                try
                {
                    Directory.Move(oldDir, newDir);
                }
                catch
                {
                    // 文件夹移动失败时回滚 JSON 文件名，避免留下半成品
                    if (jsonRenamed && File.Exists(newJson) && !File.Exists(oldJson))
                    {
                        try { File.Move(newJson, oldJson); } catch { /* 回滚失败也只能接受 */ }
                    }
                    throw;
                }

                var settings = AppSettings.Load();
                if (string.Equals(settings.LastVersion, oldName, StringComparison.Ordinal))
                {
                    settings.LastVersion = newName;
                    settings.Save();
                }
                _selectedVersion = null;
                ReloadVersions(newName);
                SetStatus("已重命名版本：" + oldName + " → " + newName);
            }
            catch (Exception ex)
            {
                SetStatus("重命名失败：" + ex.Message, true);
            }
        }

        private void DeleteVersion()
        {
            var version = _selectedVersion;
            if (string.IsNullOrEmpty(version))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var versionsRoot = Path.Combine(McFolder, "versions");
            var dir = VersionDir(version);
            if (!Directory.Exists(dir))
            {
                SetStatus("版本目录不存在：" + dir, true);
                return;
            }
            // 安全护栏：只允许删除 versions\<id> 这一层，且必须是 versions 的子目录
            if (!IsPathInside(dir, versionsRoot))
            {
                SetStatus("拒绝删除：目标路径不在 versions 目录内", true);
                return;
            }
            if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                              Path.GetFullPath(versionsRoot).TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("拒绝删除：目标路径就是 versions 根目录", true);
                return;
            }

            if (!Confirm("删除版本",
                    "确定要删除版本「" + version + "」吗？\r\n\r\n" + dir +
                    "\r\n\r\n将递归删除整个版本目录，且无法恢复。"))
            {
                SetStatus("已取消删除");
                return;
            }

            try
            {
                Directory.Delete(dir, true);
                var settings = AppSettings.Load();
                if (string.Equals(settings.LastVersion, version, StringComparison.Ordinal))
                {
                    settings.LastVersion = "";
                    settings.Save();
                }
                _selectedVersion = null;
                ReloadVersions();
                SetStatus("已删除版本：" + version);
            }
            catch (Exception ex)
            {
                SetStatus("删除失败：" + ex.Message, true);
            }
        }

        // =====================================================================
        // Mod
        // =====================================================================

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve(),
            WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MelodyLauncher", "jobs")
        });

        private async Task ScanModsAsync()
        {
            var version = _selectedVersion;
            if (string.IsNullOrEmpty(version))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }

            BtnModScan.IsEnabled = false;
            SetStatus("正在扫描 Mod…");
            try
            {
                EngineTaskResult result;
                try
                {
                    var spec = EngineTaskBuilder.ModScan(version, McFolder);
                    result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    if (string.Equals(_selectedVersion, version, StringComparison.Ordinal))
                        LocalScanMods(version, "引擎不可用（" + ex.Message + "），已本地扫描：");
                    return;
                }

                if (!string.Equals(_selectedVersion, version, StringComparison.Ordinal)) return;

                _mods.Clear();
                _selectedMod = null;
                _modsFolder = "";
                ListMods.Children.Clear();

                if (!result.Ok || result.Data == null)
                {
                    LocalScanMods(version, "引擎扫描失败（" + result.Code + "），已本地扫描：");
                    return;
                }

                var data = result.Data.Value;
                _modsFolder = JsonString(data, "modsFolder");
                if (data.TryGetProperty("mods", out var modsArray) && modsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in modsArray.EnumerateArray())
                    {
                        _mods.Add(new ModEntry
                        {
                            File = JsonString(item, "file"),
                            FullPath = JsonString(item, "fullPath"),
                            Enabled = JsonBool(item, "enabled"),
                            Size = JsonLong(item, "size")
                        });
                    }
                }
                _mods.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
                foreach (var entry in _mods) ListMods.Children.Add(BuildModRow(entry));
                UpdateModCount();
                SetStatus(_mods.Count == 0 ? "扫描完成：该版本没有 Mod" : "扫描完成：" + TextModCount.Text);
            }
            finally
            {
                BtnModScan.IsEnabled = true;
            }
        }

        /// <summary>引擎不可用/失败时的兜底：直接读 mods 目录，按扩展名判断启用状态。</summary>
        private void LocalScanMods(string version, string note)
        {
            try
            {
                var modsDir = Path.Combine(InstanceRoot(version), "mods");
                _modsFolder = modsDir;
                _mods.Clear();
                _selectedMod = null;
                ListMods.Children.Clear();

                foreach (var path in SafeEnumerateFiles(modsDir))
                {
                    var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if (extension != "jar" && extension != "zip" && extension != "litemod" &&
                        extension != "disabled" && extension != "old") continue;
                    _mods.Add(new ModEntry
                    {
                        File = Path.GetFileName(path),
                        FullPath = path,
                        Enabled = extension != "disabled" && extension != "old",
                        Size = SafeFileSize(path)
                    });
                }
                _mods.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
                foreach (var entry in _mods) ListMods.Children.Add(BuildModRow(entry));
                UpdateModCount();
                SetStatus(note + (TextModCount.Text.Length > 0 ? TextModCount.Text : "0 个 Mod"));
            }
            catch (Exception ex)
            {
                SetStatus("扫描 Mod 失败：" + ex.Message, true);
            }
        }

        private void UpdateModCount()
        {
            TextModCount.Text = _mods.Count == 0
                ? ""
                : "共 " + _mods.Count + " 个（启用 " + _mods.Count(m => m.Enabled) + "）";
        }

        private Border BuildModRow(ModEntry entry)
        {
            var grid = new Grid { Height = 22 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var flag = new TextBlock
            {
                Text = entry.Enabled ? "启用" : "禁用",
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = entry.Enabled ? EnabledBrush : DisabledBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            grid.Children.Add(flag);

            var name = new TextBlock
            {
                Text = entry.File,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 12,
                Foreground = RowTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var size = new TextBlock
            {
                Text = FormatSize(entry.Size),
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 10,
                Foreground = NoteTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = entry.File
            };
            var captured = entry;
            row.MouseEnter += (s, e) =>
            {
                if (!string.Equals(_selectedMod, captured.File, StringComparison.Ordinal)) row.Background = HoverBrush;
            };
            row.MouseLeave += (s, e) =>
            {
                if (!string.Equals(_selectedMod, captured.File, StringComparison.Ordinal)) row.Background = Brushes.Transparent;
            };
            row.MouseLeftButtonDown += async (s, e) =>
            {
                _selectedMod = captured.File;
                foreach (var child in ListMods.Children.OfType<Border>()) child.Background = Brushes.Transparent;
                row.Background = SelectedRowBrush;
                SetStatus("已选中 Mod：" + captured.File + "（双击可切换启用状态）");
                if (e.ClickCount == 2)
                    await GuardAsync(() => SetModEnabledAsync(!captured.Enabled));
            };
            return row;
        }

        private async Task SetModEnabledAsync(bool enabled)
        {
            var version = _selectedVersion;
            if (string.IsNullOrEmpty(version))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var entry = _mods.FirstOrDefault(m => string.Equals(m.File, _selectedMod, StringComparison.Ordinal));
            if (entry == null)
            {
                SetStatus("请先在列表中选中一个 Mod", true);
                return;
            }

            BtnModEnable.IsEnabled = false;
            BtnModDisable.IsEnabled = false;
            SetStatus((enabled ? "正在启用：" : "正在禁用：") + entry.File);
            try
            {
                EngineTaskResult result;
                try
                {
                    var spec = new EngineTaskSpec
                    {
                        Type = EngineProtocol.ModSetEnabled,
                        Params = new Dictionary<string, object>
                        {
                            ["version"] = version,
                            ["file"] = entry.File,
                            ["enabled"] = enabled
                        },
                        Options = new EngineTaskOptions
                        {
                            EngineAppName = "MelodyEngine",
                            McFolder = McFolder,
                            RequireEngine = new EngineRequirement { MinBuild = 1 }
                        }
                    };
                    result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    if (TryLocalSetEnabled(entry, enabled, out var localMessage))
                    {
                        await ScanModsAsync();
                        SetStatus(localMessage + "（引擎不可用：" + ex.Message + "）");
                    }
                    else
                    {
                        SetStatus("操作异常：" + ex.Message, true);
                    }
                    return;
                }

                if (result.Ok)
                {
                    await ScanModsAsync();
                    SetStatus("已" + (enabled ? "启用" : "禁用") + "：" + entry.File);
                }
                else if (TryLocalSetEnabled(entry, enabled, out var localMessage2))
                {
                    await ScanModsAsync();
                    SetStatus(localMessage2 + "（引擎返回：" + result.Code + "）");
                }
                else
                {
                    SetStatus("操作失败：" + result.Code + (string.IsNullOrEmpty(result.CrashReport) ? "" : " " + result.CrashReport), true);
                }
            }
            finally
            {
                BtnModEnable.IsEnabled = true;
                BtnModDisable.IsEnabled = true;
            }
        }

        /// <summary>与引擎 ModSetEnabled 相同规则的文件级兜底：.jar/.zip/.litemod ↔ .disabled/.old。</summary>
        private static bool TryLocalSetEnabled(ModEntry entry, bool enabled, out string message)
        {
            message = "";
            try
            {
                if (string.IsNullOrEmpty(entry.FullPath) || !File.Exists(entry.FullPath))
                {
                    message = "文件不存在：" + entry.File;
                    return false;
                }
                if (enabled)
                {
                    if (entry.Enabled)
                    {
                        message = "已经是启用状态：" + entry.File;
                        return true;
                    }
                    var directory = Path.GetDirectoryName(entry.FullPath) ?? "";
                    var baseName = entry.File.Replace(".disabled", "", StringComparison.OrdinalIgnoreCase)
                                            .Replace(".old", "", StringComparison.OrdinalIgnoreCase);
                    var target = Path.Combine(directory, baseName);
                    if (File.Exists(target))
                    {
                        message = "已存在同名启用文件：" + baseName;
                        return false;
                    }
                    File.Move(entry.FullPath, target);
                }
                else
                {
                    if (!entry.Enabled)
                    {
                        message = "已经是禁用状态：" + entry.File;
                        return true;
                    }
                    var target = File.Exists(entry.FullPath + ".old")
                        ? entry.FullPath + ".old"
                        : entry.FullPath + ".disabled";
                    if (File.Exists(target))
                    {
                        message = "已存在：" + Path.GetFileName(target);
                        return false;
                    }
                    File.Move(entry.FullPath, target);
                }
                message = "已" + (enabled ? "启用" : "禁用") + "：" + entry.File;
                return true;
            }
            catch (Exception ex)
            {
                message = "文件操作失败：" + ex.Message;
                return false;
            }
        }

        private void OpenModsDir()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var dir = string.IsNullOrEmpty(_modsFolder)
                ? Path.Combine(InstanceRoot(_selectedVersion), "mods")
                : _modsFolder;
            OpenDir(dir);
        }

        // =====================================================================
        // 资源包 / 光影 / 存档 / 截图
        // =====================================================================

        private (string Folder, StackPanel List, TextBlock Path, TextBlock Count) FileTabWidgets(FileTabKind kind)
        {
            switch (kind)
            {
                case FileTabKind.ResourcePacks:
                    return ("resourcepacks", ListResourcePacks, TextResourcePacksPath, TextResourcePacksCount);
                case FileTabKind.ShaderPacks:
                    return ("shaderpacks", ListShaderPacks, TextShaderPacksPath, TextShaderPacksCount);
                case FileTabKind.Saves:
                    return ("saves", ListSaves, TextSavesPath, TextSavesCount);
                default:
                    return ("screenshots", ListScreenshots, TextScreenshotsPath, TextScreenshotsCount);
            }
        }

        private void LoadFileTab(FileTabKind kind)
        {
            var widgets = FileTabWidgets(kind);
            widgets.List.Children.Clear();
            widgets.Count.Text = "";

            if (string.IsNullOrEmpty(_selectedVersion))
            {
                widgets.Path.Text = "";
                widgets.List.Children.Add(BuildHintRow("（请先在左侧选择版本）"));
                return;
            }

            var dir = Path.Combine(InstanceRoot(_selectedVersion), widgets.Folder);
            widgets.Path.Text = dir;
            var count = 0;
            try
            {
                if (!Directory.Exists(dir))
                {
                    widgets.List.Children.Add(BuildHintRow("（目录不存在，点「打开目录」会自动创建）"));
                }
                else
                {
                    foreach (var subDir in SafeEnumerateDirs(dir))
                    {
                        widgets.List.Children.Add(BuildFileRow(Path.GetFileName(subDir) + "\\", "文件夹"));
                        count++;
                    }
                    foreach (var file in SafeEnumerateFiles(dir))
                    {
                        widgets.List.Children.Add(BuildFileRow(Path.GetFileName(file), FormatSize(SafeFileSize(file))));
                        count++;
                    }
                    if (count == 0) widgets.List.Children.Add(BuildHintRow("（目录为空）"));
                }
                widgets.Count.Text = count + " 项";
                SetStatus("已加载 " + count + " 项：" + dir);
            }
            catch (Exception ex)
            {
                widgets.List.Children.Add(BuildHintRow("（读取失败：" + ex.Message + "）"));
                SetStatus("读取目录失败：" + ex.Message, true);
            }
        }

        private void ClearFileList(FileTabKind kind)
        {
            var widgets = FileTabWidgets(kind);
            widgets.Path.Text = "";
            widgets.List.Children.Clear();
            widgets.Count.Text = "";
        }

        private void OpenFileTab(FileTabKind kind)
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var widgets = FileTabWidgets(kind);
            OpenDir(Path.Combine(InstanceRoot(_selectedVersion), widgets.Folder));
        }

        private static Border BuildFileRow(string name, string rightText)
        {
            var grid = new Grid { Height = 20 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

            var nameBlock = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = RowTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            grid.Children.Add(nameBlock);

            var rightBlock = new TextBlock
            {
                Text = rightText,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 10,
                Foreground = NoteTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(rightBlock, 1);
            grid.Children.Add(rightBlock);

            var row = new Border { Child = grid, Background = Brushes.Transparent };
            row.MouseEnter += (s, e) => row.Background = HoverBrush;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            return row;
        }

        private static Border BuildHintRow(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = NoteTextBrush,
                Margin = new Thickness(6, 4, 6, 4),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            return new Border { Child = block, Background = Brushes.Transparent };
        }

        // =====================================================================
        // 日志 / 崩溃分析
        // =====================================================================

        private void LoadLog()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var path = ResolveLogPath(_selectedVersion);
            TextLogPath.Text = path;
            try
            {
                if (!File.Exists(path))
                {
                    TextLogContent.Text = "（没有找到 latest.log）\r\n" + path;
                    SetStatus("未找到日志文件：" + path, true);
                    return;
                }
                var lines = ReadTail(path, LogTailLines);
                TextLogContent.Text = string.Join(Environment.NewLine, lines);
                TextLogContent.ScrollToEnd();
                SetStatus("已加载日志末尾 " + lines.Length + " 行：" + path);
            }
            catch (Exception ex)
            {
                TextLogContent.Text = "读取日志失败：" + ex.Message;
                SetStatus("读取日志失败：" + ex.Message, true);
            }
        }

        private string ResolveLogPath(string version)
        {
            var candidates = new[]
            {
                Path.Combine(VersionDir(version), "logs", "latest.log"),
                Path.Combine(InstanceRoot(version), "logs", "latest.log"),
                Path.Combine(McFolder, "logs", "latest.log")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return candidates[0];
        }

        private void OpenLogDir()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }
            var candidates = new[]
            {
                Path.Combine(VersionDir(_selectedVersion), "logs"),
                Path.Combine(InstanceRoot(_selectedVersion), "logs"),
                Path.Combine(McFolder, "logs")
            };
            var dir = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
            OpenDir(dir);
        }

        private void CopyLog()
        {
            try
            {
                if (string.IsNullOrEmpty(TextLogContent.Text))
                {
                    SetStatus("日志内容为空", true);
                    return;
                }
                Clipboard.SetText(TextLogContent.Text);
                SetStatus("日志已复制到剪贴板");
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message, true);
            }
        }

        private async Task AnalyzeCrashAsync()
        {
            var version = _selectedVersion;
            if (string.IsNullOrEmpty(version))
            {
                SetStatus("请先在左侧选择版本", true);
                return;
            }

            BtnCrashAnalyze.IsEnabled = false;
            SetStatus("正在做崩溃分析…");
            try
            {
                var spec = new EngineTaskSpec
                {
                    Type = EngineProtocol.AnalyzeCrash,
                    Params = new Dictionary<string, object> { ["version"] = version },
                    Options = new EngineTaskOptions
                    {
                        EngineAppName = "MelodyEngine",
                        McFolder = McFolder,
                        LogLevel = "info",
                        LogEvents = "warn",
                        Interaction = "auto-first",
                        RequireEngine = new EngineRequirement { MinBuild = 1 }
                    }
                };
                var result = await Backend.RunTaskAsync(spec, onEvent: null, ct: CancellationToken.None);
                if (!result.Ok)
                {
                    SetStatus("崩溃分析失败：" + result.Code, true);
                    if (!string.IsNullOrEmpty(result.CrashReport)) TextLogContent.Text = result.CrashReport;
                    return;
                }

                var found = false;
                string report = null;
                if (result.Data != null)
                {
                    var data = result.Data.Value;
                    found = JsonBool(data, "found");
                    report = JsonString(data, "report");
                }
                if (string.IsNullOrEmpty(report)) report = result.CrashReport;
                if (string.IsNullOrEmpty(report))
                {
                    SetStatus("崩溃分析完成，但没有返回报告文本", true);
                    return;
                }
                TextLogContent.Text = report;
                TextLogPath.Text = "崩溃分析结果（" + version + "）";
                TextLogContent.ScrollToEnd();
                SetStatus(found ? "崩溃分析完成，已显示报告" : "崩溃分析完成：没有找到可分析的崩溃日志");
            }
            catch (Exception ex)
            {
                SetStatus("崩溃分析异常：" + ex.Message, true);
            }
            finally
            {
                BtnCrashAnalyze.IsEnabled = true;
            }
        }

        private static string[] ReadTail(string path, int count)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                    if (lines.Count > count) lines.RemoveAt(0);
                }
                return lines.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // =====================================================================
        // 全局设置
        // =====================================================================

        private void LoadGlobalSettings()
        {
            try
            {
                var settings = AppSettings.Load();
                _settings = settings;
                EditMemory.Text = settings.MemoryMB.ToString();
                EditJava.Text = settings.JavaPath ?? "";
                EditWinW.Text = settings.WindowWidth.ToString();
                EditWinH.Text = settings.WindowHeight.ToString();
            }
            catch (Exception ex)
            {
                SetStatus("读取全局设置失败：" + ex.Message, true);
            }
        }

        private void SaveGlobalSettings()
        {
            try
            {
                if (!int.TryParse((EditMemory.Text ?? "").Trim(), out var memory) || memory < 256 || memory > 65536)
                {
                    SetStatus("内存需为 256–65536 之间的整数（MB）", true);
                    return;
                }
                if (!int.TryParse((EditWinW.Text ?? "").Trim(), out var width) || width < 100 || width > 10000)
                {
                    SetStatus("窗口宽度需为 100–10000 之间的整数", true);
                    return;
                }
                if (!int.TryParse((EditWinH.Text ?? "").Trim(), out var height) || height < 100 || height > 10000)
                {
                    SetStatus("窗口高度需为 100–10000 之间的整数", true);
                    return;
                }

                var settings = AppSettings.Load();
                settings.MemoryMB = memory;
                settings.WindowWidth = width;
                settings.WindowHeight = height;
                settings.JavaPath = (EditJava.Text ?? "").Trim();
                settings.Save();
                _settings = settings;

                var note = "";
                if (settings.JavaPath.Length > 0 && !File.Exists(settings.JavaPath))
                    note = "（注意：该路径下没有找到文件）";
                SetStatus("全局设置已保存" + note);
            }
            catch (Exception ex)
            {
                SetStatus("保存设置失败：" + ex.Message, true);
            }
        }

        private void BrowseJava()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "javaw.exe|javaw.exe|可执行文件|*.exe",
                    Title = "选择 javaw.exe"
                };
                var owner = Window.GetWindow(this);
                var ok = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (ok == true)
                {
                    EditJava.Text = dialog.FileName;
                    SetStatus("已选择 Java：" + dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                SetStatus("选择 Java 失败：" + ex.Message, true);
            }
        }

        private void FindJava()
        {
            try
            {
                var found = JavaScanner.FindJava();
                if (found.Count == 0)
                {
                    SetStatus("未在常见位置找到 Java，请手动浏览选择 javaw.exe", true);
                    return;
                }
                EditJava.Text = found[0];
                SetStatus("找到 " + found.Count + " 个 Java，已填入第一个：" + found[0]);
            }
            catch (Exception ex)
            {
                SetStatus("查找 Java 失败：" + ex.Message, true);
            }
        }

        // =====================================================================
        // 基础设施
        // =====================================================================

        private string McFolder => _settings.McFolder;

        private string VersionDir(string version) => Path.Combine(McFolder, "versions", version);

        /// <summary>
        /// 判断该版本是否使用版本隔离（引擎的 PathIndie 指向版本目录）：
        /// 版本目录下出现 mods/saves/resourcepacks/shaderpacks/config/logs 任一目录即视为隔离实例。
        /// 这是启发式判断，用于选择「打开/扫描哪个目录」。
        /// </summary>
        private bool IsVersionIsolated(string version)
        {
            // 统一跟随全局「版本隔离」设置（引擎 2.13+ 默认开启），
            // 保证本页扫描的目录与 引擎 引擎/资源下载使用同一路径。
            return MelodyLauncher.Services.InstancePaths.IsIsolated && !string.IsNullOrWhiteSpace(version);
        }

        private string InstanceRoot(string version)
            => IsVersionIsolated(version) ? VersionDir(version) : McFolder;

        private void OpenDir(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                SetStatus("已打开目录：" + dir);
            }
            catch (Exception ex)
            {
                SetStatus("打开目录失败：" + ex.Message, true);
            }
        }

        private void SetStatus(string text, bool error = false)
        {
            // 防御性线程封送：正常 WPF 应用里始终在 UI 线程；若宿主在消息循环启动前构造本控件，
            // 异步续体可能落在工作线程，这里统一回到 Dispatcher 上更新，避免抛跨线程异常。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetStatus(text, error)));
                return;
            }
            TextStatus.Text = text ?? "";
            TextStatus.Foreground = error ? ErrorBrush : StatusBrush;
            TextStatus.ToolTip = text;
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        private async Task GuardAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                SetStatus("操作失败：" + ex.Message, true);
            }
        }

        private bool Confirm(string title, string message)
        {
            try
            {
                var owner = Window.GetWindow(this);
                var result = owner == null
                    ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                return result == MessageBoxResult.Yes;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeVersionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            if (name.Contains("/") || name.Contains("\\")) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static bool IsPathInside(string child, string parent)
        {
            try
            {
                var childFull = Path.GetFullPath(child)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parentFull = Path.GetFullPath(parent)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return childFull.Length > parentFull.Length &&
                       childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static (long Files, long Dirs, long Bytes) GetDirStats(string dir)
        {
            long files = 0, dirs = 0, bytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += SafeFileSize(file);
                }
                foreach (var _ in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                    dirs++;
            }
            catch { }
            return (files, dirs, bytes);
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirs(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static long SafeFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }

        private static string JsonString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "")
                : "";
        }

        private static bool JsonBool(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }

        private static long JsonLong(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt64(out var number)
                ? number
                : 0;
        }

        private static Brush FromRgb(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush FromArgb(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
