using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Models;
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
    [ObservableProperty] private MiniMaxRegion _miniMaxRegion;
    [ObservableProperty] private bool _showUnknownWindows;
    [ObservableProperty] private ClockDisplayFormat _clockDisplayFormat;

    // 接口地址覆盖（"接口地址（高级）"卡片）：空串 = 使用内置默认。保存时转成 null 落盘。
    [ObservableProperty] private string _claudeEndpointOverride = string.Empty;
    [ObservableProperty] private string _codexEndpointOverride = string.Empty;
    [ObservableProperty] private string _miniMaxEndpointOverride = string.Empty;
    [ObservableProperty] private string _deepSeekEndpointOverride = string.Empty;

    [ObservableProperty] private string _miniMaxApiKeyInput = string.Empty;
    [ObservableProperty] private string _deepSeekApiKeyInput = string.Empty;
    [ObservableProperty] private string _miniMaxConfiguredText = "未配置";
    [ObservableProperty] private string _deepSeekConfiguredText = "未配置";
    [ObservableProperty] private bool _isMiniMaxConfigured;
    [ObservableProperty] private bool _isDeepSeekConfigured;

    // 明文查看：默认隐藏，点击"眼睛"需先通过 Windows Hello（或密码兜底）验证才会读取真实值。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyDisplayText))]
    private bool _isMiniMaxKeyRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniMaxKeyDisplayText))]
    private string _miniMaxRevealedKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyDisplayText))]
    private bool _isDeepSeekKeyRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepSeekKeyDisplayText))]
    private string _deepSeekRevealedKey = string.Empty;

    public string MiniMaxKeyDisplayText =>
        IsMiniMaxKeyRevealed && !string.IsNullOrEmpty(MiniMaxRevealedKey) ? MiniMaxRevealedKey : "••••••••••••";

    public string DeepSeekKeyDisplayText =>
        IsDeepSeekKeyRevealed && !string.IsNullOrEmpty(DeepSeekRevealedKey) ? DeepSeekRevealedKey : "••••••••••••";

    [ObservableProperty] private string _claudeLoginStatusText = "检测中...";
    [ObservableProperty] private string _codexLoginStatusText = "检测中...";

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>身份验证入口，默认用 Windows Hello/密码；单测里可以换成假实现。</summary>
    private readonly Func<string, Task<IdentityVerificationResult>> _verifyIdentity;

    public int[] AllowedRefreshIntervals => AppSettings.AllowedRefreshIntervals;

    /// <summary>"日期显示格式"下拉选项（标签 + 枚举值）。</summary>
    public IReadOnlyList<ClockDisplayFormatOption> ClockDisplayFormatOptions { get; } =
    [
        new(ClockDisplayFormat.Full, "日期 + 周几 + 第几周 + 时间"),
        new(ClockDisplayFormat.DateWeekdayTime, "日期 + 周几 + 时间"),
        new(ClockDisplayFormat.DateTime, "日期 + 时间"),
        new(ClockDisplayFormat.TimeOnly, "仅时间（实时）"),
    ];

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

        SaveMiniMaxKeyCommand = new RelayCommand(SaveMiniMaxKey);
        SaveDeepSeekKeyCommand = new RelayCommand(SaveDeepSeekKey);
        ClearMiniMaxKeyCommand = new RelayCommand(ClearMiniMaxKey);
        ClearDeepSeekKeyCommand = new RelayCommand(ClearDeepSeekKey);
        ClearCacheCommand = new RelayCommand(ClearCache);
        RefreshLoginStatusCommand = new RelayCommand(RefreshLoginStatus);
        SaveGeneralSettingsCommand = new RelayCommand(SaveGeneralSettings);
        ToggleRevealMiniMaxKeyCommand = new AsyncRelayCommand(ToggleRevealMiniMaxKeyAsync);
        ToggleRevealDeepSeekKeyCommand = new AsyncRelayCommand(ToggleRevealDeepSeekKeyAsync);

        RefreshCredentialLabels();
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

        MiniMaxRevealedKey = _credentialStore.TryRead(MiniMaxKeyName) ?? string.Empty;
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

        DeepSeekRevealedKey = _credentialStore.TryRead(DeepSeekKeyName) ?? string.Empty;
        IsDeepSeekKeyRevealed = true;
    }

    private void HideMiniMaxKey()
    {
        IsMiniMaxKeyRevealed = false;
        MiniMaxRevealedKey = string.Empty;
    }

    private void HideDeepSeekKey()
    {
        IsDeepSeekKeyRevealed = false;
        DeepSeekRevealedKey = string.Empty;
    }

    /// <summary>窗口失焦/关闭时调用，避免明文 Key 在界面上停留超出必要时间。</summary>
    public void HideAllRevealedKeys()
    {
        HideMiniMaxKey();
        HideDeepSeekKey();
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
        if (string.IsNullOrWhiteSpace(MiniMaxApiKeyInput))
        {
            return;
        }

        _credentialStore.Save(MiniMaxKeyName, MiniMaxApiKeyInput.Trim());
        MiniMaxApiKeyInput = string.Empty;
        HideMiniMaxKey();
        RefreshCredentialLabels();
        StatusMessage = "MiniMax API Key 已保存";
    }

    private void SaveDeepSeekKey()
    {
        if (string.IsNullOrWhiteSpace(DeepSeekApiKeyInput))
        {
            return;
        }

        _credentialStore.Save(DeepSeekKeyName, DeepSeekApiKeyInput.Trim());
        DeepSeekApiKeyInput = string.Empty;
        HideDeepSeekKey();
        RefreshCredentialLabels();
        StatusMessage = "DeepSeek API Key 已保存";
    }

    private void ClearMiniMaxKey()
    {
        _credentialStore.Delete(MiniMaxKeyName);
        HideMiniMaxKey();
        RefreshCredentialLabels();
        StatusMessage = "已清除 MiniMax 凭据";
    }

    private void ClearDeepSeekKey()
    {
        _credentialStore.Delete(DeepSeekKeyName);
        HideDeepSeekKey();
        RefreshCredentialLabels();
        StatusMessage = "已清除 DeepSeek 凭据";
    }

    private void ClearCache()
    {
        _cache.Clear();
        StatusMessage = "已清除本地缓存";
    }

    private void SaveGeneralSettings()
    {
        AutoStartService.SetEnabled(StartWithWindows);

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
        };

        _settingsStore.Save(settings);
        SettingsSaved?.Invoke(this, settings);
        StatusMessage = "设置已保存";
    }

    private static string? ToNullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>日期显示格式下拉的单个选项。</summary>
public sealed record ClockDisplayFormatOption(ClockDisplayFormat Format, string Label);
