// =============================================================================
// Melody Launcher 引擎桥接 —— 协议 DTO（.NET 8，System.Text.Json）
// 目标路径：src/MelodyLauncher/Services/Bridge/EngineModels.cs
// 与 work/poc/engine-bridge/protocol/*.schema.json 一一对应。
// =============================================================================
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MelodyLauncher.Core.Bridge;

/// <summary>协议常量。</summary>
public static class EngineProtocol
{
    public const int Version = 1;

    // 任务类型（== task.schema.json 的 type 枚举）
    public const string ListVersions = "list-versions";
    public const string InstallVanilla = "install-vanilla";
    public const string InstallLoader = "install-loader";
    public const string InstallModpack = "install-modpack";
    public const string Launch = "launch";
    public const string JavaList = "java-list";
    public const string JavaInstall = "java-install";
    public const string ModScan = "mod-scan";
    public const string ModSetEnabled = "mod-set-enabled";
    public const string ResourceSearch = "resource-search";
    public const string AnalyzeCrash = "analyze-crash";
    public const string Shutdown = "shutdown";
}

/// <summary>任务描述。序列化后即 task.json。</summary>
public sealed class EngineTaskSpec
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = EngineProtocol.Version;
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = Guid.NewGuid().ToString("D");
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("params")] public Dictionary<string, object?> Params { get; set; } = new();
    [JsonPropertyName("options")] public EngineTaskOptions Options { get; set; } = new();
    [JsonPropertyName("eventsPath")] public string EventsPath { get; set; } = "";
    [JsonPropertyName("controlPath")] public string? ControlPath { get; set; }
}

public sealed class EngineTaskOptions
{
    [JsonPropertyName("engineAppName")] public string EngineAppName { get; set; } = "MelodyEngine";
    [JsonPropertyName("mcFolder")] public string? McFolder { get; set; }
    [JsonPropertyName("cacheDir")] public string? CacheDir { get; set; }
    [JsonPropertyName("logLevel")] public string LogLevel { get; set; } = "info";
    [JsonPropertyName("logEvents")] public string LogEvents { get; set; } = "warn";

    /// <summary>auto-first | auto-second | fail | ask</summary>
    [JsonPropertyName("interaction")] public string Interaction { get; set; } = "auto-first";
    [JsonPropertyName("interactionTimeoutMs")] public int InteractionTimeoutMs { get; set; } = 30000;

    [JsonPropertyName("download")] public EngineDownloadOptions? Download { get; set; }
    [JsonPropertyName("java")] public EngineJavaOptions? Java { get; set; }
    [JsonPropertyName("memoryMB")] public int? MemoryMB { get; set; }
    [JsonPropertyName("window")] public EngineWindowOptions? Window { get; set; }
    [JsonPropertyName("login")] public EngineLoginOptions? Login { get; set; }

    [JsonPropertyName("serverIp")] public string? ServerIp { get; set; }
    [JsonPropertyName("extraGameArgs")] public List<string>? ExtraGameArgs { get; set; }
    [JsonPropertyName("curseForgeApiKey")] public string? CurseForgeApiKey { get; set; }
    [JsonPropertyName("oauthClientId")] public string? OAuthClientId { get; set; }
    [JsonPropertyName("noCrashReport")] public bool NoCrashReport { get; set; }
    [JsonPropertyName("requireEngine")] public EngineRequirement? RequireEngine { get; set; }
}

public sealed class EngineDownloadOptions
{
    [JsonPropertyName("source")] public string Source { get; set; } = "auto";              // auto|official|mirror
    [JsonPropertyName("versionListSource")] public string VersionListSource { get; set; } = "auto";
    [JsonPropertyName("mirrorBase")] public string? MirrorBase { get; set; }
    [JsonPropertyName("versionManifestUrl")] public string? VersionManifestUrl { get; set; }
    [JsonPropertyName("urlRewriteRules")] public List<EngineUrlRewriteRule>? UrlRewriteRules { get; set; }
    [JsonPropertyName("maxThreads")] public int? MaxThreads { get; set; }
}

public sealed class EngineUrlRewriteRule
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

