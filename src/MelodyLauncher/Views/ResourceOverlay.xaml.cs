using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MelodyLauncher.Core.Bridge;
using MelodyLauncher.Services;
using MelodyLauncher.Services.Bridge;

namespace MelodyLauncher.Views
{
    /// <summary>
    /// 社区资源子页（698×268 浮层）：五类资源搜索 + Modrinth 直接下载到本地目录。
    /// 搜索走 引擎内核的 resource-search 任务；下载走 Modrinth 公开 API（CurseForge 无 API Key 不可用）。
    /// </summary>
    public partial class ResourceOverlay : UserControl
    {
        private sealed class Item
        {
            public string Name = "";
            public string Description = "";
            public string Website = "";
            public string Slug = "";
            public string Id = "";
            public int Platform;   // 1 = CurseForge, 2 = Modrinth（引擎 ResourcePlatforms 枚举）
            public int Types;      // 引擎 ResourceTypes 位标志
            public int Downloads;
            public Border? Row;
        }

        private const string ModrinthApi = "https://api.modrinth.com/v2";

        private static readonly HttpClient Http = CreateHttpClient();
        // 列表底色本身接近白色，选中/悬停用主题蓝色纱（白色叠加几乎看不出）
        private static readonly Brush RowHover = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0x74, 0xC2, 0xE5)));
        private static readonly Brush RowSelected = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0x74, 0xC2, 0xE5)));

        private readonly List<Item> _items = new();
        private EngineClient? _backend;
        private Item? _selected;
        private bool _searching;
        private bool _downloading;
        private CancellationTokenSource? _downloadCts;

        public event EventHandler CloseRequested;

        public ResourceOverlay()
        {
            InitializeComponent();

            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnSearch.Click += async (s, e) => await SearchAsync();
            BtnDownload.Click += async (s, e) => await DownloadSelectedAsync();
            BtnOpenWebsite.Click += (s, e) => OpenSelectedWebsite();
            BtnOpenFolder.Click += (s, e) => OpenTargetFolder();
            BtnCancel.Click += (s, e) => CancelDownload();
            EditQuery.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) await SearchAsync();
            };
            EditVersion.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) await SearchAsync();
            };
            RadioMod.Checked += (s, e) => OnTypeChanged();
            RadioModpack.Checked += (s, e) => OnTypeChanged();
            RadioResourcepack.Checked += (s, e) => OnTypeChanged();
            RadioShader.Checked += (s, e) => OnTypeChanged();
            RadioDatapack.Checked += (s, e) => OnTypeChanged();

            ComboLoader.Items.Add("任意");
            ComboLoader.Items.Add("Forge");
            ComboLoader.Items.Add("Fabric");
            ComboLoader.Items.Add("NeoForge");
            ComboLoader.SelectedIndex = 0;

            ComboLoader.IsEnabled = IsModLikeType();
            SetStatus("输入关键词搜索社区资源（版本 / 加载器可留空）");
        }

        // ---------------------------------------------------------------- 基础

        private EngineClient Backend => _backend ??= new EngineClient(new EngineClientOptions
        {
            EngineExePath = EngineLocator.Resolve(),
            WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MelodyLauncher", "jobs")
        });

        /// <summary>MainWindow 打开浮层时调用；保留上次结果，只刷新提示。</summary>
        public void Refresh()
        {
            if (_searching || _downloading) return;
            if (_items.Count == 0)
            {
                TextEmpty.Visibility = Visibility.Visible;
                TextEmpty.Text = "输入关键词开始搜索；单击选中结果，双击直接下载到本地";
                SetStatus("选择类型并输入关键词，点「搜索」；版本 / 加载器可留空");
            }
            else
            {
                SetStatus($"共 {_items.Count} 个结果；单击选中，双击或点「下载到本地」");
            }
        }

        private static T Frozen<T>(T brush) where T : Freezable
        {
            brush.Freeze();
            return brush;
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient
            {
                // 下载可能很慢，超时交给 CancellationToken / 内核任务超时控制
                Timeout = Timeout.InfiniteTimeSpan
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyLauncher/5.31.0");
            return http;
        }

        private void SetStatus(string text, bool error = false)
        {
            TextStatus.Text = text;
            TextStatus.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0x8B, 0x1A, 0x1A)
                : Color.FromRgb(0x17, 0x3C, 0x44));
            TextStatus.ToolTip = text;
        }

        private void UpdateBusyUi()
        {
            var busy = _searching || _downloading;
            BtnSearch.IsEnabled = !busy;
            BtnDownload.IsEnabled = !busy;
            BtnOpenWebsite.IsEnabled = !busy;
            BtnOpenFolder.IsEnabled = !busy;
            BtnCancel.IsEnabled = _downloading;
            EditQuery.IsEnabled = !busy;
            EditVersion.IsEnabled = !busy;
            ComboLoader.IsEnabled = !busy && IsModLikeType();
            RadioMod.IsEnabled = !busy;
            RadioModpack.IsEnabled = !busy;
            RadioResourcepack.IsEnabled = !busy;
            RadioShader.IsEnabled = !busy;
            RadioDatapack.IsEnabled = !busy;
            if (!busy)
            {
                Progress.Visibility = Visibility.Collapsed;
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
            }
        }

        // ---------------------------------------------------------------- 类型 / 参数

        private bool IsModLikeType() => RadioMod.IsChecked == true || RadioModpack.IsChecked == true;

        private string TypeParam()
        {
            if (RadioModpack.IsChecked == true) return "modpack";
            if (RadioResourcepack.IsChecked == true) return "resourcepack";
            if (RadioShader.IsChecked == true) return "shader";
            if (RadioDatapack.IsChecked == true) return "datapack";
            return "mod";
        }

        private string TypeDisplayName() => TypeParam() switch
        {
            "modpack" => "整合包",
            "resourcepack" => "资源包",
            "shader" => "光影",
            "datapack" => "数据包",
            _ => "Mod"
        };

        /// <summary>各类型对应的本地目录名（相对 .minecraft）。</summary>
        private static string TargetDirName(string type) => type switch
        {
            "modpack" => "modpacks",
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            "datapack" => "datapacks",
            _ => "mods"
        };

        private static string TargetDir(string mcFolder, string type)
            => MelodyLauncher.Services.InstancePaths.Resolve(mcFolder, null, TargetDirName(type));

        /// <summary>用于状态栏展示的「版本/mods」短路径。</summary>
        private static string TargetDisplay(string mcFolder, string type)
        {
            var s = MelodyLauncher.Services.AppSettings.Load();
            return (s.VersionIsolation && !string.IsNullOrEmpty(s.LastVersion) ? s.LastVersion + "/" : "")
                   + TargetDirName(type) + "/";
        }

        private string? LoaderParam()
        {
            if (!IsModLikeType()) return null;
            return ComboLoader.SelectedIndex switch
            {
                1 => "forge",
                2 => "fabric",
                3 => "neoforge",
                _ => null
            };
        }

        private void OnTypeChanged()
        {
            if (!IsLoaded || _searching || _downloading) return;
            _items.Clear();
            _selected = null;
            ResultList.Children.Clear();
            TextEmpty.Visibility = Visibility.Visible;
            TextEmpty.Text = "类型已切换为「" + TypeDisplayName() + "」，请重新搜索";
            ComboLoader.IsEnabled = IsModLikeType();
            SetStatus("类型：" + TypeDisplayName() + "，下载目录 " + TargetDisplay(AppSettings.Load().McFolder, TypeParam()));
        }

        // ---------------------------------------------------------------- 搜索

        private async Task SearchAsync()
        {
            if (_searching || _downloading) return;
            var query = EditQuery.Text.Trim();
            if (query.Length == 0)
            {
                SetStatus("请输入搜索关键词", true);
                EditQuery.Focus();
                return;
            }
            if (!EngineLocator.Exists)
            {
                SetStatus("未找到内核 engine\\core.exe，无法搜索", true);
                return;
            }

            var mcFolder = AppSettings.Load().McFolder;
            var type = TypeParam();
            var version = EditVersion.Text.Trim();
            var loader = LoaderParam() ?? "";

            _searching = true;
            UpdateBusyUi();
            SetStatus("正在搜索「" + Shorten(query, 24) + "」…");
            try
            {
                var spec = EngineTaskBuilder.ResourceSearch(query, mcFolder, type,
                    version.Length > 0 ? version : null,
                    loader.Length > 0 ? loader : null);
                var result = await Backend.RunTaskAsync(spec, OnSearchEvent, CancellationToken.None);

                _items.Clear();
                _selected = null;
                ResultList.Children.Clear();

                if (!result.Ok)
                {
                    TextEmpty.Visibility = Visibility.Visible;
                    TextEmpty.Text = "搜索失败：" + result.Code;
                    SetStatus("搜索失败：" + result.Code, true);
                    return;
                }

                string? err = null;
                if (result.Data is JsonElement data)
                {
                    if (data.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                        err = errEl.GetString();
                    if (data.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in arr.EnumerateArray())
                            if (r.ValueKind == JsonValueKind.Object)
                                _items.Add(ParseItem(r));
                    }
                }

                foreach (var item in _items) ResultList.Children.Add(BuildRow(item));
                Scroller.ScrollToTop();

                if (_items.Count == 0)
                {
                    TextEmpty.Visibility = Visibility.Visible;
                    TextEmpty.Text = "没有找到结果";
                    SetStatus(err == null ? "没有找到结果" : "没有找到结果：" + Shorten(err, 60), true);
                    return;
                }

                TextEmpty.Visibility = Visibility.Collapsed;
                var modrinth = _items.Count(x => x.Platform == 2);
                var curseForge = _items.Count(x => x.Platform == 1);
                var summary = $"共 {_items.Count} 个结果（Modrinth {modrinth}"
                              + (curseForge > 0 ? "，CurseForge " + curseForge : "") + "）";
                if (err != null) summary += " · CurseForge 需 API Key，建议用 Modrinth";
                SetStatus(summary);
            }
            catch (Exception ex)
            {
                TextEmpty.Visibility = Visibility.Visible;
                TextEmpty.Text = "搜索异常";
                SetStatus("搜索异常：" + ex.GetType().Name + " " + Shorten(ex.Message, 60), true);
            }
            finally
            {
                _searching = false;
                UpdateBusyUi();
            }
        }

        /// <summary>内核事件在工作线程触发，切回 UI 线程更新状态栏。</summary>
        private void OnSearchEvent(EngineEvent e)
        {
            try
            {
                if (e.Type != "progress" || _searching == false) return;
                var percent = e.Percent;
                var text = e.Phase ?? e.Stage ?? "搜索中";
                UiInvoke(() =>
                {
                    if (!_searching) return;
                    SetStatus(percent.HasValue
                        ? text + " " + (percent.Value * 100).ToString("F0") + "%"
                        : text);
                });
            }
            catch { /* 事件回调不能影响任务 */ }
        }

        private static Item ParseItem(JsonElement r) => new()
        {
            Name = SingleLine(Str(r, "RawName")),
            Description = SingleLine(Str(r, "Description")),
            Website = Str(r, "Website"),
            Slug = Str(r, "Slug"),
            Id = Str(r, "Id"),
            Platform = Int(r, "Platform"),
            Types = Int(r, "Types"),
            Downloads = Int(r, "DownloadCount")
        };

        private Border BuildRow(Item item)
        {
            var grid = new Grid { Height = 26 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

            var name = new TextBlock
            {
                Text = item.Name,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x32, 0x3D)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Name
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var desc = new TextBlock
            {
                Text = item.Description,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0x5A, 0x66)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Description
            };
            Grid.SetColumn(desc, 1);
            grid.Children.Add(desc);

            var platform = new TextBlock
            {
                Text = PlatformName(item.Platform),
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(item.Platform == 2
                    ? Color.FromRgb(0x2F, 0x6B, 0x4F)
                    : Color.FromRgb(0x8B, 0x5E, 0x24)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(platform, 2);
            grid.Children.Add(platform);

            var downloads = new TextBlock
            {
                Text = FormatDownloads(item.Downloads),
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0x58, 0x6B)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(downloads, 3);
            grid.Children.Add(downloads);

            var tooltip = "单击选中，双击下载\n来源：" + PlatformName(item.Platform)
                          + " · 类型：" + TypesText(item.Types);
            if (item.Website.Length > 0) tooltip += "\n" + item.Website;

            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            item.Row = row;
            row.MouseEnter += (s, e) =>
            {
                if (!ReferenceEquals(_selected, item)) row.Background = RowHover;
            };
            row.MouseLeave += (s, e) =>
            {
                if (!ReferenceEquals(_selected, item)) row.Background = Brushes.Transparent;
            };
            row.MouseLeftButtonUp += (s, e) => Select(item);
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                Select(item);
                _ = DownloadSelectedAsync();
            };
            return row;
        }

        private void Select(Item item)
        {
            foreach (var it in _items)
                if (it.Row != null) it.Row.Background = Brushes.Transparent;
            _selected = item;
            if (item.Row != null) item.Row.Background = RowSelected;
            var hint = item.Platform == 1 ? " · CurseForge 需 API Key，建议选 Modrinth" : "";
            SetStatus("已选中：" + item.Name + "（" + PlatformName(item.Platform) + "）" + hint);
        }

        // ---------------------------------------------------------------- 下载

        private async Task DownloadSelectedAsync()
        {
            if (_downloading || _searching) return;
            var item = _selected;
            if (item == null)
            {
                SetStatus("请先选中一个结果", true);
                return;
            }
            if (item.Platform == 1)
            {
                SetStatus("CurseForge 需要 API Key，建议换用 Modrinth 的结果", true);
                return;
            }
            if (item.Platform != 2)
            {
                SetStatus("该结果的来源未知，暂不支持直接下载", true);
                return;
            }
            if (item.Slug.Length == 0)
            {
                SetStatus("该结果缺少 Slug，无法定位下载文件", true);
                return;
            }

            var mcFolder = AppSettings.Load().McFolder;
            var type = TypeParam();
            var version = EditVersion.Text.Trim();
            var loader = LoaderParam() ?? "";
            var targetDir = TargetDir(mcFolder, type);
            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                SetStatus("无法创建目录：" + Shorten(ex.Message, 60), true);
                return;
            }

            _downloading = true;
            _downloadCts = new CancellationTokenSource();
            UpdateBusyUi();
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            SetStatus("正在获取 Modrinth 版本信息…");

            try
            {
                var pick = await ResolveModrinthFileAsync(item.Slug, version, loader, type, _downloadCts.Token);
                if (pick == null)
                {
                    SetStatus("Modrinth 上没有匹配的文件"
                              + (version.Length > 0 || loader.Length > 0 ? "（可尝试清空版本 / 加载器）" : ""), true);
                    return;
                }

                var fileName = SanitizeFileName(pick.Value.Name);
                var targetPath = Path.Combine(targetDir, fileName);
                SetStatus("开始下载 " + Shorten(fileName, 36));
                await DownloadFileAsync(pick.Value.Url, targetPath, pick.Value.Size, _downloadCts.Token);
                SetStatus("已下载：" + Shorten(fileName, 40) + " → " + TargetDisplay(mcFolder, type));
                TextStatus.ToolTip = targetPath;

                if (type == "modpack")
                    await InstallModpackAsync(targetPath, item, mcFolder);
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消下载");
            }
            catch (Exception ex)
            {
                SetStatus("下载失败：" + ex.GetType().Name + " " + Shorten(ex.Message, 60), true);
            }
            finally
            {
                _downloading = false;
                try { _downloadCts?.Dispose(); } catch { }
                _downloadCts = null;
                UpdateBusyUi();
            }
        }

        /// <summary>
        /// 用 Modrinth 公开 API 查询项目的版本列表，取第一个版本的 files（优先 primary），
        /// 返回下载地址 / 文件名 / 大小。带筛选条件查不到时自动退回不带筛选再试一次。
        /// </summary>
        private static async Task<(string Url, string Name, long Size)?> ResolveModrinthFileAsync(
            string slug, string gameVersion, string loader, string type, CancellationToken ct)
        {
            var urls = new List<string> { BuildVersionUrl(slug, gameVersion, loader, type) };
            if (gameVersion.Length > 0 || (loader.Length > 0 && type is "mod" or "modpack"))
                urls.Add(BuildVersionUrl(slug, "", "", type));

            foreach (var url in urls)
            {
                try
                {
                    using var apiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    apiCts.CancelAfter(TimeSpan.FromSeconds(30));
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, apiCts.Token);
                    if (!resp.IsSuccessStatusCode) continue;

                    await using var stream = await resp.Content.ReadAsStreamAsync(apiCts.Token);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: apiCts.Token);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var version in doc.RootElement.EnumerateArray())
                    {
                        var file = PickFile(version);
                        if (file != null) return file;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 网络/格式问题：换下一个候选 URL
                }
            }
            return null;
        }

        private static string BuildVersionUrl(string slug, string gameVersion, string loader, string type)
        {
            var url = ModrinthApi + "/project/" + Uri.EscapeDataString(slug) + "/version";
            var query = new List<string>();
            if (gameVersion.Length > 0)
                query.Add("game_versions=" + Uri.EscapeDataString("[\"" + gameVersion + "\"]"));
            if (loader.Length > 0 && type is "mod" or "modpack")
                query.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
            return query.Count == 0 ? url : url + "?" + string.Join("&", query);
        }

        private static (string Url, string Name, long Size)? PickFile(JsonElement version)
        {
            if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return null;
            JsonElement chosen = default;
            JsonElement first = default;
            var hasChosen = false;
            var hasFirst = false;
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                if (!hasFirst)
                {
                    first = f;
                    hasFirst = true;
                }
                if (f.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                {
                    chosen = f;
                    hasChosen = true;
                    break;
                }
            }
            if (!hasChosen)
            {
                if (!hasFirst) return null;
                chosen = first;
            }

            var url = Str(chosen, "url");
            if (url.Length == 0) return null;
            var name = Str(chosen, "filename");
            if (name.Length == 0)
            {
                try { name = Path.GetFileName(new Uri(url).AbsolutePath); } catch { }
            }
            if (name.Length == 0) name = "download.bin";
            long size = 0;
            if (chosen.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number)
                sizeEl.TryGetInt64(out size);
            return (url, name, size);
        }

        /// <summary>HttpClient 流式下载：先写 .part 临时文件，成功后再替换目标文件。</summary>
        private async Task DownloadFileAsync(string url, string targetPath, long sizeHint, CancellationToken ct)
        {
            var tmp = targetPath + ".part";
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? sizeHint;
                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                          81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReport = 0L;
                    var watch = Stopwatch.StartNew();
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, ct);
                        done += read;
                        if (watch.ElapsedMilliseconds - lastReport < 120) continue;
                        lastReport = watch.ElapsedMilliseconds;
                        ReportProgress(done, total);
                    }
                }
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmp, targetPath);
                ReportProgress(new FileInfo(targetPath).Length, Math.Max(total, 0));
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private void ReportProgress(long done, long total)
        {
            UiInvoke(() =>
            {
                if (!_downloading) return;
                Progress.Visibility = Visibility.Visible;
                if (total > 0)
                {
                    var percent = Math.Clamp(done * 100.0 / total, 0, 100);
                    Progress.IsIndeterminate = false;
                    Progress.Value = percent;
                    SetStatus($"下载中 {percent.ToString("F0")}% · {FormatSize(done)} / {FormatSize(total)}");
                }
                else
                {
                    Progress.IsIndeterminate = true;
                    SetStatus("下载中 " + FormatSize(done));
                }
            });
        }

        /// <summary>整合包：下载 .mrpack 后调用内核 install-modpack 安装；失败只保留文件并提示。</summary>
        private async Task InstallModpackAsync(string file, Item item, string mcFolder)
        {
            var instanceName = SanitizeInstanceName(item.Name);
            SetStatus("已下载 .mrpack，正在安装整合包「" + Shorten(instanceName, 20) + "」…");
            try
            {
                var spec = EngineTaskBuilder.InstallModpack(file, instanceName, mcFolder);
                var result = await Backend.RunTaskAsync(spec, OnInstallEvent, CancellationToken.None);
                if (result.Ok)
                    SetStatus("整合包安装完成：" + instanceName);
                else
                    SetStatus("已下载 .mrpack，但自动安装失败（" + result.Code + "）；文件保留在：" + file, true);
            }
            catch (Exception ex)
            {
                SetStatus("已下载 .mrpack，但安装异常（" + ex.GetType().Name + "）；文件保留在：" + file, true);
            }
        }

        private void OnInstallEvent(EngineEvent e)
        {
            try
            {
                if (e.Type != "progress" || !_downloading) return;
                var percent = e.Percent;
                var phase = e.Phase ?? e.Stage ?? "安装整合包";
                UiInvoke(() =>
                {
                    if (!_downloading) return;
                    if (percent.HasValue)
                    {
                        Progress.Visibility = Visibility.Visible;
                        Progress.IsIndeterminate = false;
                        Progress.Value = Math.Clamp(percent.Value * 100, 0, 100);
                        SetStatus(phase + " " + (percent.Value * 100).ToString("F0") + "%");
                    }
                    else
                    {
                        SetStatus(phase);
                    }
                });
            }
            catch { /* 事件回调不能影响任务 */ }
        }

        private void CancelDownload()
        {
            if (!_downloading) return;
            try
            {
                _downloadCts?.Cancel();
                SetStatus("正在取消下载…");
            }
            catch { }
        }

        // ---------------------------------------------------------------- 其它操作

        private void OpenSelectedWebsite()
        {
            var item = _selected;
            if (item == null)
            {
                SetStatus("请先选中一个结果", true);
                return;
            }
            if (item.Website.Length == 0)
            {
                SetStatus("该结果没有网页地址", true);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(item.Website) { UseShellExecute = true });
                SetStatus("已在浏览器打开：" + item.Website);
            }
            catch (Exception ex)
            {
                SetStatus("打开网页失败：" + Shorten(ex.Message, 60), true);
            }
        }

        private void OpenTargetFolder()
        {
            var dir = TargetDir(AppSettings.Load().McFolder, TypeParam());
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                SetStatus("已打开目录：" + dir);
            }
            catch (Exception ex)
            {
                SetStatus("打开目录失败：" + Shorten(ex.Message, 60), true);
            }
        }

        // ---------------------------------------------------------------- 工具

        private void UiInvoke(Action action)
        {
            try
            {
                if (Dispatcher.CheckAccess()) action();
                else Dispatcher.BeginInvoke(action);
            }
            catch { /* 窗口关闭 / 调度器停止时忽略 */ }
        }

        private static string Str(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return "";
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        }

        private static int Int(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return 0;
            return value.TryGetInt32(out var result) ? result : 0;
        }

        private static string PlatformName(int platform) => platform switch
        {
            1 => "CurseForge",
            2 => "Modrinth",
            _ => "其他"
        };

        private static string TypesText(int types)
        {
            if (types == 0) return "未知";
            var parts = new List<string>();
            if ((types & 1) != 0) parts.Add("Mod");
            if ((types & 2) != 0) parts.Add("整合包");
            if ((types & 4) != 0) parts.Add("资源包");
            if ((types & 8) != 0) parts.Add("光影");
            if ((types & 16) != 0) parts.Add("数据包");
            if ((types & 32) != 0) parts.Add("插件");
            if ((types & 64) != 0) parts.Add("地图");
            return parts.Count == 0 ? "未知" : string.Join(" / ", parts);
        }

        private static string FormatDownloads(int count)
        {
            if (count >= 100_000_000) return (count / 100_000_000.0).ToString("0.##") + " 亿";
            if (count >= 10_000) return (count / 10_000.0).ToString("0.#") + " 万";
            return count.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        private static string SingleLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = SingleLine(text);
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars).Trim();
            if (result.Length == 0) return "download.bin";
            if (result.Length > 180)
            {
                var extension = Path.GetExtension(result);
                if (extension.Length > 20) extension = "";
                result = result.Substring(0, 180 - extension.Length) + extension;
            }
            return result;
        }

        private static string SanitizeInstanceName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            var result = new string(chars).Trim();
            if (result.Length == 0) result = "整合包";
            return result.Length > 40 ? result.Substring(0, 40) : result;
        }
    }
}
