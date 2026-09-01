using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 读写非敏感应用设置（刷新间隔、主题、开机启动开关、接口地址覆盖等）。不涉及任何密钥——
/// 密钥始终只走 <see cref="SecureCredentialStore"/>（Windows 凭据管理器）。但设置仍以 DPAPI
/// 加密信封落地（文档硬性要求：配置不得明文内嵌/落盘），并用 schemaVersion 记录格式版本：
/// 旧版本明文/旧信封加载时自动迁移、保留旧文件快照（settings.json.vN.bak）后重写为当前版本。
/// </summary>
public sealed class AppSettingsStore
{
    /// <summary>
    /// 当前配置 schema 版本。只增不减；当"新增字段无法靠模型默认值平滑迁移"时才 +1 并补迁移逻辑
    /// （纯新增可空/带默认值字段不改变旧文件的可读性，无需 bump）。
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>本实现使用的加密方案标识；未来换算法时用于区分。</summary>
    private const string CipherName = "dpapi-user-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    /// <summary>设置文件所在目录，供设置页显示路径并提供"打开配置目录"入口。</summary>
    public string DataDirectory => Path.GetDirectoryName(_settingsPath) ?? string.Empty;

    /// <summary>
    /// 写盘失败时的通知回调（异常本身，不含设置内容）。
    ///
    /// 写失败必须静默降级——它曾经把整个应用在启动阶段带崩、连托盘图标都来不及创建。但"不崩"
    /// 不等于"不说"：实测遇到过窗口显示设置连续多次保存全部失败，界面照常显示新值、磁盘一个
    /// 字节没变，用户完全无从察觉，重启后才发现设置全丢。静默吞异常同时也违反项目规则
    /// "不隐藏 Error"。所以这里保留降级，但把失败暴露出去，由组合根写进 crash.log。
    /// </summary>
    public Action<Exception>? OnSaveFailed { get; set; }

    public AppSettingsStore(string? settingsPathOverride = null)
    {
        _settingsPath = settingsPathOverride ?? GetDefaultSettingsPath();
    }

