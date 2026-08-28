using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 设置页 ViewModel。文档 §5.5 要求的项目：自动刷新间隔、启动时刷新、开机启动、主题、
/// MiniMax/DeepSeek 凭据、Claude/Codex 本机登录状态检测、清除缓存和安全凭据。
///
/// MiniMax/DeepSeek 的 Key 只经 <see cref="SecureCredentialStore"/> 写入 Windows 凭据管理器，
/// 保存后立即清空输入框，绝不在界面上常驻明文、也绝不落地普通文件。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string MiniMaxKeyName = "minimax:ApiKey";
    public const string DeepSeekKeyName = "deepseek:ApiKey";

    private readonly AppSettingsStore _settingsStore;
    private readonly SecureCredentialStore _credentialStore;
    private readonly LocalCache _cache;

    [ObservableProperty] private int _autoRefreshIntervalMinutes;
    [ObservableProperty] private bool _refreshOnStartup;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private ThemeMode _theme;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMiniMaxUrl))]
    private MiniMaxRegion _miniMaxRegion;
    [ObservableProperty] private bool _showUnknownWindows;
    [ObservableProperty] private ClockDisplayFormat _clockDisplayFormat;

    /// <summary>
    /// 面板卡片顺序：v1.0.6 起改由面板本身的 ▲/▼ 直接调整并即时持久化（见 MainPanelViewModel），
    /// 设置页不再重复提供这个入口。这里只是原样透传打开设置页时读到的顺序，避免"保存设置"
    /// 把用户刚在面板上调整过的顺序覆盖回旧值。
    /// </summary>
    private readonly string[]? _initialPlatformOrder;

    /// <summary>
    /// 额度显示语义（已用/剩余）：v1.1.0 起改由面板顶部的切换按钮直接调整并即时持久化
    /// （见 MainPanelViewModel），设置页不提供入口。原样透传，避免"保存设置"把面板上刚切换过的
    /// 语义覆盖回默认值。
    /// </summary>
    private readonly QuotaDisplaySemantic _initialQuotaDisplaySemantic;

    /// <summary>设置页"自定义平台"的可编辑行：新增/编辑的定义在保存前也驻留于此。</summary>
    public ObservableCollection<CustomPlatformRow> CustomPlatformRows { get; } = [];

    /// <summary>一个自定义平台都没有时，卡片显示引导文案而不是一片空白。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomPlatformsEmptyHintVisibility))]
    private bool _hasCustomPlatforms;

    public Visibility CustomPlatformsEmptyHintVisibility => HasCustomPlatforms ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 删除自定义平台时，对应的凭据键名先记在这里，真正的删除动作推迟到点击"保存设置"才执行——
    /// 不保存就关闭设置页等于撤销这次删除，与本页其它编辑（顺序调整、新增平台）保持一致。
    /// </summary>
    private readonly HashSet<string> _pendingCredentialDeletions = [];

    /// <summary>本次设置页会话里下一个可用的 custom-{n} 编号，只增不减：即使中途删除了编号最大的行，
    /// 再次新增也不会复用同一个 Id（Id 与凭据键一一对应，复用会撞上还没真正执行的挂起删除）。</summary>
    private int _nextCustomPlatformSeq = 1;

    public IRelayCommand AddCustomPlatformCommand { get; }
    public IRelayCommand AddOpenCodeGoTemplateCommand { get; }

    // 接口地址覆盖（各平台分组下的"接口地址（可选）"）：空串 = 使用内置默认。保存时转成 null 落盘。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveClaudeUrl))]
    private string _claudeEndpointOverride = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveCodexUrl))]
    private string _codexEndpointOverride = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMiniMaxUrl))]
    private string _miniMaxEndpointOverride = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveDeepSeekUrl))]
    private string _deepSeekEndpointOverride = string.Empty;

    [ObservableProperty] private string _miniMaxConfiguredText = "未配置";
    [ObservableProperty] private string _deepSeekConfiguredText = "未配置";

    // ---- API Key 统一输入（"眼睛 + 单行输入框"）----
    // 未配置：输入框可直接输入新 Key。
    // 已配置 + 掩码：输入框只读，防止盲改或误把掩码当 Key 保存。
    // 已配置 + 展开：经 Windows Hello 验证后显示明文，且可编辑。
    // 明文、掩码、用户新输入都走同一个 *KeyDisplayText 字段：输入框可写时（未配置/已展开）
    // 用户编辑回写该字段，保存时直接读它写进 Windows 凭据管理器。
    private const string KeyMask = "••••••••••••";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyIsReadOnly))]
    [NotifyPropertyChangedFor(nameof(MiniMaxEyeVisibility))]
    private bool _isMiniMaxConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyIsReadOnly))]
    private bool _isMiniMaxKeyRevealed;

    [ObservableProperty] private string _miniMaxKeyDisplayText = string.Empty;

    public bool MiniMaxKeyIsReadOnly => IsMiniMaxConfigured && !IsMiniMaxKeyRevealed;

    public Visibility MiniMaxEyeVisibility => IsMiniMaxConfigured ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyIsReadOnly))]
    [NotifyPropertyChangedFor(nameof(DeepSeekEyeVisibility))]
    private bool _isDeepSeekConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyIsReadOnly))]
    private bool _isDeepSeekKeyRevealed;

    [ObservableProperty] private string _deepSeekKeyDisplayText = string.Empty;

    public bool DeepSeekKeyIsReadOnly => IsDeepSeekConfigured && !IsDeepSeekKeyRevealed;

    public Visibility DeepSeekEyeVisibility => IsDeepSeekConfigured ? Visibility.Visible : Visibility.Collapsed;

    // ---- 当前生效的接口地址（供设置页各平台分组实时展示；仅展示，不参与查询逻辑）----

    /// <summary>Claude 当前生效的用量接口地址：已填覆盖值则用之，否则为内置默认。</summary>
    public string EffectiveClaudeUrl => EndpointResolver.Resolve(ClaudeEndpointOverride, ClaudeQuotaProvider.DefaultUsageUrl);

    /// <summary>Codex 当前生效的用量接口地址。</summary>
    public string EffectiveCodexUrl => EndpointResolver.Resolve(CodexEndpointOverride, CodexQuotaProvider.DefaultUsageUrl);

    /// <summary>MiniMax 当前生效的用量接口地址：随区域选择切换内置默认。</summary>
    public string EffectiveMiniMaxUrl => EndpointResolver.Resolve(MiniMaxEndpointOverride,
        MiniMaxRegion == MiniMaxRegion.China
            ? MiniMaxQuotaProvider.DefaultUsageUrlCn
            : MiniMaxQuotaProvider.DefaultUsageUrlIntl);

    /// <summary>DeepSeek 当前生效的余额接口地址。</summary>
    public string EffectiveDeepSeekUrl => EndpointResolver.Resolve(DeepSeekEndpointOverride, DeepSeekBalanceProvider.DefaultUsageUrl);

    [ObservableProperty] private string _claudeLoginStatusText = "检测中...";
    [ObservableProperty] private string _codexLoginStatusText = "检测中...";

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>身份验证入口，默认用 Windows Hello/密码；单测里可以换成假实现。</summary>
    private readonly Func<string, Task<IdentityVerificationResult>> _verifyIdentity;

    public int[] AllowedRefreshIntervals => AppSettings.AllowedRefreshIntervals;

    private readonly DispatcherTimer _clockPreviewTimer;

    /// <summary>
    /// "日期显示格式"下拉选项：每个选项显示该格式的实时样本
    /// （例如 "2026-08-27 周四 · 第35周 · 14:30:05"），每秒随当前时刻刷新。
    /// </summary>
    public IReadOnlyList<ClockDisplayFormatOption> ClockDisplayFormatOptions { get; }

    /// <summary>关于页版本号：直接取程序集版本（csproj &lt;Version&gt;），避免手工改 UI 文本造成漂移。</summary>
    public string AppVersionText
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return $"QuotaFlow for Windows · v{(version is null ? "unknown" : version.ToString(3))}";
        }
    }

    /// <summary>保存后通知外部（MainPanelViewModel / ThemeManager）应用新设置。</summary>
    public event EventHandler<AppSettings>? SettingsSaved;

    public IRelayCommand SaveMiniMaxKeyCommand { get; }
    public IRelayCommand SaveDeepSeekKeyCommand { get; }
    public IRelayCommand ClearMiniMaxKeyCommand { get; }
    public IRelayCommand ClearDeepSeekKeyCommand { get; }
    public IRelayCommand ClearCacheCommand { get; }
    public IRelayCommand RefreshLoginStatusCommand { get; }
    public IRelayCommand SaveGeneralSettingsCommand { get; }
    public IAsyncRelayCommand ToggleRevealMiniMaxKeyCommand { get; }
    public IAsyncRelayCommand ToggleRevealDeepSeekKeyCommand { get; }

    public SettingsViewModel(AppSettingsStore settingsStore, SecureCredentialStore credentialStore, LocalCache cache,
        AppSettings current, Func<string, Task<IdentityVerificationResult>>? verifyIdentity = null)
    {
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _cache = cache;
        _verifyIdentity = verifyIdentity ?? IdentityVerifier.VerifyAsync;

        _autoRefreshIntervalMinutes = current.AutoRefreshIntervalMinutes;
        _refreshOnStartup = current.RefreshOnStartup;
        // 开机启动开关以注册表实际状态为准，避免设置文件和系统状态不一致（例如用户手动改过注册表）。
        _startWithWindows = AutoStartService.IsEnabled();
        _theme = current.Theme;
        _miniMaxRegion = current.MiniMaxRegion;
        _showUnknownWindows = current.ShowUnknownWindows;
        _clockDisplayFormat = current.ClockDisplayFormat;
        _claudeEndpointOverride = current.ClaudeEndpointOverride ?? string.Empty;
        _codexEndpointOverride = current.CodexEndpointOverride ?? string.Empty;
        _miniMaxEndpointOverride = current.MiniMaxEndpointOverride ?? string.Empty;
        _deepSeekEndpointOverride = current.DeepSeekEndpointOverride ?? string.Empty;
        _initialPlatformOrder = current.PlatformOrder;
        _initialQuotaDisplaySemantic = current.QuotaDisplaySemantic;

        _clockPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockPreviewTimer.Tick += (_, _) => RefreshClockPreviews();
        ClockDisplayFormatOptions =
        [
            new(ClockDisplayFormat.Full, "完整"),
            new(ClockDisplayFormat.DateWeekdayTime, "含星期"),
            new(ClockDisplayFormat.DateTime, "标准"),
            new(ClockDisplayFormat.TimeOnly, "仅时间"),
        ];
        RefreshClockPreviews(); // 立即生成当前时刻的预览，打开设置页时下拉不是空的

        SaveMiniMaxKeyCommand = new RelayCommand(SaveMiniMaxKey);
        SaveDeepSeekKeyCommand = new RelayCommand(SaveDeepSeekKey);
        ClearMiniMaxKeyCommand = new RelayCommand(ClearMiniMaxKey);
        ClearDeepSeekKeyCommand = new RelayCommand(ClearDeepSeekKey);
        ClearCacheCommand = new RelayCommand(ClearCache);
        RefreshLoginStatusCommand = new RelayCommand(RefreshLoginStatus);
        SaveGeneralSettingsCommand = new RelayCommand(SaveGeneralSettings);
        ToggleRevealMiniMaxKeyCommand = new AsyncRelayCommand(ToggleRevealMiniMaxKeyAsync);
        ToggleRevealDeepSeekKeyCommand = new AsyncRelayCommand(ToggleRevealDeepSeekKeyAsync);
        AddCustomPlatformCommand = new RelayCommand(AddCustomPlatform);
        AddOpenCodeGoTemplateCommand = new RelayCommand(AddOpenCodeGoTemplate);

        // 自定义平台行：已保存的定义逐条加载成可编辑行（防御性去重——正常写入路径不会产生
        // 重复 Id，但不排除配置文件被手工改坏）。
        var seenCustomIds = new HashSet<string>();
        foreach (var def in current.CustomPlatforms ?? [])
        {
            if (string.IsNullOrWhiteSpace(def.Id) || !seenCustomIds.Add(def.Id))
            {
                continue;
            }

            var row = new CustomPlatformRow(def.Id, _credentialStore, _verifyIdentity,
                DeleteCustomPlatform, s => StatusMessage = s, def);
            CustomPlatformRows.Add(row);

            if (TryParseCustomIndex(def.Id, out var n) && n >= _nextCustomPlatformSeq)
            {
                _nextCustomPlatformSeq = n + 1;
            }
        }

        HasCustomPlatforms = CustomPlatformRows.Count > 0;
        RefreshCredentialLabels();
        // 已配置的 Key 默认以掩码呈现，避免打开设置页就直接把明文带出来。
        MiniMaxKeyDisplayText = IsMiniMaxConfigured ? KeyMask : string.Empty;
        DeepSeekKeyDisplayText = IsDeepSeekConfigured ? KeyMask : string.Empty;
        RefreshLoginStatus();
    }

    private void RefreshCredentialLabels()
    {
        IsMiniMaxConfigured = !string.IsNullOrEmpty(_credentialStore.TryRead(MiniMaxKeyName));
        IsDeepSeekConfigured = !string.IsNullOrEmpty(_credentialStore.TryRead(DeepSeekKeyName));
        MiniMaxConfiguredText = IsMiniMaxConfigured ? "已配置" : "未配置";
        DeepSeekConfiguredText = IsDeepSeekConfigured ? "已配置" : "未配置";
    }

    private async Task ToggleRevealMiniMaxKeyAsync()
    {
        if (IsMiniMaxKeyRevealed)
        {
            HideMiniMaxKey();
            return;
        }

        var result = await _verifyIdentity("验证身份以查看 MiniMax API Key 明文");
        if (result != IdentityVerificationResult.Verified)
        {
            StatusMessage = result == IdentityVerificationResult.Cancelled ? "已取消验证" : "验证失败，无法显示明文";
            return;
        }

        MiniMaxKeyDisplayText = _credentialStore.TryRead(MiniMaxKeyName) ?? string.Empty;
        IsMiniMaxKeyRevealed = true;
    }

    private async Task ToggleRevealDeepSeekKeyAsync()
    {
        if (IsDeepSeekKeyRevealed)
        {
            HideDeepSeekKey();
            return;
        }

        var result = await _verifyIdentity("验证身份以查看 DeepSeek API Key 明文");
        if (result != IdentityVerificationResult.Verified)
        {
            StatusMessage = result == IdentityVerificationResult.Cancelled ? "已取消验证" : "验证失败，无法显示明文";
            return;
        }

        DeepSeekKeyDisplayText = _credentialStore.TryRead(DeepSeekKeyName) ?? string.Empty;
        IsDeepSeekKeyRevealed = true;
    }

    private void HideMiniMaxKey()
    {
        IsMiniMaxKeyRevealed = false;
        MiniMaxKeyDisplayText = KeyMask;
    }

    private void HideDeepSeekKey()
    {
        IsDeepSeekKeyRevealed = false;
        DeepSeekKeyDisplayText = KeyMask;
    }

    /// <summary>窗口失焦/关闭时调用，避免明文 Key 在界面上停留超出必要时间。</summary>
    public void HideAllRevealedKeys()
    {
        HideMiniMaxKey();
        HideDeepSeekKey();
        foreach (var row in CustomPlatformRows)
        {
            row.HideKey();
        }
    }

    private void RefreshLoginStatus()
    {
        var claude = new ClaudeCredentialReader().Read();
        ClaudeLoginStatusText = claude.Status switch
        {
            CredentialStatus.Valid => "已检测到本机登录",
            CredentialStatus.Expired => "已检测到本机登录（凭据可能已过期，如额度显示异常请重新登录）",
            CredentialStatus.NotFound => "未检测到，请先运行 Claude Code 并登录",
            CredentialStatus.ParseError => "凭据文件无法解析",
            _ => "未知状态",
        };

        var codex = new CodexCredentialReader().Read();
        CodexLoginStatusText = codex.Status switch
        {
            CredentialStatus.Valid => "已检测到本机 ChatGPT 登录",
            CredentialStatus.Expired => "已检测到本机登录（凭据可能已过期，如额度显示异常请重新登录）",
            // NotFound 覆盖两种情况：真的没有 auth.json（Message 为 null），和文件存在但登录成了
            // API Key 模式而非 ChatGPT 订阅模式（Message 会点明这一点）——后者不该被提示"请登录"，
            // 用户其实已经登录了，只是模式不对，用具体原因才不会误导。
            CredentialStatus.NotFound => codex.Message ?? "未检测到，请先运行 Codex CLI 并使用 ChatGPT 账号登录",
            CredentialStatus.ParseError => "凭据文件无法解析",
            _ => "未知状态",
        };
    }

    private void SaveMiniMaxKey()
    {
        // 掩码状态下输入框只读，不会产生新值；直接保存会误把掩码写进凭据管理器。
        if (MiniMaxKeyIsReadOnly)
        {
            StatusMessage = "当前为掩码状态，点开眼睛并验证身份后即可编辑保存";
            return;
        }

        var value = MiniMaxKeyDisplayText?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "请输入 MiniMax API Key";
            return;
        }

        _credentialStore.Save(MiniMaxKeyName, value);
        IsMiniMaxConfigured = true;
        MiniMaxKeyDisplayText = KeyMask;
        IsMiniMaxKeyRevealed = false;
        RefreshCredentialLabels();
        StatusMessage = "MiniMax API Key 已保存";
    }

    private void SaveDeepSeekKey()
    {
        if (DeepSeekKeyIsReadOnly)
        {
            StatusMessage = "当前为掩码状态，点开眼睛并验证身份后即可编辑保存";
            return;
        }

        var value = DeepSeekKeyDisplayText?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "请输入 DeepSeek API Key";
            return;
        }

        _credentialStore.Save(DeepSeekKeyName, value);
        IsDeepSeekConfigured = true;
        DeepSeekKeyDisplayText = KeyMask;
        IsDeepSeekKeyRevealed = false;
        RefreshCredentialLabels();
        StatusMessage = "DeepSeek API Key 已保存";
    }

    private void ClearMiniMaxKey()
    {
        _credentialStore.Delete(MiniMaxKeyName);
        MiniMaxKeyDisplayText = string.Empty;
        IsMiniMaxKeyRevealed = false;
        RefreshCredentialLabels();
        StatusMessage = "已清除 MiniMax 凭据";
    }

    private void ClearDeepSeekKey()
    {
        _credentialStore.Delete(DeepSeekKeyName);
        DeepSeekKeyDisplayText = string.Empty;
        IsDeepSeekKeyRevealed = false;
        RefreshCredentialLabels();
        StatusMessage = "已清除 DeepSeek 凭据";
    }

    private void ClearCache()
    {
        _cache.Clear();
        StatusMessage = "已清除本地缓存";
    }

    private void AddCustomPlatform()
    {
        var id = $"custom-{_nextCustomPlatformSeq++}";
        var row = new CustomPlatformRow(id, _credentialStore, _verifyIdentity,
            DeleteCustomPlatform, s => StatusMessage = s);
        CustomPlatformRows.Add(row);
        row.IsExpanded = true; // 新行直接展开，方便填写
        HasCustomPlatforms = true;
        StatusMessage = "已添加自定义平台（保存后生效）";
    }

    /// <summary>OpenCode GO 内置模板：预填地址/鉴权方式/三个额度窗口，只需填 API Key 就能用。</summary>
    private void AddOpenCodeGoTemplate()
    {
        var id = $"custom-{_nextCustomPlatformSeq++}";
        var row = CustomPlatformRow.CreateOpenCodeGoTemplate(id, _credentialStore, _verifyIdentity,
            DeleteCustomPlatform, s => StatusMessage = s);
        CustomPlatformRows.Add(row);
        HasCustomPlatforms = true;
        StatusMessage = "已添加 OpenCode GO 模板，填写并保存 API Key 后点击「保存设置」即可生效";
    }

    private void DeleteCustomPlatform(CustomPlatformRow row)
    {
        CustomPlatformRows.Remove(row);

        // 真正删除凭据推迟到"保存设置"时执行（见 _pendingCredentialDeletions 上的说明）；
        // 这里只把行从列表里挪走，用户还有机会用"不保存就关闭"来撤销这次删除。
        _pendingCredentialDeletions.Add(row.CredentialKeyName);
        HasCustomPlatforms = CustomPlatformRows.Count > 0;
        StatusMessage = string.IsNullOrWhiteSpace(row.Name)
            ? "已移除该自定义平台（保存后生效）"
            : $"已移除「{row.Name}」（保存后生效）";
    }

    private static bool TryParseCustomIndex(string id, out int index)
    {
        index = 0;
        const string prefix = "custom-";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(id[prefix.Length..], out var n) ||
            n < 0)
        {
            return false;
        }

        index = n;
        return true;
    }

    private void SaveGeneralSettings()
    {
        AutoStartService.SetEnabled(StartWithWindows);

        // 自定义平台：逐行校验，配置不完整的行直接跳过（不落盘），并点名提示用户是哪一个。
        var customPlatforms = new List<CustomPlatformSettings>();
        var skippedNames = new List<string>();
        foreach (var row in CustomPlatformRows)
        {
            var def = row.ToSettings();
            if (!IsValidCustomPlatform(def))
            {
                skippedNames.Add(string.IsNullOrWhiteSpace(def.Name) ? row.Id : def.Name);
                continue;
            }

            customPlatforms.Add(def);
        }

        var settings = new AppSettings
        {
            AutoRefreshIntervalMinutes = AutoRefreshIntervalMinutes,
            RefreshOnStartup = RefreshOnStartup,
            StartWithWindows = StartWithWindows,
            Theme = Theme,
            MiniMaxRegion = MiniMaxRegion,
            ShowUnknownWindows = ShowUnknownWindows,
            ClockDisplayFormat = ClockDisplayFormat,
            // 接口地址覆盖：空串转 null（= 用内置默认）。不并入则保存普通设置会把覆盖项清掉。
            ClaudeEndpointOverride = ToNullIfEmpty(ClaudeEndpointOverride),
            CodexEndpointOverride = ToNullIfEmpty(CodexEndpointOverride),
            MiniMaxEndpointOverride = ToNullIfEmpty(MiniMaxEndpointOverride),
            DeepSeekEndpointOverride = ToNullIfEmpty(DeepSeekEndpointOverride),
            // 面板顺序现在只由面板自己的 ▲/▼ 调整并即时持久化（见 MainPanelViewModel），
            // 这里原样透传打开设置页时读到的值，不要覆盖用户在面板上刚调整过的顺序。
            PlatformOrder = _initialPlatformOrder,
            // 已用/剩余显示语义同理，由面板顶部切换并即时持久化，这里原样透传。
            QuotaDisplaySemantic = _initialQuotaDisplaySemantic,
            CustomPlatforms = customPlatforms,
        };

        _settingsStore.Save(settings);

        // 执行挂起的凭据删除：只删"确实已经不在当前行列表里"的键——万一被删掉的编号在本次会话
        // 里又被新增行占用（理论上不会发生，_nextCustomPlatformSeq 只增不减，这里是双重保险），
        // 也不会误删新行刚保存的 Key。
        if (_pendingCredentialDeletions.Count > 0)
        {
            var liveKeys = CustomPlatformRows.Select(r => r.CredentialKeyName).ToHashSet();
            foreach (var key in _pendingCredentialDeletions)
            {
                if (!liveKeys.Contains(key))
                {
                    _credentialStore.Delete(key);
                }
            }

            _pendingCredentialDeletions.Clear();
        }

        SettingsSaved?.Invoke(this, settings);
        StatusMessage = skippedNames.Count > 0
            ? $"设置已保存（以下自定义平台配置不完整未保存：{string.Join("、", skippedNames)}。请检查名称/接口地址/取值路径是否填写，自定义请求头方式还需填写请求头名称）"
            : "设置已保存";
    }

    /// <summary>
    /// 保存前校验一个自定义平台定义：平台级字段（名称/地址/鉴权）与 v1.0.5 一致；
    /// v1.1.0 新增对窗口列表的校验——至少一个窗口、每个窗口名称和取值路径必填、
    /// 同一平台内窗口名称不能重复、"已使用/剩余数值"语义必须配置额度上限。
    /// 任一条不满足就整个平台跳过保存（不做"部分窗口生效"这种更复杂的半保存）。
    /// </summary>
    private static bool IsValidCustomPlatform(CustomPlatformSettings def)
    {
        if (string.IsNullOrWhiteSpace(def.Name) ||
            string.IsNullOrWhiteSpace(def.Endpoint) ||
            !Uri.TryCreate(def.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            (def.AuthKind == CustomAuthKind.CustomHeader && string.IsNullOrWhiteSpace(def.HeaderName)))
        {
            return false;
        }

        if (def.QuotaWindows.Count == 0)
        {
            return false;
        }

        var seenWindowNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in def.QuotaWindows)
        {
            if (string.IsNullOrWhiteSpace(window.Name) || string.IsNullOrWhiteSpace(window.ValuePath))
            {
                return false;
            }

            if (!seenWindowNames.Add(window.Name))
            {
                return false; // 同一平台内窗口名称重复
            }

            if (window.DataKind is CustomDataKind.UsedValue or CustomDataKind.RemainingValue &&
                string.IsNullOrWhiteSpace(window.LimitPath) && window.FixedLimit is null)
            {
                return false; // 已使用/剩余数值语义必须配置额度上限（路径或固定值二选一）
            }
        }

        return true;
    }

    private static string? ToNullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>设置窗口显示后调用：启动每秒一次的"日期显示格式"实时预览。</summary>
    public void StartClockPreviewTimer() => _clockPreviewTimer.Start();

    /// <summary>设置窗口关闭时调用：停止实时预览定时器，避免窗口关闭后定时器继续空转。</summary>
    public void StopClockPreviewTimer() => _clockPreviewTimer.Stop();

    private void RefreshClockPreviews()
    {
        var now = DateTimeOffset.Now;
        foreach (var option in ClockDisplayFormatOptions)
        {
            option.RefreshPreview(now);
        }
    }
}

/// <summary>"日期显示格式"下拉的单个选项：显示该格式的实时样本 + 规范名称。</summary>
public sealed partial class ClockDisplayFormatOption : ObservableObject
{
    public ClockDisplayFormat Format { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _previewText = string.Empty;

    public ClockDisplayFormatOption(ClockDisplayFormat format, string name)
    {
        Format = format;
        Name = name;
    }

    /// <summary>ComboBox 展示文本：只显示该格式的实时样本（如 "2026-08-27 周四 · 第35周 · 14:30:05"），
    /// 四种格式的样本差异一目了然，不再追加"（完整）"这类后缀。</summary>
    public string Label => string.IsNullOrEmpty(PreviewText) ? Name : PreviewText;

    public void RefreshPreview(DateTimeOffset now) => PreviewText = ClockFormatter.Format(now, Format);

    /// <summary>让 UIA / 无障碍工具把选项读成样本文本，而不是默认的类型名。</summary>
    public override string ToString() => Label;
}
