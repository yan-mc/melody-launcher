// =============================================================================
// Melody Launcher 引擎桥接 —— 前端调用封装（.NET 8 / WPF 可用）
// 目标路径：src/MelodyLauncher.Core/Bridge/EngineClient.cs
//
// 设计要点（对应分析报告 §4.4）：
//   * 引擎进程是独立子进程：EngineClient.RunTaskAsync() spawn → 读 JSONL 事件 → 返回结果；
//   * 事件来自 events.jsonl（不是 stdout）：WinExe 无控制台，文件通道最稳、可 tail、可回放；
//   * 取消/弹窗答复写 control.json（原子替换），后端 200ms 轮询；
//   * 引擎 exe 位置：默认与本程序同目录的 engine\core.exe（推荐，避免「释放 exe 被杀软误报」）；
//     只有单文件分发时才用 EmbeddedEngineResource 首次释放；
//   * 版本握手：started 事件的 engine.protocolVersion/build 与 task.options.requireEngine 校验；
//   * 单任务单进程：前台 UI 应串行调用（本类默认用 EngineGate 串行化所有任务）。
//
// 用法：
//   var backend = new EngineClient();
//   var result = await backend.RunTaskAsync(
//       EngineTaskBuilder.InstallVanilla("1.20.1", @"D:\Games\Melody\.minecraft"),
//       onEvent: e => Dispatcher.Invoke(() => UpdateUi(e)),
//       ct: token);
// =============================================================================
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MelodyLauncher.Core.Bridge;

public sealed class EngineClientOptions
{
    /// <summary>后端 exe。默认：&lt;程序目录&gt;\engine\core.exe（推荐随安装包分发）。</summary>
    public string EngineExePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "engine", "core.exe");

    /// <summary>任务工作目录（task.json / progress.jsonl / control.json）。建议 %LocalAppData%\MelodyLauncher\jobs。</summary>
    public string WorkRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MelodyLauncher", "jobs");

    /// <summary>
    /// 可选：把后端 exe 作为嵌入资源（Assembly 资源名）首次运行时释放。
    /// 注意：杀软对「exe 释放 exe 并执行」敏感；能用同目录分发就不要用这个。
    /// </summary>
    public string? EmbeddedEngineResource { get; set; }

    /// <summary>嵌入资源的 SHA-256（十六进制），释放后校验；为 null 则跳过。</summary>
    public string? EmbeddedEngineSha256 { get; set; }

    /// <summary>启动到收到 started 事件的超时。</summary>
    public int StartTimeoutMs { get; set; } = 60_000;

    /// <summary>取消后等待后端自行退出的时间，超时强杀进程树。</summary>
    public int CancelGraceMs { get; set; } = 15_000;

    /// <summary>等待 done 事件的兜底超时（默认 2 小时；安装大整合包可能很久）。</summary>
    public int TaskTimeoutMs { get; set; } = (int)TimeSpan.FromHours(2).TotalMilliseconds;

    /// <summary>是否把所有任务串行化（引擎单实例锁/共享状态所限，默认 true）。</summary>
    public bool SerializeTasks { get; set; } = true;
}