public sealed class EngineJavaOptions
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("allowAutoDownload")] public bool AllowAutoDownload { get; set; } = true;
}

public sealed class EngineWindowOptions
{
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
}

public sealed class EngineLoginOptions
{
    [JsonPropertyName("type")] public string Type { get; set; } = "offline"; // offline|authlib|microsoft
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("skinType")] public int? SkinType { get; set; }
    [JsonPropertyName("skinName")] public string? SkinName { get; set; }
    [JsonPropertyName("oAuthRefreshToken")] public string? OAuthRefreshToken { get; set; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("profileJson")] public string? ProfileJson { get; set; }
}

public sealed class EngineRequirement
{
    [JsonPropertyName("minBuild")] public int MinBuild { get; set; }
    [JsonPropertyName("gitSha")] public string? GitSha { get; set; }
}

// ---------------------------------------------------------------------------
// 事件
// ---------------------------------------------------------------------------

/// <summary>progress.jsonl 的一行。未知字段保留在 Raw，便于前向兼容。</summary>
public sealed class EngineEvent
{
    public int V { get; init; }
    public int Seq { get; init; }
    public DateTimeOffset Ts { get; init; }
    public string TaskId { get; init; } = "";
    public string Type { get; init; } = "";
    public JsonElement Raw { get; init; }

    public string? Stage => GetString("stage");
    public string? Phase => GetString("phase");
    public double? Percent => GetDouble("percent");
    public double? StagePercent => GetDouble("stagePercent");
    public string? Level => GetString("level");
    public string? Text => GetString("text");
    public string? Code => GetString("code");
    public string? Message => GetString("message");
    public string? Detail => GetString("detail");
    public string? GameState => GetString("gameState");
    public int? Pid => GetInt("pid");
    public int? ExitCode => GetInt("exitCode");
    public int? FilesDone => GetInt("filesDone");
    public int? FilesTotal => GetInt("filesTotal");
    public double? SpeedBps => GetDouble("speedBps");
    public bool? Ok => GetBool("ok");
    public long? DurationMs => GetLong("durationMs");

    public EngineInfo? Engine =>
        Raw.TryGetProperty("engine", out var engine) ? engine.Deserialize<EngineInfo>() : null;

    public JsonElement? Data => Raw.TryGetProperty("data", out var data) ? data : null;

    public EngineInteraction? InteractionEvent =>
        Raw.TryGetProperty("interactionId", out _) ? new EngineInteraction
        {
            InteractionId = GetString("interactionId") ?? "",
            Kind = GetString("interactionKind") ?? "confirm",
            Title = GetString("title") ?? "",
            Text = GetString("text") ?? "",
            Buttons = Raw.TryGetProperty("buttons", out var buttons)
                ? buttons.EnumerateArray().Select(b => b.GetString() ?? "").ToArray()
                : Array.Empty<string>(),
            DefaultButton = GetInt("defaultButton") ?? 1
        } : null;

    private string? GetString(string name) =>
        Raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private int? GetInt(string name) =>
        Raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private long? GetLong(string name) =>
        Raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
    private double? GetDouble(string name) =>
        Raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    private bool? GetBool(string name) =>
        Raw.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}

public sealed class EngineInfo
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("engineVersion")] public string EngineVersion { get; set; } = "";
    [JsonPropertyName("gitSha")] public string GitSha { get; set; } = "";
    [JsonPropertyName("build")] public int Build { get; set; }
    [JsonPropertyName("appName")] public string AppName { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("dotnet")] public string Dotnet { get; set; } = "";
    [JsonPropertyName("silent")] public bool Silent { get; set; }
}

public sealed class EngineInteraction
{
    public string InteractionId { get; init; } = "";
    public string Kind { get; init; } = "confirm";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string[] Buttons { get; init; } = Array.Empty<string>();
    public int DefaultButton { get; init; } = 1;
}

/// <summary>任务最终结果（由 done 事件 + 进程退出码合成）。</summary>
public sealed class EngineTaskResult
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "OK";
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public JsonElement? Data { get; init; }
    public string? CrashReport { get; init; }
    public EngineInfo? Engine { get; init; }
    public bool Canceled => Code == "E_CANCELED";
}
