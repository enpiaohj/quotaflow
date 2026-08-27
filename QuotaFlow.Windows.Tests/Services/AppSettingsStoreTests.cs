using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

// 设置文件用 DPAPI（当前 Windows 用户）加密信封持久化；测试进程跑在当前用户下，可正常往返。
// 每个测试用独立临时目录，互不干扰。
public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly AppSettingsStore _store;

    public AppSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quotaflow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        _store = new AppSettingsStore(_path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = _store.Load();

        Assert.Equal(5, settings.AutoRefreshIntervalMinutes);
        Assert.True(settings.RefreshOnStartup);
        Assert.Equal(ThemeMode.System, settings.Theme);
        Assert.Equal(MiniMaxRegion.China, settings.MiniMaxRegion);
        Assert.Null(settings.ClaudeEndpointOverride);
        Assert.Null(settings.DeepSeekEndpointOverride);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllSettings()
    {
        var original = new AppSettings
        {
            AutoRefreshIntervalMinutes = 15,
            RefreshOnStartup = false,
            StartWithWindows = true,
            Theme = ThemeMode.Dark,
            MiniMaxRegion = MiniMaxRegion.International,
            ShowUnknownWindows = true,
            ClaudeEndpointOverride = "https://claude.example/usage",
            CodexEndpointOverride = "https://codex.example/usage",
            MiniMaxEndpointOverride = "https://minimax.example/usage",
            DeepSeekEndpointOverride = "https://deepseek.example/balance",
            PlatformOrder = ["deepseek", "claude", "minimax", "codex"],
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal(original.AutoRefreshIntervalMinutes, loaded.AutoRefreshIntervalMinutes);
        Assert.Equal(original.RefreshOnStartup, loaded.RefreshOnStartup);
        Assert.Equal(original.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.MiniMaxRegion, loaded.MiniMaxRegion);
        Assert.Equal(original.ShowUnknownWindows, loaded.ShowUnknownWindows);
        Assert.Equal(original.ClaudeEndpointOverride, loaded.ClaudeEndpointOverride);
        Assert.Equal(original.CodexEndpointOverride, loaded.CodexEndpointOverride);
        Assert.Equal(original.MiniMaxEndpointOverride, loaded.MiniMaxEndpointOverride);
        Assert.Equal(original.DeepSeekEndpointOverride, loaded.DeepSeekEndpointOverride);
        Assert.Equal(["deepseek", "claude", "minimax", "codex"], loaded.PlatformOrder);
    }

    [Fact]
    public void Save_WritesEncryptedEnvelope_NotPlaintext()
    {
        _store.Save(new AppSettings
        {
            Theme = ThemeMode.Dark,
            ClaudeEndpointOverride = "https://secret-override.example/usage",
            CustomPlatforms =
            [
                new CustomPlatformSettings { Id = "custom-1", Name = "OpenCode", Endpoint = "https://opencode.example", ValuePath = "data.quota" },
            ],
        });

        var text = File.ReadAllText(_path);

        // 信封元信息可见，但明文内容（含自定义平台定义）绝不能落盘。
        Assert.Contains("schemaVersion", text);
        Assert.Contains("dpapi-user-v1", text);
        Assert.Contains("\"payload\"", text);
        Assert.DoesNotContain("autoRefreshIntervalMinutes", text);
        Assert.DoesNotContain("refreshOnStartup", text);
        Assert.DoesNotContain("secret-override.example", text);
        Assert.DoesNotContain("theme", text);
        Assert.DoesNotContain("customPlatforms", text);
        Assert.DoesNotContain("opencode.example", text);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCustomPlatforms()
    {
        var original = new AppSettings
        {
            CustomPlatforms =
            [
                new CustomPlatformSettings
                {
                    Id = "custom-1",
                    Name = "OpenCode",
                    Endpoint = "https://opencode.example/api/usage",
                    AuthKind = CustomAuthKind.CustomHeader,
                    HeaderName = "X-API-Key",
                    DataKind = CustomDataKind.Balance,
                    ValuePath = "data.balance",
                    ResetsAtPath = "data.updated_at",
                    Currency = "USD",
                },
                new CustomPlatformSettings
                {
                    Id = "custom-2",
                    Name = "GO",
                    Endpoint = "https://go.example/usage",
                    AuthKind = CustomAuthKind.None,
                    DataKind = CustomDataKind.RemainingPercent,
                    ValuePath = "data.quota_left",
                },
            ],
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal(2, loaded.CustomPlatforms!.Count);

        var first = loaded.CustomPlatforms[0];
        Assert.Equal("custom-1", first.Id);
        Assert.Equal("OpenCode", first.Name);
        Assert.Equal("https://opencode.example/api/usage", first.Endpoint);
        Assert.Equal(CustomAuthKind.CustomHeader, first.AuthKind);
        Assert.Equal("X-API-Key", first.HeaderName);
        Assert.Equal(CustomDataKind.Balance, first.DataKind);
        Assert.Equal("data.balance", first.ValuePath);
        Assert.Equal("data.updated_at", first.ResetsAtPath);
        Assert.Equal("USD", first.Currency);

        var second = loaded.CustomPlatforms[1];
        Assert.Equal("custom-2", second.Id);
        Assert.Equal("GO", second.Name);
        Assert.Equal(CustomAuthKind.None, second.AuthKind);
        Assert.Equal(CustomDataKind.RemainingPercent, second.DataKind);
    }

    [Fact]
    public void Load_LegacyPlaintext_MigratesAndBacksUp()
    {
        // v1.0.0~v1.0.2 的明文格式：无 schemaVersion。
        File.WriteAllText(_path, """
            { "autoRefreshIntervalMinutes": 10, "refreshOnStartup": false, "startWithWindows": true, "theme": 2, "miniMaxRegion": 1 }
            """);

        var settings = _store.Load();

        Assert.Equal(10, settings.AutoRefreshIntervalMinutes);
        Assert.False(settings.RefreshOnStartup);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(ThemeMode.Dark, settings.Theme);
        Assert.Equal(MiniMaxRegion.International, settings.MiniMaxRegion);
        Assert.Null(settings.ClaudeEndpointOverride);

        // 旧明文被备份成快照。
        var backup = Path.Combine(_dir, "settings.json.v0.bak");
        Assert.True(File.Exists(backup));
        Assert.Contains("autoRefreshIntervalMinutes", File.ReadAllText(backup));

        // 主文件升级为加密信封，不再是明文。
        var text = File.ReadAllText(_path);
        Assert.Contains("schemaVersion", text);
        Assert.DoesNotContain("autoRefreshIntervalMinutes", text);

        // 再次加载是幂等的：不会重复生成 .v0 之外的备份。
        _store.Load();
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void Load_LegacyPlaintext_MissingFields_GetDefaults()
    {
        File.WriteAllText(_path, """{ "autoRefreshIntervalMinutes": 30 }""");

        var settings = _store.Load();

        Assert.Equal(30, settings.AutoRefreshIntervalMinutes);
        Assert.True(settings.RefreshOnStartup);
        Assert.Equal(ThemeMode.System, settings.Theme);
        Assert.Null(settings.DeepSeekEndpointOverride);
    }

    [Fact]
    public void Load_OlderEnvelopeVersion_MigratesAndBacksUp()
    {
        var inner = """{ "autoRefreshIntervalMinutes": 20, "miniMaxRegion": 1 }""";
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(inner), null, DataProtectionScope.CurrentUser);
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            schemaVersion = 0,
            cipher = "dpapi-user-v1",
            payload = Convert.ToBase64String(encrypted),
        }));

        var settings = _store.Load();

        Assert.Equal(20, settings.AutoRefreshIntervalMinutes);
        Assert.Equal(MiniMaxRegion.International, settings.MiniMaxRegion);

        // 旧信封被备份，主文件被重写为当前 schema 版本。
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.v0.bak")));
        var text = File.ReadAllText(_path);
        Assert.Contains("\"schemaVersion\": 1", text);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults_DoesNotThrow()
    {
        File.WriteAllText(_path, "this is not json {{{");

        var settings = _store.Load();

        Assert.Equal(5, settings.AutoRefreshIntervalMinutes);
        Assert.Null(settings.ClaudeEndpointOverride);
    }

    [Fact]
    public void Load_UndecryptablePayload_ReturnsDefaults()
    {
        // payload 是合法 base64 但不是本用户可解的 DPAPI 密文。
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            cipher = "dpapi-user-v1",
            payload = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
        }));

        var settings = _store.Load();

        Assert.Equal(5, settings.AutoRefreshIntervalMinutes);
    }

    [Fact]
    public void Load_UnknownCipher_ReturnsDefaults()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            cipher = "aes-gcm-v1",
            payload = "AAAA",
        }));

        var settings = _store.Load();

        Assert.Equal(5, settings.AutoRefreshIntervalMinutes);
    }
}
