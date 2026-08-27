using System.Text.Json;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 本地展示缓存：只保存 <see cref="ProviderSnapshot"/>（百分比、重置时间、更新时间等展示数据），
/// 绝不保存 OAuth Token 或 API Key——ProviderSnapshot 这个类型本身也没有任何字段能装下密钥
/// （文档 §6 / §7 的硬性要求）。
///
/// 用途：应用启动时先把上一次的快照读出来渲染（100ms 内先展示旧数据），
/// 再在后台发起真实刷新，避免冷启动出现"全空白"。
/// </summary>
public sealed class LocalCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _cachePath;

    public LocalCache(string? cachePathOverride = null)
    {
        _cachePath = cachePathOverride ?? GetDefaultCachePath();
    }

    private static string GetDefaultCachePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "QuotaFlow");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "cache.json");
    }

    /// <summary>读取上一次持久化的快照；文件不存在或已损坏时返回空字典，不抛异常、不阻塞启动。</summary>
    public IReadOnlyDictionary<string, ProviderSnapshot> Load()
    {
        if (!File.Exists(_cachePath))
        {
            return new Dictionary<string, ProviderSnapshot>();
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, ProviderSnapshot>>(json, JsonOptions);
            return data ?? new Dictionary<string, ProviderSnapshot>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ProviderSnapshot>();
        }
    }

    /// <summary>持久化最新一轮成功/失败快照。写入失败不影响主流程（吞掉异常，仅退化为"本次不缓存"）。</summary>
    public void Save(IReadOnlyDictionary<string, ProviderSnapshot> snapshots)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshots, JsonOptions);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写入失败不是致命错误，忽略即可。
        }
    }

    /// <summary>清除缓存文件（对应设置页"清除缓存"）。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 把一份快照标记为"来自缓存"，并在必要时把 State 降级为 Stale——
    /// 当缓存已经超过新鲜度窗口（默认与刷新间隔挂钩）时提醒用户数据可能过期。
    /// </summary>
    public static ProviderSnapshot MarkAsCache(ProviderSnapshot snapshot, TimeSpan staleAfter, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var isStale = snapshot.LastUpdatedAt is null || reference - snapshot.LastUpdatedAt.Value > staleAfter;

        return snapshot with
        {
            IsFromCache = true,
            State = isStale ? ProviderState.Stale : snapshot.State,
        };
    }
}