    private static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "QuotaFlow");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        string text;
        try
        {
            text = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("schemaVersion", out _))
            {
                return LoadEnvelope(text);
            }
        }
        catch (JsonException)
        {
            // 既不是合法 JSON，也不可能是旧明文——按损坏处理，落回默认。
            return new AppSettings();
        }

        return MigrateLegacyPlaintext(text);
    }

    private AppSettings LoadEnvelope(string envelopeJson)
    {
        SettingsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SettingsEnvelope>(envelopeJson, JsonOptions);
        }
        catch (JsonException)
        {
            return new AppSettings();
        }

        if (envelope is null || !string.Equals(envelope.Cipher, CipherName, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(envelope.Payload))
        {
            // 未知加密方案或信封缺 payload：无法解密，落回默认（保留原文件供排查）。
            return new AppSettings();
        }

        string plainJson;
        try
        {
            var cipherBytes = Convert.FromBase64String(envelope.Payload);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            plainJson = Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // payload 不是合法 base64 或不是当前用户可解密的 DPAPI 密文：落回默认。
            return new AppSettings();
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(plainJson, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }

        if (envelope.SchemaVersion < CurrentSchemaVersion)
        {
            // 旧 schema：先备份旧文件快照，再以当前版本重写。
            TryBackup($"{_settingsPath}.v{envelope.SchemaVersion}.bak");
            SaveCore(settings);
        }

        MigrateAndPersistIfNeeded(settings);
        return settings;
    }

    /// <summary>
    /// v1.1.0 自定义平台多额度窗口改造：把 v1.0.5 及更早版本保存的单窗口配置（<c>valuePath</c> /
    /// <c>resetsAtPath</c> / <c>dataKind</c> / <c>currency</c>）迁移进 <c>quotaWindows[0]</c>。
    /// 不影响 API Key（本来就只在 Windows 凭据管理器里，迁移完全不碰）、不改变 Id（凭据键不变）、
    /// 不新增平台条目——只是把同一个平台的形状换成新格式。保存新格式前先备份旧信封快照，
    /// 万一迁移出问题可以找回迁移前的原始配置。
    /// </summary>
    private void MigrateAndPersistIfNeeded(AppSettings settings)
    {
        var migratedAny = false;
        foreach (var platform in settings.CustomPlatforms ?? [])
        {
            if (platform.QuotaWindows is { Count: > 0 })
            {
                continue; // 已经是新格式（或本来就没有旧字段可迁移）。
            }

            if (string.IsNullOrWhiteSpace(platform.ValuePath))
            {
                continue; // 没有旧数据可迁移（例如手改配置留下的空条目），保持空列表由 Provider 按未配置处理。
            }

            platform.QuotaWindows =
            [
                new CustomQuotaWindowSettings
                {
                    Id = "window-1",
                    Name = "额度",
                    DataKind = platform.DataKind ?? CustomDataKind.UtilizationPercent,
                    ValuePath = platform.ValuePath,
                    ResetsAtPath = platform.ResetsAtPath,
                    ResetTimeKind = CustomResetTimeKind.Auto,
                    Unit = platform.Currency,
                    SortOrder = 0,
                },
            ];
            migratedAny = true;
        }

        // 显示模式枚举值校验：配置损坏、或来自更新版本引入了这个版本不认识的新枚举成员时，
        // System.Text.Json 默认会把裸整数原样塞进枚举字段（不会因为不是已定义成员就报错），
        // 静默留着一个未定义的模式值可能让窗口进入无法预期的状态。文档 §11 要求安全回退到
        // TrayPopup，这里做校验。
        var windowDisplay = settings.WindowDisplay;
        var invalidMode = !Enum.IsDefined(windowDisplay.Mode);
        if (invalidMode)
        {
            windowDisplay.Mode = WindowPresentationMode.TrayPopup;
        }

        if (!migratedAny && !invalidMode)
        {
            return;
        }

        // 备份迁移前的信封快照（与 schema 版本备份用不同后缀，避免文件名撞车），再重写为新格式。
        TryBackup($"{_settingsPath}.custom-quota-migrate.bak");

        // 旧字段迁移完成后清空，避免下次加载重复识别成"待迁移"（QuotaWindows 已非空会被上面的
        // continue 挡住，这里清空纯粹是保持配置文件干净，不影响正确性）。
        foreach (var platform in settings.CustomPlatforms ?? [])
        {
            if (platform.QuotaWindows is { Count: > 0 })
            {
                platform.ValuePath = null;
                platform.ResetsAtPath = null;
                platform.DataKind = null;
                platform.Currency = null;
            }
        }

        SaveCore(settings);
    }

    /// <summary>
    /// 当前已部署的旧版明文 JSON（v1.0.0~1.0.2 都是这种格式，无 schemaVersion）。识别到就备份成
    /// settings.json.v0.bak 并升级为加密信封；新增字段缺失时由模型默认初始化器补默认值。
    /// </summary>
    private AppSettings MigrateLegacyPlaintext(string plainJson)
    {
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(plainJson, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }

        TryBackup($"{_settingsPath}.v0.bak");
        SaveCore(settings);
        MigrateAndPersistIfNeeded(settings);
        return settings;
    }

    /// <summary>写盘成功返回 true；失败时返回 false 并触发 <see cref="OnSaveFailed"/>，不抛异常。</summary>
    public bool Save(AppSettings settings) => SaveCore(settings);

    /// <summary>
    /// 读-改-写：从磁盘读出当前设置，只修改 <paramref name="mutate"/> 指定的部分，再整体写回。
    ///
    /// 设置有多条独立的写入路径（设置页、面板卡片顺序、已用/剩余切换、窗口显示模式与位置），
    /// 各自只关心自己那几个字段。谁要是拿着一份<b>启动时读进来、之后再没刷新过</b>的快照整份
    /// 写盘，就会把这期间别的路径写进去的改动一起抹掉。
    ///
    /// 实测发生过两次同类事故：一次是设置页保存把整个 WindowDisplay 重置
    /// （见 <see cref="SettingsSnapshotBuilder"/>）；一次是切换显示模式后再点卡片 ▲/▼，
    /// 模式改动被面板持有的陈旧快照覆盖回去。
    ///
    /// 所以凡是"只想改几个字段"的调用方都应该走这里，而不是自己 Load/Save 或直接
    /// <see cref="Save"/> 一份记不清有多新的对象。
    /// </summary>
    public bool Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var settings = Load();
        mutate(settings);
        return SaveCore(settings);
    }

    private bool SaveCore(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);

            var envelope = new SettingsEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                Cipher = CipherName,
                Payload = Convert.ToBase64String(encrypted),
            };

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(envelope, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            // 把失败暴露给调用方（写日志/提示用户），但不向上抛——见 OnSaveFailed 的说明。
            try
            {
                OnSaveFailed?.Invoke(ex);
            }
            catch (Exception)
            {
                // 通知回调自身出错不能反过来影响保存流程。
            }

            return false;
            // 写失败静默降级：与原实现一致，绝不让保存设置拖垮主流程。ArgumentException 补充于
            // 实测事故——某些字段（如显示模式的窗口坐标）一旦意外携带 NaN/Infinity，
            // System.Text.Json 会在序列化阶段直接抛出，之前没接住，导致整个应用在启动时
            // （RestoreLastModeAsync 触发的一次 Persist）崩掉、连托盘图标都来不及创建。
            // 产生 NaN/Infinity 的根因已经在 MonitorService/WindowPlacementCalculator 里堵上，
            // 这里是防止同类问题再次把整个应用带崩的最后一道防线。
        }
    }

    private void TryBackup(string backupPath)
    {
        try
        {
            File.Copy(_settingsPath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>磁盘上的信封结构。字段名按 Web 默认序列化为 camelCase（schemaVersion/cipher/payload）。</summary>
    private sealed class SettingsEnvelope
    {
        public int SchemaVersion { get; set; }
        public string? Cipher { get; set; }
        public string? Payload { get; set; }
    }
}
