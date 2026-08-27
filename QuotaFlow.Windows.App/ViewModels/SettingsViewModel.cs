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

    // ---- 平台显示与顺序（面板卡片从上到下的顺序）----

    /// <summary>设置页"平台显示与顺序"的当前顺序；保存时写入 AppSettings.PlatformOrder。</summary>
    public ObservableCollection<PlatformOrderRow> PlatformRows { get; } = [];

    /// <summary>设置页"自定义平台"的可编辑行：新增/编辑的定义在保存前也驻留于此。</summary>
    public ObservableCollection<CustomPlatformRow> CustomPlatformRows { get; } = [];

    private static readonly string[] DefaultPlatformOrder = ["claude", "codex", "minimax", "deepseek"];

    private static readonly IReadOnlyDictionary<string, string> PlatformDisplayNames = new Dictionary<string, string>
    {
        ["claude"] = "Claude",
        ["codex"] = "Codex",
        ["minimax"] = "MiniMax",
        ["deepseek"] = "DeepSeek",
    };

    public IRelayCommand MovePlatformUpCommand { get; }
    public IRelayCommand MovePlatformDownCommand { get; }
    public IRelayCommand AddCustomPlatformCommand { get; }

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
        MovePlatformUpCommand = new RelayCommand<PlatformOrderRow>(MovePlatformUp, r => r is not null && PlatformRows.IndexOf(r) > 0);
        MovePlatformDownCommand = new RelayCommand<PlatformOrderRow>(MovePlatformDown,
            r => r is not null && PlatformRows.IndexOf(r) >= 0 && PlatformRows.IndexOf(r) < PlatformRows.Count - 1);
        AddCustomPlatformCommand = new RelayCommand(AddCustomPlatform);

        // 已保存的自定义平台定义：按 Id 索引，供平台顺序加载与行加载共用。
        var customDefs = new Dictionary<string, CustomPlatformSettings>();
        foreach (var def in current.CustomPlatforms ?? [])
        {
            if (!string.IsNullOrWhiteSpace(def.Id) && !customDefs.ContainsKey(def.Id))
            {
                customDefs[def.Id] = def;
            }
        }

        // 平台顺序：优先用已保存的顺序，缺失的（内置默认 + 自定义）按自然顺序补在末尾。
        var savedOrder = current.PlatformOrder ?? DefaultPlatformOrder;
        foreach (var id in savedOrder)
        {
            if (PlatformDisplayNames.TryGetValue(id, out var name))
            {
                PlatformRows.Add(new PlatformOrderRow(id, name));
            }
            else if (customDefs.TryGetValue(id, out var def))
            {
                PlatformRows.Add(new PlatformOrderRow(id, def.Name));
            }
        }

        foreach (var id in DefaultPlatformOrder)
        {
            if (PlatformRows.All(r => r.ProviderId != id))
            {
                PlatformRows.Add(new PlatformOrderRow(id, PlatformDisplayNames[id]));
            }
        }

        foreach (var def in customDefs.Values)
        {
            if (PlatformRows.All(r => r.ProviderId != def.Id))
            {
                PlatformRows.Add(new PlatformOrderRow(def.Id, def.Name));
            }
        }

        // 自定义平台行：已保存的定义逐条加载成可编辑行。
        foreach (var def in customDefs.Values)
        {
            var row = new CustomPlatformRow(def.Id, _credentialStore, _verifyIdentity,
                DeleteCustomPlatform, s => StatusMessage = s, def);
            row.NameChanged += OnCustomRowNameChanged;
            CustomPlatformRows.Add(row);
        }

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
            CredentialStatus.NotFound => "未检测到，请先运行 Codex CLI 并使用 ChatGPT 账号登录",
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

    private void MovePlatformUp(PlatformOrderRow? row) => MovePlatformBy(row, -1);

    private void MovePlatformDown(PlatformOrderRow? row) => MovePlatformBy(row, +1);

    private void MovePlatformBy(PlatformOrderRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = PlatformRows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= PlatformRows.Count)
        {
            return;
        }

        PlatformRows.Move(index, target);
        // 位置变了，各行的上/下按钮可用性跟着变。
        MovePlatformUpCommand.NotifyCanExecuteChanged();
        MovePlatformDownCommand.NotifyCanExecuteChanged();
    }

    private void AddCustomPlatform()
    {
        var id = $"custom-{NextCustomPlatformIndex()}";
        var row = new CustomPlatformRow(id, _credentialStore, _verifyIdentity,
            DeleteCustomPlatform, s => StatusMessage = s);
        row.NameChanged += OnCustomRowNameChanged;
        CustomPlatformRows.Add(row);
        PlatformRows.Add(new PlatformOrderRow(id, row.Name));
        row.IsExpanded = true; // 新行直接展开，方便填写
        MovePlatformUpCommand.NotifyCanExecuteChanged();
        MovePlatformDownCommand.NotifyCanExecuteChanged();
        StatusMessage = "已添加自定义平台（保存后生效）";
    }

    private void DeleteCustomPlatform(CustomPlatformRow row)
    {
        CustomPlatformRows.Remove(row);
        row.NameChanged -= OnCustomRowNameChanged;

        var orderRow = PlatformRows.FirstOrDefault(r => r.ProviderId == row.Id);
        if (orderRow is not null)
        {
            PlatformRows.Remove(orderRow);
        }

        _credentialStore.Delete(row.CredentialKeyName); // 删平台即删凭据（含孤儿凭据）
        MovePlatformUpCommand.NotifyCanExecuteChanged();
        MovePlatformDownCommand.NotifyCanExecuteChanged();
        StatusMessage = $"已删除「{row.Name}」";
    }

    /// <summary>自定义平台改名时，同步"平台显示与顺序"里的显示名。</summary>
    private void OnCustomRowNameChanged(object? sender, EventArgs e)
    {
        if (sender is not CustomPlatformRow row)
        {
            return;
        }

        var orderRow = PlatformRows.FirstOrDefault(r => r.ProviderId == row.Id);
        if (orderRow is not null)
        {
            orderRow.DisplayName = row.Name;
        }
    }

    /// <summary>分配下一个 custom-{n}：取所有已存在行（含已保存定义）编号的最大值 + 1。</summary>
    private int NextCustomPlatformIndex()
    {
        var max = 0;
        foreach (var row in CustomPlatformRows)
        {
            if (TryParseCustomIndex(row.Id, out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return max + 1;
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

        // 自定义平台：逐行校验，配置不完整的行直接跳过（不落盘），并提示用户。
        var customPlatforms = new List<CustomPlatformSettings>();
        var skippedInvalid = false;
        foreach (var row in CustomPlatformRows)
        {
            var def = row.ToSettings();
            if (string.IsNullOrWhiteSpace(def.Name) ||
                string.IsNullOrWhiteSpace(def.Endpoint) ||
                string.IsNullOrWhiteSpace(def.ValuePath) ||
                !Uri.TryCreate(def.Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                (def.AuthKind == CustomAuthKind.CustomHeader && string.IsNullOrWhiteSpace(def.HeaderName)))
            {
                skippedInvalid = true;
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
            // 面板平台顺序（当前行的顺序即面板从上到下的顺序；空集合转 null = 用默认顺序）。
            PlatformOrder = PlatformRows.Count > 0 ? PlatformRows.Select(r => r.ProviderId).ToArray() : null,
            CustomPlatforms = customPlatforms,
        };

        _settingsStore.Save(settings);
        SettingsSaved?.Invoke(this, settings);
        StatusMessage = skippedInvalid
            ? "已保存（有自定义平台配置不完整被跳过：名称、接口地址、取值路径、有效 http(s) 地址、自定义请求头名任一缺失都不会保存）"
            : "设置已保存";
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

/// <summary>
/// 设置页"平台显示与顺序"的单个平台行：显示名 + 面板中的位置（由列表顺序决定）。
/// 改为可写对象后，自定义平台改名时能同步更新这里显示的显示名。
/// </summary>
public sealed partial class PlatformOrderRow : ObservableObject
{
    public string ProviderId { get; }

    [ObservableProperty] private string _displayName;

    public PlatformOrderRow(string providerId, string displayName)
    {
        ProviderId = providerId;
        _displayName = displayName;
    }
}
