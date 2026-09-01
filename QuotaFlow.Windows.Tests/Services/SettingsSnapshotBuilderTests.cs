using System.Reflection;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="SettingsSnapshotBuilder"/> 的测试。
///
/// 这组测试守的是一条曾经被破坏、并造成真实数据丢失的不变量：
/// <b>保存设置时，设置页不拥有的字段必须原样保留。</b>
///
/// 事故经过：早期实现用 <c>new AppSettings { ... }</c> 从零构造要写盘的对象，凡是没在
/// 初始化器里列出的字段都取默认值并被整体写盘。<c>WindowDisplay</c> 恰好没列出来，于是
/// 点一次「保存设置」就把显示模式、不透明度、置顶、锁定位置、紧凑布局、材质、边缘吸附
/// 以及各显示器分别记住的窗口位置全部重置回默认值。当时这段逻辑在 App 工程里，测试工程
/// 只引用 Core，写不了单测，只能靠 UI 自动化加窗口截图才发现。
/// </summary>
public class SettingsSnapshotBuilderTests
{
    /// <summary>一份所有字段都被改成非默认值的基线，用来放大"字段被悄悄重置"的现象。</summary>
    private static AppSettings FullyPopulatedBaseline() => new()
    {
        AutoRefreshIntervalMinutes = 30,
        RefreshOnStartup = false,
        StartWithWindows = true,
        Theme = ThemeMode.Dark,
        MiniMaxRegion = MiniMaxRegion.International,
        ShowUnknownWindows = true,
        ClockDisplayFormat = ClockDisplayFormat.TimeOnly,
        ProxyMode = ProxyMode.Custom,
        ProxyAddress = "http://10.0.0.1:8888",
        ClaudeEndpointOverride = "https://old-claude.test",
        CodexEndpointOverride = "https://old-codex.test",
        MiniMaxEndpointOverride = "https://old-minimax.test",
        DeepSeekEndpointOverride = "https://old-deepseek.test",
        HotkeyEnabled = false,
        HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
        HotkeyKey = "Q",
        HiddenPlatforms = ["codex"],
        CustomPlatforms = [ValidPlatform("custom-9", "旧平台")],

        // ↓ 以下三项由主面板即时持久化，不属于设置页，保存时必须原样保留
        PlatformOrder = ["deepseek", "claude"],
        QuotaDisplaySemantic = QuotaDisplaySemantic.Used,
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
                MonitorDeviceName = @"\\.\DISPLAY1",
                LeftDip = 100,
                TopDip = 200,
                WidthDip = 420,
                HeightDip = 560,
            },
        },
    };

    private static CustomPlatformSettings ValidPlatform(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Endpoint = "https://example.test/quota",
        QuotaWindows =
        [
            new CustomQuotaWindowSettings { Name = "周额度", ValuePath = "data.percent", DataKind = CustomDataKind.RemainingPercent },
        ],
    };

    private static SettingsPageEdits SampleEdits() => new()
    {
        AutoRefreshIntervalMinutes = 5,
        RefreshOnStartup = true,
        Theme = ThemeMode.Light,
        HotkeyKey = "Z",
        CustomPlatforms = [ValidPlatform("custom-1", "新平台")],
    };

    // ---- 核心不变量 ----

    [Fact]
    public void Build_PreservesWindowDisplay_TheFieldThatCausedRealDataLoss()
    {
        var baseline = FullyPopulatedBaseline();
        var original = baseline.WindowDisplay;

        var result = SettingsSnapshotBuilder.Build(baseline, SampleEdits());

        var w = result.Settings.WindowDisplay;
        Assert.Same(original, w);
        Assert.Equal(WindowPresentationMode.DesktopPanel, w.Mode);
        Assert.True(w.IsCompactLayout);
        Assert.Equal(0.85, w.Opacity);
        Assert.Equal(WindowMaterial.Acrylic, w.Material);
        Assert.NotNull(w.FloatingPlacement);
        Assert.Equal(420, w.FloatingPlacement!.WidthDip);
    }

    [Fact]
    public void Build_PreservesPlatformOrderAndDisplaySemantic()
    {
        // 这两项由面板顶部的 ▲/▼ 与「剩余/已用」切换即时持久化，同样不属于设置页。
        var result = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), SampleEdits());

        Assert.Equal(["deepseek", "claude"], result.Settings.PlatformOrder);
        Assert.Equal(QuotaDisplaySemantic.Used, result.Settings.QuotaDisplaySemantic);
    }

    /// <summary>
    /// 反射校验：<see cref="AppSettings"/> 的每个字段，要么由设置页写入（等于 edits 的值），
    /// 要么在"面板拥有"名单里（等于基线的值）。两者都不满足就说明这个字段被悄悄改动了。
    ///
    /// <b>这条测试是为将来准备的</b>：给 AppSettings 新增字段而忘记在
    /// <see cref="SettingsSnapshotBuilder"/> 里归类时，它会直接失败——正是当初漏掉
    /// WindowDisplay 的那个场景。
    /// </summary>
    [Fact]
    public void Build_EveryAppSettingsField_IsEitherWrittenByPageOrPreservedFromBaseline()
    {
        var baseline = FullyPopulatedBaseline();
        var untouched = FullyPopulatedBaseline(); // 与 baseline 同值的独立副本，用于比对
        var edits = SampleEdits();

        var result = SettingsSnapshotBuilder.Build(baseline, edits);

        var panelOwned = SettingsSnapshotBuilder.PanelOwnedFieldNames.ToHashSet(StringComparer.Ordinal);
        var editableNames = typeof(SettingsPageEdits)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = new List<string>();
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (panelOwned.Contains(prop.Name))
            {
                // 面板拥有：必须与基线一致
                var actual = prop.GetValue(result.Settings);
                var expected = prop.GetValue(untouched);
                Assert.True(
                    ValuesLookEqual(actual, expected),
                    $"字段 {prop.Name} 属于面板即时持久化范围，保存设置时必须原样保留，但它被改动了");
                continue;
            }

            if (editableNames.Contains(prop.Name))
            {
                continue; // 设置页拥有：由其它测试逐项验证
            }

            unclassified.Add(prop.Name);
        }

        Assert.True(
            unclassified.Count == 0,
            $"AppSettings 新增了未归类的字段：{string.Join("、", unclassified)}。" +
            "请判断它属于设置页（加进 SettingsPageEdits 并在 SettingsSnapshotBuilder.Build 中赋值）" +
            "还是属于面板即时持久化（加进 SettingsSnapshotBuilder.PanelOwnedFieldNames）。" +
            "漏掉归类曾导致保存设置时窗口显示设置被整片重置。");
    }

    /// <summary>
    /// 结构化比较两个字段值。
    ///
    /// 用 JSON 序列化而不是 <c>Equals</c>：<see cref="WindowDisplaySettings"/> 这类嵌套设置
    /// 对象是普通类、没有值相等语义，<c>Equals</c> 会退化成引用比较，两个内容相同的实例也判不等。
    /// 而设置本来就以 JSON 形式落盘，序列化结果相同即等价于"写进磁盘的内容没变"。
    /// </summary>
    private static bool ValuesLookEqual(object? a, object? b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    // ---- 设置页拥有的字段确实被写入 ----

    [Fact]
    public void Build_WritesPageOwnedFields()
    {
        var edits = SampleEdits();
        edits.AutoRefreshIntervalMinutes = 15;
        edits.Theme = ThemeMode.Dark;
        edits.ProxyMode = ProxyMode.Direct;
        edits.HotkeyKey = "K";

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Equal(15, s.AutoRefreshIntervalMinutes);
        Assert.Equal(ThemeMode.Dark, s.Theme);
        Assert.Equal(ProxyMode.Direct, s.ProxyMode);
        Assert.Equal("K", s.HotkeyKey);
    }

    [Fact]
    public void Build_BlankEndpointOverrides_BecomeNullNotEmptyString()
    {
        // 空串与 null 在"是否使用内置默认地址"上语义相同，统一成 null 避免磁盘上出现空字符串。
        var edits = SampleEdits();
        edits.ClaudeEndpointOverride = "   ";
        edits.CodexEndpointOverride = string.Empty;
        edits.ProxyAddress = "  ";

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Null(s.ClaudeEndpointOverride);
        Assert.Null(s.CodexEndpointOverride);
        Assert.Null(s.ProxyAddress);
    }

    [Fact]
    public void Build_EndpointOverride_IsTrimmed()
    {
        var edits = SampleEdits();
        edits.MiniMaxEndpointOverride = "  https://custom.test/api  ";

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Equal("https://custom.test/api", s.MiniMaxEndpointOverride);
    }

    [Fact]
    public void Build_NoHiddenPlatforms_StoresNullSoOldConfigsNeedNoMigration()
    {
        var edits = SampleEdits();
        edits.HiddenPlatforms = [];

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Null(s.HiddenPlatforms);
    }

    [Fact]
    public void Build_HiddenPlatforms_AreWritten()
    {
        var edits = SampleEdits();
        edits.HiddenPlatforms = ["minimax", "alibaba-tokenplan"];

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Equal(["minimax", "alibaba-tokenplan"], s.HiddenPlatforms);
    }

    // ---- 自定义平台校验 ----

    [Fact]
    public void Build_InvalidCustomPlatform_IsSkippedAndReportedByName()
    {
        // 静默丢弃会让用户以为保存成功了，必须点名。
        var edits = SampleEdits();
        var broken = ValidPlatform("custom-2", "缺地址的平台");
        broken.Endpoint = string.Empty;
        edits.CustomPlatforms = [ValidPlatform("custom-1", "好平台"), broken];

        var result = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits);

        Assert.Single(result.Settings.CustomPlatforms);
        Assert.Equal("好平台", result.Settings.CustomPlatforms[0].Name);
        Assert.Equal(["缺地址的平台"], result.SkippedCustomPlatforms);
    }

    [Fact]
    public void Build_InvalidCustomPlatformWithoutName_IsReportedById()
    {
        var edits = SampleEdits();
        var broken = ValidPlatform("custom-7", string.Empty);
        edits.CustomPlatforms = [broken];

        var result = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits);

        Assert.Equal(["custom-7"], result.SkippedCustomPlatforms);
    }

    [Fact]
    public void Build_ValidCustomPlatforms_ReplaceBaselineList()
    {
        var edits = SampleEdits(); // 含 custom-1「新平台」，基线里是 custom-9「旧平台」

        var s = SettingsSnapshotBuilder.Build(FullyPopulatedBaseline(), edits).Settings;

        Assert.Single(s.CustomPlatforms);
        Assert.Equal("custom-1", s.CustomPlatforms[0].Id);
    }
}
