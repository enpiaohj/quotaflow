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
        Assert.True(settings.HotkeyEnabled);
        Assert.Equal(HotkeyModifiers.Alt, settings.HotkeyModifiers);
        Assert.Equal("Z", settings.HotkeyKey);
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
            HotkeyEnabled = false,
            HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyKey = "Q",
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
        Assert.Equal(original.HotkeyEnabled, loaded.HotkeyEnabled);
        Assert.Equal(original.HotkeyModifiers, loaded.HotkeyModifiers);
        Assert.Equal(original.HotkeyKey, loaded.HotkeyKey);
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
                new CustomPlatformSettings
                {
                    Id = "custom-1",
                    Name = "OpenCode GO",
                    Endpoint = "https://opencode.example",
                    QuotaWindows = [new CustomQuotaWindowSettings { Id = "window-1", Name = "额度", ValuePath = "data.quota" }],
                },
            ],
        });

        var text = File.ReadAllText(_path);

        // 信封元信息可见，但明文内容（含自定义平台定义/窗口配置）绝不能落盘。
        Assert.Contains("schemaVersion", text);
        Assert.Contains("dpapi-user-v1", text);
        Assert.Contains("\"payload\"", text);
        Assert.DoesNotContain("autoRefreshIntervalMinutes", text);
        Assert.DoesNotContain("refreshOnStartup", text);
        Assert.DoesNotContain("secret-override.example", text);
        Assert.DoesNotContain("theme", text);
        Assert.DoesNotContain("customPlatforms", text);
        Assert.DoesNotContain("quotaWindows", text);
        Assert.DoesNotContain("opencode.example", text);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCustomPlatforms_NewMultiWindowFormat()
    {
        var original = new AppSettings
        {
            CustomPlatforms =
            [
                new CustomPlatformSettings
                {
                    Id = "custom-1",
                    Name = "OpenCode GO",
                    Endpoint = "https://opencode.example/api/usage",
                    AuthKind = CustomAuthKind.CustomHeader,
                    HeaderName = "X-API-Key",
                    QuotaWindows =
                    [
                        new CustomQuotaWindowSettings
                        {
                            Id = "window-1",
                            Name = "5 小时",
                            DataKind = CustomDataKind.UtilizationPercent,
                            ValuePath = "usage.rolling.percent",
                            ResetsAtPath = "usage.rolling.resetsAt",
                            ResetTimeKind = CustomResetTimeKind.Absolute,
                            SortOrder = 0,
                        },
                        new CustomQuotaWindowSettings
                        {
                            Id = "window-2",
                            Name = "每周",
                            DataKind = CustomDataKind.RemainingValue,
                            ValuePath = "usage.weekly.remaining",
                            LimitPath = "usage.weekly.limit",
                            Unit = "次",
                            SortOrder = 1,
                        },
                    ],
                },
                new CustomPlatformSettings
                {
                    Id = "custom-2",
                    Name = "GO",
                    Endpoint = "https://go.example/usage",
                    AuthKind = CustomAuthKind.None,
                    QuotaWindows =
                    [
                        new CustomQuotaWindowSettings
                        {
                            Id = "window-1", Name = "余额", DataKind = CustomDataKind.Balance,
                            ValuePath = "data.balance", Unit = "USD", SortOrder = 0,
                        },
                    ],
                },
            ],
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal(2, loaded.CustomPlatforms!.Count);

        var first = loaded.CustomPlatforms[0];
        Assert.Equal("custom-1", first.Id);
        Assert.Equal("OpenCode GO", first.Name);
        Assert.Equal("https://opencode.example/api/usage", first.Endpoint);
        Assert.Equal(CustomAuthKind.CustomHeader, first.AuthKind);
        Assert.Equal("X-API-Key", first.HeaderName);
        Assert.Equal(2, first.QuotaWindows.Count);
        Assert.Equal("window-1", first.QuotaWindows[0].Id);
        Assert.Equal("5 小时", first.QuotaWindows[0].Name);
        Assert.Equal(CustomDataKind.UtilizationPercent, first.QuotaWindows[0].DataKind);
        Assert.Equal("usage.rolling.percent", first.QuotaWindows[0].ValuePath);
        Assert.Equal("usage.rolling.resetsAt", first.QuotaWindows[0].ResetsAtPath);
        Assert.Equal(CustomResetTimeKind.Absolute, first.QuotaWindows[0].ResetTimeKind);
        Assert.Equal(CustomDataKind.RemainingValue, first.QuotaWindows[1].DataKind);
        Assert.Equal("usage.weekly.limit", first.QuotaWindows[1].LimitPath);
        Assert.Equal("次", first.QuotaWindows[1].Unit);

        var second = loaded.CustomPlatforms[1];
        Assert.Equal("custom-2", second.Id);
        Assert.Single(second.QuotaWindows);
        Assert.Equal(CustomDataKind.Balance, second.QuotaWindows[0].DataKind);
        Assert.Equal("USD", second.QuotaWindows[0].Unit);
    }

    [Fact]
    public void Load_LegacyCustomPlatform_MigratesToQuotaWindows_PreservesIdAndBacksUp()
    {
        // 模拟 v1.0.5 保存下来的单窗口旧格式：QuotaWindows 为空，旧字段有值。
        // Save() 本身不做迁移，原样落盘旧格式（相当于"这是 v1.0.5 写下的文件"）。
        var legacy = new AppSettings
        {
            CustomPlatforms =
            [
                new CustomPlatformSettings
                {
                    Id = "custom-7",
                    Name = "OpenCode",
                    Endpoint = "https://opencode.example/api/usage",
                    AuthKind = CustomAuthKind.BearerKey,
                    DataKind = CustomDataKind.RemainingPercent,
                    ValuePath = "data.quota_left",
                    ResetsAtPath = "data.resets_at",
                    Currency = "USD",
                },
            ],
        };
        _store.Save(legacy);

        var migrated = _store.Load(); // 加载时就地迁移并重写

        var platform = Assert.Single(migrated.CustomPlatforms!);
        Assert.Equal("custom-7", platform.Id); // Id 不变 -> 凭据键 custom:custom-7:ApiKey 不变 -> 不丢 API Key
        Assert.Equal("OpenCode", platform.Name);
        Assert.Equal("https://opencode.example/api/usage", platform.Endpoint);
        Assert.Equal(CustomAuthKind.BearerKey, platform.AuthKind);

        var window = Assert.Single(platform.QuotaWindows);
        Assert.Equal(CustomDataKind.RemainingPercent, window.DataKind);
        Assert.Equal("data.quota_left", window.ValuePath);
        Assert.Equal("data.resets_at", window.ResetsAtPath);
        Assert.Equal("USD", window.Unit); // 旧 Currency 迁移进新的 Unit
        Assert.Equal(CustomResetTimeKind.Auto, window.ResetTimeKind);

        // 旧字段迁移后被清空，避免下次加载重复识别成"待迁移"。
        Assert.Null(platform.ValuePath);
        Assert.Null(platform.DataKind);
        Assert.Null(platform.ResetsAtPath);
        Assert.Null(platform.Currency);

        // 迁移前的信封快照被保留，供排查（"保存新格式前提供安全回退"）。
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.custom-quota-migrate.bak")));

        // 迁移后主文件已是新格式：再次加载不产生重复迁移，也不产生重复平台条目。
        var reloaded = _store.Load();
        Assert.Single(reloaded.CustomPlatforms!);
        Assert.Single(reloaded.CustomPlatforms![0].QuotaWindows);
    }

    [Fact]
    public void Load_LegacyCustomPlatformWithoutValuePath_StaysEmptyQuotaWindows_DoesNotCrash()
    {
        // 手改配置留下的空条目（没有 ValuePath 可迁移）：不应该崩溃，也不应该凭空生成一个窗口。
        var legacy = new AppSettings
        {
            CustomPlatforms = [new CustomPlatformSettings { Id = "custom-1", Name = "Broken", Endpoint = "https://x.example" }],
        };
        _store.Save(legacy);

        var loaded = _store.Load();

        Assert.Empty(Assert.Single(loaded.CustomPlatforms!).QuotaWindows);
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

    [Fact]
    public void Load_UnknownWindowPresentationMode_FallsBackToTrayPopup()
    {
        // 模拟配置文件里存了一个当前版本不认识的显示模式枚举值（未来版本新增的模式，
        // 或者文件被手改坏）——不能让窗口带着一个未定义的模式启动。
        var legacy = new AppSettings
        {
            WindowDisplay = new WindowDisplaySettings { Mode = (WindowPresentationMode)99 },
        };
        _store.Save(legacy);

        var loaded = _store.Load();

        Assert.Equal(WindowPresentationMode.TrayPopup, loaded.WindowDisplay.Mode);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsHiddenPlatforms()
    {
        _store.Save(new AppSettings { HiddenPlatforms = ["minimax", "alibaba-tokenplan"] });

        Assert.Equal(["minimax", "alibaba-tokenplan"], _store.Load().HiddenPlatforms);
    }

    [Fact]
    public void Load_MissingHiddenPlatforms_DefaultsToNullMeaningAllVisible()
    {
        // 老配置文件里没有这个字段，必须表示"全部显示"，绝不能反过来把所有平台都藏掉。
        _store.Save(new AppSettings());

        Assert.Null(_store.Load().HiddenPlatforms);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsWindowDisplaySettings()
    {
        var original = new AppSettings
        {
            WindowDisplay = new WindowDisplaySettings
            {
                Mode = WindowPresentationMode.DesktopPanel,
                IsAlwaysOnTop = false,
                IsPositionLocked = true,
                IsCompactLayout = true,
                Opacity = 0.85,
                Material = WindowMaterial.Acrylic,
                SnapToEdges = false,
                RestoreLastModeOnStartup = true,
                EnhanceReadabilityOnHover = false,
                FloatingPlacement = new SavedWindowPlacement
                {
                    MonitorDeviceName = "\\\\.\\DISPLAY1",
                    LeftDip = 100,
                    TopDip = 200,
                    WidthDip = 420,
                    HeightDip = 560,
                    SavedDpiX = 144,
                    SavedDpiY = 144,
                    LastUpdatedAt = DateTimeOffset.Parse("2026-08-28T10:00:00Z"),
                },
                DesktopPlacement = new SavedWindowPlacement
                {
                    MonitorDeviceName = "\\\\.\\DISPLAY2",
                    LeftDip = 50,
                    TopDip = 60,
                    WidthDip = 320,
                    HeightDip = 180,
                },
            },
        };

        _store.Save(original);
        var loaded = _store.Load();

        var w = loaded.WindowDisplay;
        Assert.Equal(WindowPresentationMode.DesktopPanel, w.Mode);
        Assert.False(w.IsAlwaysOnTop);
        Assert.True(w.IsPositionLocked);
        Assert.True(w.IsCompactLayout);
        Assert.Equal(0.85, w.Opacity);
        Assert.Equal(WindowMaterial.Acrylic, w.Material);
        Assert.False(w.SnapToEdges);
        Assert.True(w.RestoreLastModeOnStartup);
        Assert.False(w.EnhanceReadabilityOnHover);
        Assert.NotNull(w.FloatingPlacement);
        Assert.Equal("\\\\.\\DISPLAY1", w.FloatingPlacement!.MonitorDeviceName);
        Assert.Equal(100, w.FloatingPlacement.LeftDip);
        Assert.Equal(144, w.FloatingPlacement.SavedDpiX);
        Assert.NotNull(w.DesktopPlacement);
        Assert.Equal("\\\\.\\DISPLAY2", w.DesktopPlacement!.MonitorDeviceName);
    }

    [Fact]
    public void Load_MissingFile_WindowDisplayDefaultsAreSensible()
    {
        var settings = _store.Load();

        Assert.Equal(WindowPresentationMode.TrayPopup, settings.WindowDisplay.Mode);
        Assert.True(settings.WindowDisplay.IsAlwaysOnTop);
        Assert.False(settings.WindowDisplay.IsPositionLocked);
        Assert.False(settings.WindowDisplay.IsCompactLayout);
        Assert.Equal(1.0, settings.WindowDisplay.Opacity);
        Assert.Equal(WindowMaterial.System, settings.WindowDisplay.Material);
        Assert.Null(settings.WindowDisplay.FloatingPlacement);
        Assert.Null(settings.WindowDisplay.DesktopPlacement);
    }

    [Fact]
    public void Save_PlacementContainsNonFiniteNumbers_DoesNotThrow()
    {
        // 实测事故：远程桌面等虚拟显示驱动偶尔报告 0 DPI，上游按 0 做除法产出的
        // Infinity/NaN 一路带进 SavedWindowPlacement；System.Text.Json 默认拒绝序列化非有限
        // 浮点数，会抛 ArgumentException——之前 SaveCore 的 catch 没接住这一种，导致
        // "保存设置"这个不该致命的操作直接把调用方（应用启动路径）整个带崩。
        // 产生 NaN/Infinity 的根因已经在别处堵上，这里验证 Save 本身对这类输入是安全的，
        // 不会向上抛异常——这条防线不该依赖调用方永远不出错才成立。
        var settings = new AppSettings
        {
            WindowDisplay = new WindowDisplaySettings
            {
                Mode = WindowPresentationMode.Floating,
                FloatingPlacement = new SavedWindowPlacement
                {
                    MonitorDeviceName = "\\\\.\\DISPLAY-BROKEN",
                    LeftDip = double.PositiveInfinity,
                    TopDip = double.NaN,
                    WidthDip = 320,
                    HeightDip = 200,
                },
            },
        };

        var exception = Record.Exception(() => _store.Save(settings));

        Assert.Null(exception);
    }
}