public sealed class EngineClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly SemaphoreSlim EngineGate = new(1, 1);

    private readonly EngineClientOptions _options;
    private Process? _process;
    private readonly object _controlLock = new();
    private readonly List<KeyValuePair<string, JsonElement>> _answers = new();
    private bool _cancelRequested;
    private bool _killGameRequested;
    private int _controlSeq;

    public EngineClient(EngineClientOptions? options = null) => _options = options ?? new EngineClientOptions();

    /// <summary>每条事件（已校验 seq 单调）。回调在工作线程触发，UI 侧记得 Dispatcher。</summary>
    public event Action<EngineEvent>? EventReceived;

    /// <summary>interaction=ask 时收到需要用户回答的弹窗请求。</summary>
    public event Action<EngineInteraction>? InteractionRequested;

    /// <summary>最近一次任务的 started 事件信息。</summary>
    public EngineInfo? EngineInfo { get; private set; }

    // =====================================================================
    // 主入口
    // =====================================================================

    public async Task<EngineTaskResult> RunTaskAsync(
        EngineTaskSpec spec,
        Action<EngineEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        if (_options.SerializeTasks) await EngineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunTaskCoreAsync(spec, onEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_options.SerializeTasks) EngineGate.Release();
        }
    }

    private async Task<EngineTaskResult> RunTaskCoreAsync(EngineTaskSpec spec, Action<EngineEvent>? onEvent, CancellationToken ct)
    {
        // 1) 准备任务目录与 spec 文件
        var workDir = Path.Combine(_options.WorkRoot, SanitizeName(spec.TaskId));
        Directory.CreateDirectory(workDir);
        spec.EventsPath = Path.Combine(workDir, "progress.jsonl");
        spec.ControlPath = Path.Combine(workDir, "control.json");
        _controlPath = spec.ControlPath;
        var specPath = Path.Combine(workDir, "task.json");
        if (File.Exists(spec.EventsPath)) File.Delete(spec.EventsPath);
        if (File.Exists(spec.ControlPath)) File.Delete(spec.ControlPath);
        WriteAtomic(specPath, JsonSerializer.Serialize(spec, JsonOpts));

        // 2) 定位/释放引擎
        var engineExe = await EnsureEngineAsync(ct).ConfigureAwait(false);

        // 3) 启动子进程
        var psi = new ProcessStartInfo
        {
            FileName = engineExe,
            WorkingDirectory = Path.GetDirectoryName(engineExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--task");
        psi.ArgumentList.Add(specPath);
        psi.ArgumentList.Add("--events");
        psi.ArgumentList.Add(spec.EventsPath);
        if (!string.IsNullOrEmpty(spec.ControlPath))
        {
            psi.ArgumentList.Add("--control");
            psi.ArgumentList.Add(spec.ControlPath);
        }
        psi.Environment["MELODY_BRIDGE"] = "1";

        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 引擎进程");
        // 后端是 WinExe，正常不会写 stdout；重定向只是兜底日志，避免管道塞满
        _ = DrainAsync(_process.StandardOutput, "[engine:out]");
        _ = DrainAsync(_process.StandardError, "[engine:err]");

        // 4) 启动事件 tail
        var tcs = new TaskCompletionSource<EngineTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tailTask = TailEventsAsync(spec, tcs, onEvent, tailCts.Token);

        // 5) 等 done / 进程退出 / 超时 / 取消
        using var timeoutCts = new CancellationTokenSource(_options.TaskTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        EngineTaskResult result;
        try
        {
            var doneTask = tcs.Task;
            var exitTask = _process.WaitForExitAsync(linked.Token);
            var completed = await Task.WhenAny(doneTask, exitTask).ConfigureAwait(false);

            if (linked.IsCancellationRequested)
            {
                if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _ = Task.Run(() => SendControlAsync(cancel: true, killGame: false, answers: null));
                    var ended = await WaitExitAsync(_options.CancelGraceMs).ConfigureAwait(false);
                    if (!ended) KillProcessTree();
                    return new EngineTaskResult { Ok = false, Code = "E_TIMEOUT", ExitCode = 1, Engine = EngineInfo };
                }
                _ = Task.Run(() => SendControlAsync(cancel: true, killGame: false, answers: null));
                var exited = await WaitExitAsync(_options.CancelGraceMs).ConfigureAwait(false);
                if (!exited) KillProcessTree();
                return new EngineTaskResult { Ok = false, Code = "E_CANCELED", ExitCode = 2, Engine = EngineInfo };
            }

            if (completed == doneTask)
            {
                result = await doneTask.ConfigureAwait(false);
            }
            else
            {
                // 进程先退出（异常崩溃 / 后端 bug / 被强杀）
                await Task.Delay(150).ConfigureAwait(false);
                if (!tcs.Task.IsCompleted)
                {
                    result = new EngineTaskResult
                    {
                        Ok = false,
                        Code = $"E_ENGINE_EXIT_{_process.ExitCode}",
                        ExitCode = _process.ExitCode,
                        Engine = EngineInfo
                    };
                }
                else result = await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _ = Task.Run(() => SendControlAsync(cancel: true, killGame: false, answers: null));
            var exited = await WaitExitAsync(_options.CancelGraceMs).ConfigureAwait(false);
            if (!exited) KillProcessTree();
            return new EngineTaskResult { Ok = false, Code = "E_CANCELED", ExitCode = 2, Engine = EngineInfo };
        }
        finally
        {
            tailCts.Cancel();
            try { await tailTask.ConfigureAwait(false); } catch { /* ignore */ }
        }

        // 6) 收尾：进程可能还活着（理论上 done 后立即 Environment.Exit）
        try
        {
            if (!_process.HasExited)
            {
                var exited = await WaitExitAsync(5000).ConfigureAwait(false);
                if (!exited) KillProcessTree();
            }
        }
        catch { /* ignore */ }
        return result;
    }

    // =====================================================================
    // 事件读取
    // =====================================================================

    private async Task TailEventsAsync(EngineTaskSpec spec, TaskCompletionSource<EngineTaskResult> tcs,
        Action<EngineEvent>? onEvent, CancellationToken ct)
    {
        var lastSeq = 0;
        string? lastResultJson = null;
        JsonElement? lastData = null;
        var tail = new JsonlTail(spec.EventsPath);
        while (!ct.IsCancellationRequested)
        {
            var events = tail.ReadNewEvents();
            foreach (var ev in events)
            {
                if (ev.Seq <= lastSeq) continue; // 重复/回放防御
                if (ev.Seq != lastSeq + 1 && lastSeq != 0)
                {
                    // 事件丢失：协议不变式被破坏，记录但不中断（后端可能写了非原子行）
                    Debug.WriteLine($"[bridge] seq 跳号：{lastSeq} → {ev.Seq}");
                }
                lastSeq = ev.Seq;

                if (ev.Type == "started")
                {
                    EngineInfo = ev.Engine;
                    if (EngineInfo != null)
                    {
                        var expected = spec.Options.RequireEngine;
                        if (expected != null && (EngineInfo.ProtocolVersion != EngineProtocol.Version ||
                                                 EngineInfo.Build < expected.MinBuild))
                        {
                            tcs.TrySetResult(new EngineTaskResult
                            {
                                Ok = false, Code = "E_ENGINE_MISMATCH", ExitCode = 1, Engine = EngineInfo
                            });
                            await SendControlAsync(cancel: true, killGame: false, answers: null).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                else if (ev.Type == "interaction")
                {
                    var interaction = ev.InteractionEvent;
                    if (interaction != null)
                    {
                        InteractionRequested?.Invoke(interaction);
                        // 没人订阅时按默认按钮自动回答，避免任务卡死
                        if (InteractionRequested == null)
                        {
                            await AnswerInteractionAsync(interaction.InteractionId, interaction.DefaultButton, null)
                                .ConfigureAwait(false);
                        }
                    }
                }
                else if (ev.Type == "result")
                {
                    lastResultJson = ev.Raw.GetRawText();
                    lastData = ev.Data;
                }
                else if (ev.Type == "done")
                {
                    var ok = ev.Ok ?? false;
                    tcs.TrySetResult(new EngineTaskResult
                    {
                        Ok = ok,
                        Code = ev.Code ?? (ok ? "OK" : "E_UNKNOWN"),
                        ExitCode = ev.ExitCode ?? (ok ? 0 : 1),
                        DurationMs = ev.DurationMs ?? 0,
                        Data = lastData,
                        CrashReport = TryGetCrashReport(lastResultJson),
                        Engine = EngineInfo
                    });
                }

                onEvent?.Invoke(ev);
                EventReceived?.Invoke(ev);
            }

            if (tcs.Task.IsCompleted) return;
            try { await Task.Delay(80, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string? TryGetCrashReport(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("report", out var report) &&
                data.TryGetProperty("crash", out var crash) &&
                crash.ValueKind == JsonValueKind.True)
            {
                return report.GetString();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // =====================================================================
    // 控制通道
    // =====================================================================

    /// <summary>取消当前任务（优雅中断加载器；超时后进程树被强杀）。</summary>
    public Task CancelAsync() => SendControlAsync(cancel: true, killGame: false, answers: null);

    /// <summary>结束当前正在运行的游戏进程（launch 任务存活期间有效）。</summary>
    public Task KillGameAsync() => SendControlAsync(cancel: false, killGame: true, answers: null);

    /// <summary>回答 interaction=ask 的弹窗请求。</summary>
    public Task AnswerInteractionAsync(string interactionId, int? button = null, string? text = null)
        => SendControlAsync(cancel: false, killGame: false,
            answers: new[] { (interactionId, button, text) });

    private async Task SendControlAsync(bool cancel, bool killGame,
        IEnumerable<(string Id, int? Button, string? Text)>? answers)
    {
        var specControlPath = _process == null ? null : _controlPath;
        if (string.IsNullOrEmpty(specControlPath)) return;
        lock (_controlLock)
        {
            _cancelRequested |= cancel;
            _killGameRequested |= killGame;
            if (answers != null)
            {
                foreach (var (id, button, text) in answers)
                {
                    var obj = new Dictionary<string, object?>();
                    if (button.HasValue) obj["button"] = button.Value;
                    if (text != null) obj["text"] = text;
                    _answers.Add(new KeyValuePair<string, JsonElement>(
                        id, JsonSerializer.SerializeToElement(obj, JsonOpts)));
                }
            }
            _controlSeq++;
            var payload = new Dictionary<string, object?>
            {
                ["seq"] = _controlSeq,
                ["cancel"] = _cancelRequested,
                ["killGame"] = _killGameRequested
            };
            var answerObj = new Dictionary<string, JsonElement>();
            foreach (var kv in _answers) answerObj[kv.Key] = kv.Value;
            if (answerObj.Count > 0) payload["answers"] = answerObj;
            try
            {
                WriteAtomic(specControlPath, JsonSerializer.Serialize(payload, JsonOpts));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[bridge] 写 control.json 失败：{ex}");
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string? _controlPath; // 由 RunTaskCoreAsync 在启动前赋值

    // =====================================================================
    // 引擎定位 / 释放
    // =====================================================================

    private async Task<string> EnsureEngineAsync(CancellationToken ct)
    {
        var path = _options.EngineExePath;
        if (File.Exists(path)) return path;

        if (string.IsNullOrEmpty(_options.EmbeddedEngineResource))
        {
            throw new FileNotFoundException(
                $"未找到 引擎进程：{path}。请把 engine\\core.exe 随安装包分发，或配置 EngineClientOptions.EmbeddedEngineResource。");
        }

        // 首次运行释放嵌入资源（不推荐用于公开分发，见报告 §4.6 杀软章节）
        var asm = typeof(EngineClient).Assembly;
        await using var stream = asm.GetManifestResourceStream(_options.EmbeddedEngineResource)
            ?? throw new FileNotFoundException("嵌入资源不存在：" + _options.EmbeddedEngineResource);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MelodyLauncher", "backend", "engine");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "core.exe");
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(_options.EmbeddedEngineSha256))
        {
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!string.Equals(hash, _options.EmbeddedEngineSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("引擎进程校验失败（SHA-256 不匹配），已删除。");
            }
        }
        File.Move(tmp, target, overwrite: true);
        WriteAtomic(target + ".build", DateTimeOffset.UtcNow.ToString("O"));
        return target;
    }

    // =====================================================================
    // 工具
    // =====================================================================

    private async Task<bool> WaitExitAsync(int timeoutMs)
    {
        if (_process == null) return true;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch { return true; }
    }

    private void KillProcessTree()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { Debug.WriteLine($"[bridge] 强杀后端失败：{ex}"); }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        // Windows 上后端可能正在以 FileShare.ReadWrite 打开旧文件：Move 替换偶发 IOException，重试几次
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 64 ? name[..64] : name;
    }

    private static async Task DrainAsync(StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Debug.WriteLine($"{prefix} {line}");
            }
        }
        catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAsync().ConfigureAwait(false);
        KillProcessTree();
        _process?.Dispose();
        _process = null;
    }
}

/// <summary>
/// 增量读取 JSONL：处理「最后一行只写了一半」的情况。
/// 与 Progress.jsonl 的「每行 flush」约定配合，可做到 &lt;100ms 延迟。
/// </summary>
internal sealed class JsonlTail
{
    private readonly string _path;
    private long _offset;
    private byte[] _pending = Array.Empty<byte>();

    public JsonlTail(string path) => _path = path;

    public List<EngineEvent> ReadNewEvents()
    {
        var result = new List<EngineEvent>();
        if (!File.Exists(_path)) return result;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= _offset) return result;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(fs.Length - _offset, 1 << 20)];
        var read = fs.Read(buffer, 0, buffer.Length);
        _offset += read;
        var combined = new byte[_pending.Length + read];
        Buffer.BlockCopy(_pending, 0, combined, 0, _pending.Length);
        Buffer.BlockCopy(buffer, 0, combined, _pending.Length, read);

        // 字节级切分：只在 '\n' 处消费，避免截断多字节 UTF-8 字符
        var start = 0;
        for (var i = 0; i < combined.Length; i++)
        {
            if (combined[i] != (byte)'\n') continue;
            var length = i - start;
            if (length > 0)
            {
                var line = Encoding.UTF8.GetString(combined, start, length).TrimEnd('\r');
                if (line.Length > 0) TryAdd(line, result);
            }
            start = i + 1;
        }
        _pending = combined[start..];
        return result;
    }

    private static void TryAdd(string line, List<EngineEvent> result)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ev = new EngineEvent
            {
                V = root.TryGetProperty("v", out var v) ? v.GetInt32() : 0,
                Seq = root.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0,
                Ts = root.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var dto)
                    ? dto : default,
                TaskId = root.TryGetProperty("taskId", out var tid) ? tid.GetString() ?? "" : "",
                Type = root.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                Raw = root.Clone()
            };
            result.Add(ev);
        }
        catch (JsonException)
        {
            // 半行/损坏行：跳过。后端每行都是独立 JSON，坏一行不影响后续
        }
    }
}

/// <summary>任务构造快捷方法（前端 UI 侧直接用）。</summary>
public static class EngineTaskBuilder
{
    private static EngineTaskOptions BaseOptions(string mcFolder) => new()
    {
        EngineAppName = "MelodyEngine",
        McFolder = mcFolder,
        LogLevel = "info",
        LogEvents = "warn",
        Interaction = "auto-first",
        Download = new EngineDownloadOptions { Source = "auto", VersionListSource = "auto" },
        Java = new EngineJavaOptions { AllowAutoDownload = true },
        RequireEngine = new EngineRequirement { MinBuild = 1 }
    };

    public static EngineTaskSpec ListVersions(string mcFolder) => new()
    {
        Type = EngineProtocol.ListVersions,
        Params = new() { ["includeSnapshots"] = true },
        Options = BaseOptions(mcFolder)
    };

    public static EngineTaskSpec InstallVanilla(string version, string mcFolder, string? instanceName = null) => new()
    {
        Type = EngineProtocol.InstallVanilla,
        Params = new() { ["version"] = version, ["instanceName"] = instanceName ?? version },
        Options = BaseOptions(mcFolder)
    };

    public static EngineTaskSpec InstallLoader(string mcVersion, string loaderType, string loaderVersion,
        string mcFolder, string? instanceName = null) => new()
    {
        Type = EngineProtocol.InstallLoader,
        Params = new()
        {
            ["version"] = mcVersion,
            ["loaderType"] = loaderType,
            ["loaderVersion"] = loaderVersion,
            ["instanceName"] = instanceName
        },
        Options = BaseOptions(mcFolder)
    };

    public static EngineTaskSpec InstallModpack(string file, string instanceName, string mcFolder) => new()
    {
        Type = EngineProtocol.InstallModpack,
        Params = new() { ["file"] = file, ["instanceName"] = instanceName },
        Options = BaseOptions(mcFolder)
    };

    public static EngineTaskSpec Launch(string version, string mcFolder, EngineLoginOptions login,
        int? memoryMB = null, (int W, int H)? window = null) => new()
    {
        Type = EngineProtocol.Launch,
        Params = new() { ["version"] = version, ["waitForGameExit"] = true },
        Options = new EngineTaskOptions
        {
            EngineAppName = "MelodyEngine",
            McFolder = mcFolder,
            LogLevel = "info",
            LogEvents = "warn",
            Interaction = "auto-first",
            MemoryMB = memoryMB,
            Window = window == null ? null : new EngineWindowOptions { Width = window.Value.W, Height = window.Value.H },
            Login = login,
            Download = new EngineDownloadOptions { Source = "auto" },
            Java = new EngineJavaOptions { AllowAutoDownload = true },
            RequireEngine = new EngineRequirement { MinBuild = 1 }
        }
    };

    public static EngineTaskSpec ModScan(string version, string mcFolder) => new()
    {
        Type = EngineProtocol.ModScan,
        Params = new() { ["version"] = version },
        Options = BaseOptions(mcFolder)
    };

    public static EngineTaskSpec ResourceSearch(string query, string mcFolder, string resourceType = "mod",
        string? gameVersion = null, string? loader = null) => new()
    {
        Type = EngineProtocol.ResourceSearch,
        Params = new()
        {
            ["query"] = query,
            ["resourceType"] = resourceType,
            ["gameVersion"] = gameVersion,
            ["loader"] = loader,
            ["limit"] = 40
        },
        Options = BaseOptions(mcFolder)
    };
}
