using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

public class LocalCacheTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"quotaflow-cache-test-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }
    }

    [Fact]
    public void Load_NoFileYet_ReturnsEmptyWithoutThrowing()
    {
        var cache = new LocalCache(_tempPath);

        var result = cache.Load();

        Assert.Empty(result);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSnapshotData()
    {
        var cache = new LocalCache(_tempPath);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "claude",
            DisplayName = "Claude",
            State = ProviderState.Available,
            QuotaWindows = [QuotaWindow.FromUtilization("five_hour", "5 小时", 10, DateTimeOffset.UtcNow.AddHours(2))],
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = "https://api.anthropic.com/api/oauth/usage",
        };

        cache.Save(new Dictionary<string, ProviderSnapshot> { ["claude"] = snapshot });
        var loaded = cache.Load();

        Assert.True(loaded.ContainsKey("claude"));
        Assert.Equal(90, loaded["claude"].QuotaWindows[0].RemainingPercent);
    }

    [Fact]
    public void CacheFile_NeverContainsSecretLookingContent()
    {
        // ProviderSnapshot 模型本身没有能装 token/key 的字段，这里做一次端到端的兜底检查：
        // 序列化后的文件里不应该出现常见的密钥前缀。
        var cache = new LocalCache(_tempPath);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "claude",
            DisplayName = "Claude",
            State = ProviderState.Available,
        };

        cache.Save(new Dictionary<string, ProviderSnapshot> { ["claude"] = snapshot });
        var raw = File.ReadAllText(_tempPath);

        Assert.DoesNotContain("sk-", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmptyWithoutThrowing()
    {
        File.WriteAllText(_tempPath, "{ not valid json");
        var cache = new LocalCache(_tempPath);

        var result = cache.Load();

        Assert.Empty(result);
    }

    [Fact]
    public void MarkAsCache_RecentSnapshot_KeepsOriginalState()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "claude",
            DisplayName = "Claude",
            State = ProviderState.Available,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        var marked = LocalCache.MarkAsCache(snapshot, TimeSpan.FromMinutes(30));

        Assert.True(marked.IsFromCache);
        Assert.Equal(ProviderState.Available, marked.State);
    }

    [Fact]
    public void MarkAsCache_OldSnapshot_DowngradesToStale()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "claude",
            DisplayName = "Claude",
            State = ProviderState.Available,
            LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

        var marked = LocalCache.MarkAsCache(snapshot, TimeSpan.FromMinutes(30));

        Assert.Equal(ProviderState.Stale, marked.State);
    }
}
