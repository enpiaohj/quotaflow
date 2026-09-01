using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>把设置页的编辑内容合并进磁盘基线后的结果。</summary>
public sealed class SettingsSnapshotResult
{
    public required AppSettings Settings { get; init; }

    /// <summary>因配置不完整而被跳过、未写盘的自定义平台名称，供界面点名提示用户。</summary>
    public IReadOnlyList<string> SkippedCustomPlatforms { get; init; } = [];
}

/// <summary>
/// 把设置页的编辑内容合并进磁盘上的现有设置。
///
/// <b>核心不变量：设置页不拥有的字段必须原样保留。</b>
/// 具体是 <see cref="AppSettings.WindowDisplay"/>、<see cref="AppSettings.PlatformOrder"/>、
/// <see cref="AppSettings.QuotaDisplaySemantic"/>——它们由主面板即时调整并即时持久化，
/// 不经过设置页的"编辑草稿 + 点保存"流程。
///
/// 之所以要把这段逻辑放在 Core 而不是 ViewModel：这条不变量曾被破坏并造成真实的数据丢失
/// （点一次「保存设置」把窗口位置、显示模式、不透明度等全部重置回默认值），而当时它在
/// App 工程里，测试工程只引用 Core，写不了单测，只能靠 UI 自动化加截图才发现。
/// 下沉之后，<c>SettingsSnapshotBuilderTests</c> 用反射逐字段校验，
/// <b>将来给 AppSettings 新增字段时若忘记归类，测试会直接失败。</b>
/// </summary>
public static class SettingsSnapshotBuilder
{
    /// <summary>
    /// 设置页<b>不</b>拥有、必须从基线原样保留的字段名。
    ///
    /// 这份名单被测试用来做反射校验：AppSettings 里的每个字段要么由设置页写入，
    /// 要么在这份名单里。新增字段而不归类时测试失败——这正是当初漏掉 WindowDisplay 的场景。
    /// </summary>
    public static readonly IReadOnlyList<string> PanelOwnedFieldNames =
    [
        nameof(AppSettings.WindowDisplay),
        nameof(AppSettings.PlatformOrder),
        nameof(AppSettings.QuotaDisplaySemantic),
    ];

    /// <summary>
    /// 以 <paramref name="baseline"/>（通常是刚从磁盘读出的设置）为基础，
    /// 覆盖设置页拥有的字段，返回可直接写盘的设置。
    ///
    /// 直接修改并返回 <paramref name="baseline"/> 实例，不做深拷贝——调用方传进来的就是
    /// 一份刚 Load 出来的独立对象，再复制一次没有意义。
    /// </summary>
    public static SettingsSnapshotResult Build(AppSettings baseline, SettingsPageEdits edits)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edits);

        // 自定义平台逐行校验，配置不完整的直接跳过（不落盘），并点名告诉用户是哪一个——
        // 静默丢弃会让用户以为保存成功了。
        var accepted = new List<CustomPlatformSettings>();
        var skipped = new List<string>();
        foreach (var def in edits.CustomPlatforms)
        {
            if (CustomPlatformValidator.IsValid(def))
            {
                accepted.Add(def);
            }
            else
            {
                skipped.Add(string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name);
            }
        }

        baseline.AutoRefreshIntervalMinutes = edits.AutoRefreshIntervalMinutes;
        baseline.RefreshOnStartup = edits.RefreshOnStartup;
        baseline.StartWithWindows = edits.StartWithWindows;
        baseline.Theme = edits.Theme;
        baseline.MiniMaxRegion = edits.MiniMaxRegion;
        baseline.ShowUnknownWindows = edits.ShowUnknownWindows;
        baseline.ClockDisplayFormat = edits.ClockDisplayFormat;

        baseline.ProxyMode = edits.ProxyMode;
        baseline.ProxyAddress = NullIfBlank(edits.ProxyAddress);

        // 接口地址覆盖：空串等同"用内置默认"，统一转成 null，避免磁盘上出现空字符串。
        baseline.ClaudeEndpointOverride = NullIfBlank(edits.ClaudeEndpointOverride);
        baseline.CodexEndpointOverride = NullIfBlank(edits.CodexEndpointOverride);
        baseline.MiniMaxEndpointOverride = NullIfBlank(edits.MiniMaxEndpointOverride);
        baseline.DeepSeekEndpointOverride = NullIfBlank(edits.DeepSeekEndpointOverride);

        baseline.HotkeyEnabled = edits.HotkeyEnabled;
        baseline.HotkeyModifiers = edits.HotkeyModifiers;
        baseline.HotkeyKey = edits.HotkeyKey;

        // 只记录"被隐藏的"，不记录"要显示的"：将来新增内置平台时，老配置里自然不会出现它的
        // id，默认就是显示，不需要写迁移逻辑。空名单存 null，保持磁盘上的设置文件干净。
        baseline.HiddenPlatforms = edits.HiddenPlatforms.Count > 0 ? [.. edits.HiddenPlatforms] : null;

        baseline.CustomPlatforms = accepted;

        return new SettingsSnapshotResult { Settings = baseline, SkippedCustomPlatforms = skipped };
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
