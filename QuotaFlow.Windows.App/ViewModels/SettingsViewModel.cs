using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
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

    /// <summary>阿里云百炼 Token Plan（个人版）Console Cookie 的凭据管理器键名——与 API Key 同等
    /// 敏感度，只走 <see cref="SecureCredentialStore"/>（Windows 凭据管理器），绝不落盘/写日志。
    /// 用 UTF-8 存储：完整会话 Cookie 很长，默认的 UTF-16 编码会超出凭据管理器 2560 字节上限。</summary>
    public const string TokenPlanCookieKeyName = "alibaba:tokenplan:consoleCookie";

    /// <summary>登录时从控制台页面抓到的 SEC_TOKEN 的凭据管理器键名（同样 UTF-8 存储）。</summary>
    public const string TokenPlanSecTokenKeyName = "alibaba:tokenplan:secToken";

    private readonly AppSettingsStore _settingsStore;
    private readonly SecureCredentialStore _credentialStore;
    private readonly LocalCache _cache;
    private readonly IWindowPresentationCoordinator _presentation;

    /// <summary>
    /// 保存设置后用新设置去注册全局热键（返回是否注册成功），由组合根（App.xaml.cs）注入真正的
    /// <see cref="Services.GlobalHotkeyService"/> 调用；测试/未注入时默认当作"成功"，不用为了
    /// 单测特意起一个真的窗口句柄。
    /// </summary>
    private readonly Func<AppSettings, bool> _applyHotkeySettings;

    [ObservableProperty] private int _autoRefreshIntervalMinutes;
    [ObservableProperty] private bool _refreshOnStartup;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private ThemeMode _theme;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMiniMaxUrl))]
    private MiniMaxRegion _miniMaxRegion;
    [ObservableProperty] private bool _showUnknownWindows;
    [ObservableProperty] private ClockDisplayFormat _clockDisplayFormat;

    // ---- 显示/隐藏面板全局快捷键（默认 Alt+Z）----

    [ObservableProperty] private bool _hotkeyEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplayText))]
    private HotkeyModifiers _hotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplayText))]
    private string _hotkeyKey = "Z";

    /// <summary>捕获框里念给用户看的组合键文案，例如 "Alt+Z"。</summary>
    public string HotkeyDisplayText
    {
        get
        {
            var parts = new List<string>();
            if (HotkeyModifiers.HasFlag(HotkeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (HotkeyModifiers.HasFlag(HotkeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (HotkeyModifiers.HasFlag(HotkeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (HotkeyModifiers.HasFlag(HotkeyModifiers.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(HotkeyKey);
            return string.Join("+", parts);
        }
    }

    /// <summary>设置页的捕获框（PreviewKeyDown 代码后置）按下新组合键后调用；至少要有一个修饰键。</summary>
    public void SetHotkeyCombo(HotkeyModifiers modifiers, string keyName)
    {
        if (modifiers == HotkeyModifiers.None || string.IsNullOrWhiteSpace(keyName))
        {
            StatusMessage = "快捷键至少要包含一个修饰键（Alt/Ctrl/Shift/Win）";
            return;
        }

        HotkeyModifiers = modifiers;
        HotkeyKey = keyName;
    }

    // ---- 平台是否在面板显示 ----
    // "未配置自动隐藏"由 Provider 状态决定，不需要用户操心；这里是另一回事：平台配好了、
    // 但用户平时不想看。走普通的"编辑草稿 + 点保存"路径（不即时生效），与同一页其它平台
    // 配置项保持一致。

    [ObservableProperty] private bool _showClaudeOnPanel = true;
    [ObservableProperty] private bool _showCodexOnPanel = true;
    [ObservableProperty] private bool _showMiniMaxOnPanel = true;
    [ObservableProperty] private bool _showDeepSeekOnPanel = true;
    [ObservableProperty] private bool _showTokenPlanOnPanel = true;

    /// <summary>
    /// 本页是否有尚未保存的改动。
    ///
    /// 实现方式是"把当前编辑态构造成设置对象，与磁盘上的设置逐字段比对"，而不是给每个属性
    /// 挂脏标记——后者需要在每处新增属性时都记得加埋点，漏一个就会静默失效；比对法则天然
    /// 覆盖本页所有字段，将来加了新设置项也不会漏。
    ///
    /// 比对用 JSON 序列化：AppSettings 是纯数据对象没有值相等语义，而设置存储本来就以 JSON
    /// 形式落盘，序列化结果相同即意味着这次保存不会改变磁盘内容。
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            try
            {
                var pending = BuildSettingsSnapshot().Settings;
                var onDisk = _settingsStore.Load();
                return JsonSerializer.Serialize(pending) != JsonSerializer.Serialize(onDisk);
            }
            catch (Exception)
            {
                // 判断不出来时一律当作"没有改动"：这个属性只用于关窗前的挽留提示，
                // 宁可漏提示，也不能因为它自身出错就把设置窗口卡住关不掉。
                return false;
            }
        }
    }

    /// <summary>关窗前挽留提示选"保存"时调用，等价于点一次「保存设置」。</summary>
    public void SavePendingChanges() => SaveGeneralSettings();

    /// <summary>内置平台的 ProviderId → "是否显示"取值器，保存时据此拼 HiddenPlatforms。</summary>
    private IEnumerable<(string ProviderId, bool Show)> BuiltInPanelVisibility()
    {
        yield return ("claude", ShowClaudeOnPanel);
        yield return ("codex", ShowCodexOnPanel);
        yield return ("minimax", ShowMiniMaxOnPanel);
        yield return ("deepseek", ShowDeepSeekOnPanel);
        yield return ("alibaba-tokenplan", ShowTokenPlanOnPanel);
    }

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

    // ---- 显示与窗口（文档"显示模式开发提示词"）----
    // 这一组全部直接读写 IWindowPresentationCoordinator，不走本页"编辑草稿 + 点保存"那一套——
    // 跟面板顶部的固定/显示语义切换一样，属于"改了就该立刻看到效果"的设置，走批量保存反而会有
    // "面板已经变了，设置页显示的还是旧值/保存时把面板刚变的状态覆盖回去"的撕裂风险。
    // _presentation.StateChanged 统一转发成 OnPropertyChanged()（见构造函数），任何一处改动
    // （包括面板顶部的模式切换按钮）都会让这里的绑定跟着刷新。

    public WindowPresentationMode WindowMode => _presentation.CurrentMode;

    public bool WindowAlwaysOnTop
    {
        get => _presentation.IsAlwaysOnTop;
        set => _presentation.SetAlwaysOnTop(value);
    }

    public bool WindowPositionLocked
    {
        get => _presentation.IsPositionLocked;
        set => _presentation.SetPositionLocked(value);
    }

    public bool WindowCompactLayout
    {
        get => _presentation.IsCompactLayout;
        set => _presentation.SetCompactLayout(value);
    }

    /// <summary>0.7–1.0，滑块步进 0.05；托盘弹出模式下这个值不生效（该模式恒为不透明）。</summary>
    public double WindowOpacity
    {
        get => _presentation.Opacity;
        set => _presentation.SetOpacity(value);
    }

    public WindowMaterial WindowMaterial
    {
        get => _presentation.Material;
        set => _presentation.SetMaterial(value);
    }

    public bool WindowSnapToEdges
    {
        get => _presentation.SnapToEdges;
        set => _presentation.SetSnapToEdges(value);
    }

    public bool WindowRestoreLastModeOnStartup
    {
        get => _presentation.RestoreLastModeOnStartup;
        set => _presentation.SetRestoreLastModeOnStartup(value);
    }

    public bool WindowEnhanceReadabilityOnHover
    {
        get => _presentation.EnhanceReadabilityOnHover;
        set => _presentation.SetEnhanceReadabilityOnHover(value);
    }

    /// <summary>当前系统/窗口是否具备真正的 DWM Mica/Acrylic 能力——不支持时材质选项仍可选，
    /// 但会自动降级为纯色近似（见 WindowMaterialService 上的说明），这里只用来在设置页给一句
    /// 提示，不隐藏选项本身（隐藏了用户会以为这个功能不存在，而不是"这台机器暂时用不了"）。</summary>
    public bool IsMaterialFullySupported => WindowMaterialService.IsMaterialSupported();

    public IAsyncRelayCommand EnterTrayModeCommand { get; }
    public IAsyncRelayCommand EnterFloatingModeCommand { get; }
    public IAsyncRelayCommand EnterDesktopPanelModeCommand { get; }
    public IRelayCommand RestoreDefaultWindowPositionCommand { get; }

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

    // ---- 阿里云百炼 Token Plan（个人版）：WebView2 一键登录 ----
    // 登录态（Console Cookie）由 AlibabaLoginWindow 用 WebView2 自动抓取并写入 Windows 凭据管理器，
    // 用户不需要也看不到 Cookie 明文。这里只暴露"是否已登录"和"一键登录 / 清除登录态"两个入口。

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenPlanConfiguredText))]
    private bool _isTokenPlanConfigured;

    public string TokenPlanConfiguredText => IsTokenPlanConfigured ? "已登录" : "未登录";

    /// <summary>一键登录命令运行中（避免重复点击打开多个登录窗口）。</summary>
    [ObservableProperty] private bool _isTokenPlanLoggingIn;

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
    public IAsyncRelayCommand LoginTokenPlanCommand { get; }
    public IRelayCommand ClearTokenPlanCookieCommand { get; }
    public IRelayCommand ClearCacheCommand { get; }
    public IRelayCommand OpenDataDirectoryCommand { get; }
    public IAsyncRelayCommand ExportBackupCommand { get; }
    public IRelayCommand ImportBackupCommand { get; }
    public IRelayCommand RefreshLoginStatusCommand { get; }
    public IRelayCommand SaveGeneralSettingsCommand { get; }
    public IAsyncRelayCommand ToggleRevealMiniMaxKeyCommand { get; }
    public IAsyncRelayCommand ToggleRevealDeepSeekKeyCommand { get; }

    public SettingsViewModel(AppSettingsStore settingsStore, SecureCredentialStore credentialStore, LocalCache cache,
        AppSettings current, IWindowPresentationCoordinator presentation,
        Func<string, Task<IdentityVerificationResult>>? verifyIdentity = null,
        Func<AppSettings, bool>? applyHotkeySettings = null)
    {
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _cache = cache;
        _presentation = presentation;
        _verifyIdentity = verifyIdentity ?? IdentityVerifier.VerifyAsync;
        _applyHotkeySettings = applyHotkeySettings ?? (_ => true);

        // 面板顶部模式切换按钮、或另一个已打开的设置窗口（理论上不会同时开两个，防御性处理）
        // 改了显示设置时，这里跟着刷新——用空属性名让 WPF 把绑定到本 ViewModel 的所有属性都
        // 重新拉取一遍，不用为"显示与窗口"那十来个属性逐个手写 NotifyPropertyChangedFor。
        _presentation.StateChanged += (_, _) => OnPropertyChanged(string.Empty);

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

        // 隐藏名单里没有的平台就是显示（默认全部显示）。用 ?? [] 兜底手改配置写成 null 的情况。
        var hidden = new HashSet<string>(current.HiddenPlatforms ?? [], StringComparer.OrdinalIgnoreCase);
        _showClaudeOnPanel = !hidden.Contains("claude");
        _showCodexOnPanel = !hidden.Contains("codex");
        _showMiniMaxOnPanel = !hidden.Contains("minimax");
        _showDeepSeekOnPanel = !hidden.Contains("deepseek");
        _showTokenPlanOnPanel = !hidden.Contains("alibaba-tokenplan");
        _hotkeyEnabled = current.HotkeyEnabled;
        _hotkeyModifiers = current.HotkeyModifiers;
        _hotkeyKey = current.HotkeyKey;

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
        LoginTokenPlanCommand = new AsyncRelayCommand(LoginTokenPlanAsync);
        ClearTokenPlanCookieCommand = new RelayCommand(ClearTokenPlanCookie);
        ClearCacheCommand = new RelayCommand(ClearCache);
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync);
        ImportBackupCommand = new RelayCommand(ImportBackup);
        RefreshLoginStatusCommand = new RelayCommand(RefreshLoginStatus);
        SaveGeneralSettingsCommand = new RelayCommand(SaveGeneralSettings);
        ToggleRevealMiniMaxKeyCommand = new AsyncRelayCommand(ToggleRevealMiniMaxKeyAsync);
        ToggleRevealDeepSeekKeyCommand = new AsyncRelayCommand(ToggleRevealDeepSeekKeyAsync);
        AddCustomPlatformCommand = new RelayCommand(AddCustomPlatform);
        AddOpenCodeGoTemplateCommand = new RelayCommand(AddOpenCodeGoTemplate);
        EnterTrayModeCommand = new AsyncRelayCommand(_presentation.EnterTrayPopupAsync);
        EnterFloatingModeCommand = new AsyncRelayCommand(_presentation.EnterFloatingAsync);
        EnterDesktopPanelModeCommand = new AsyncRelayCommand(_presentation.EnterDesktopPanelAsync);
        RestoreDefaultWindowPositionCommand = new RelayCommand(_presentation.RestoreDefaultPosition);

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
        IsTokenPlanConfigured = !string.IsNullOrEmpty(_credentialStore.TryReadLarge(TokenPlanCookieKeyName));
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

    /// <summary>
    /// 一键登录：弹出 WebView2 登录窗口，登录成功后自动抓取登录态写入 Windows 凭据管理器，
    /// 并触发一次面板刷新（登录成功即查询额度）。
    /// </summary>
    private async Task LoginTokenPlanAsync()
    {
        if (IsTokenPlanLoggingIn)
        {
            return;
        }

        IsTokenPlanLoggingIn = true;
        try
        {
            var loginWindow = new Views.AlibabaLoginWindow { Owner = WindowForDialog() };
            var loginCompleted = new TaskCompletionSource<(bool Success, string? Cookie, string? SecToken, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnSucceeded(string cookie, string secToken) => loginCompleted.TrySetResult((true, cookie, secToken, null));
            void OnFailed(string error) => loginCompleted.TrySetResult((false, null, null, error));
            loginWindow.LoginSucceeded += OnSucceeded;
            loginWindow.LoginFailed += OnFailed;

            loginWindow.Show();
            var result = await loginCompleted.Task;

            loginWindow.LoginSucceeded -= OnSucceeded;
            loginWindow.LoginFailed -= OnFailed;

            if (result.Success && result.Cookie is { } cookie)
            {
                SaveTokenPlanCredentials(cookie, result.SecToken);
                RefreshCredentialLabels();

                var cookieCount = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
                var secTokenNote = string.IsNullOrEmpty(result.SecToken) ? "（未捕获 SEC_TOKEN，将尝试从页面提取）" : string.Empty;
                StatusMessage = $"百炼登录成功，已保存 {cookieCount} 个会话 Cookie，正在查询额度…{secTokenNote}";
                // 触发面板立即刷新，让额度卡片马上出现/更新（与"保存设置"同一套生效路径）。
                SettingsSaved?.Invoke(this, BuildSettingsSnapshot().Settings);
            }
            else
            {
                StatusMessage = result.Error ?? "已取消百炼登录";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 凭据写入失败（如 Cookie 超出凭据管理器 2560 字节上限）等异常：给出明确提示，
            // 绝不能静默崩溃——否则表现就是"登录窗口消失但额度卡片没出现"。
            StatusMessage = $"百炼登录态保存失败：{ex.Message}，请重试或在反馈时附上此提示";
        }
        finally
        {
            IsTokenPlanLoggingIn = false;
        }
    }

    /// <summary>把登录抓到的 Cookie（+ 可选 SEC_TOKEN）写入 Windows 凭据管理器。完整会话 Cookie
    /// 常年超过凭据管理器单条 2560 字节上限，用 <see cref="SecureCredentialStore.SaveLarge"/>
    /// 分片存储——不再靠精简 Cookie 内容硬凑，避免重蹈"猜哪些 Cookie 重要、猜漏了"的覆辙。
    /// SEC_TOKEN 本身很短，仍用常规 UTF-8 单条存储即可。</summary>
    private void SaveTokenPlanCredentials(string cookie, string? secToken)
    {
        _credentialStore.SaveLarge(TokenPlanCookieKeyName, cookie);
        if (!string.IsNullOrEmpty(secToken))
        {
            _credentialStore.Save(TokenPlanSecTokenKeyName, secToken, useUtf8: true);
        }
        else
        {
            _credentialStore.Delete(TokenPlanSecTokenKeyName);
        }
    }

    /// <summary>登录窗口 Owner 兜底：设置窗口自身（SettingsWindow 关闭时用主面板窗口）。</summary>
    private Window WindowForDialog() =>
        System.Windows.Application.Current.Windows
            .Cast<Window>()
            .FirstOrDefault(w => w.GetType().Name == "SettingsWindow")
        ?? System.Windows.Application.Current.MainWindow;

    private void ClearTokenPlanCookie()
    {
        _credentialStore.DeleteLarge(TokenPlanCookieKeyName);
        _credentialStore.Delete(TokenPlanSecTokenKeyName);
        RefreshCredentialLabels();
        StatusMessage = "已清除百炼登录态，重新查询前需要再次登录";
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

    /// <summary>配置、缓存、日志所在目录，直接显示给用户。</summary>
    public string DataDirectory => _settingsStore.DataDirectory;

    /// <summary>
    /// 备份包里要带上的凭据键名。
    ///
    /// Claude / Codex 不在其中：它们的登录态由各自的 CLI 自己管理，QuotaFlow 只读不存，
    /// 新机器上登录一次 CLI 即可，没有可迁移的东西。
    /// </summary>
    private IEnumerable<(string Key, bool IsLarge, bool UseUtf8)> BackupCredentialKeys()
    {
        yield return (MiniMaxKeyName, false, false);
        yield return (DeepSeekKeyName, false, false);
        yield return (TokenPlanCookieKeyName, true, false);
        yield return (TokenPlanSecTokenKeyName, false, true);

        foreach (var row in CustomPlatformRows)
        {
            yield return (row.CredentialKeyName, false, false);
        }
    }

    /// <summary>
    /// 导出加密备份包。
    ///
    /// 这是唯一一条会把密钥写进文件的路径，因此必须由用户显式发起、显式设定口令，
    /// 并在界面上把"文件含密钥、口令丢了打不开"讲清楚（见 PasswordPromptWindow 的提示文案）。
    /// 密钥只在内存里从凭据管理器读出、立即交给 AES-GCM 加密，绝不写明文、绝不进日志。
    /// </summary>
    private async Task ExportBackupAsync()
    {
        // 身份验证必须在最前面：导出比"眼睛"查看单个 Key 危险得多——眼睛只暴露一个密钥，
        // 导出是把所有 API Key 和登录态一次性打包成文件。既然查看一个 Key 都要验证身份，
        // 把全部密钥导出去更没有理由豁免。
        var verification = await _verifyIdentity("验证身份以导出包含全部密钥的备份包");
        if (verification != IdentityVerificationResult.Verified)
        {
            StatusMessage = verification == IdentityVerificationResult.Cancelled
                ? "已取消验证，未导出"
                : "验证失败，无法导出备份";
            return;
        }

        var password = Views.PasswordPromptWindow.AskNewPassword(WindowForDialog());
        if (string.IsNullOrEmpty(password))
        {
            StatusMessage = "已取消导出";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 QuotaFlow 配置备份",
            Filter = "QuotaFlow 备份 (*.qfbackup)|*.qfbackup|所有文件 (*.*)|*.*",
            FileName = $"QuotaFlow-backup-{DateTime.Now:yyyy-MM-dd}.qfbackup",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "已取消导出";
            return;
        }

        try
        {
            // 以"当前编辑态 + 磁盘基线"为准导出，跟点保存看到的内容一致。
            var payload = new BackupPayload { Settings = BuildSettingsSnapshot().Settings };

            var secretCount = 0;
            foreach (var (key, isLarge, useUtf8) in BackupCredentialKeys())
            {
                var value = isLarge ? _credentialStore.TryReadLarge(key) : _credentialStore.TryRead(key, useUtf8);
                if (!string.IsNullOrEmpty(value))
                {
                    payload.Secrets[key] = value;
                    secretCount++;
                }
            }

            File.WriteAllText(dialog.FileName, SettingsBackup.Export(payload, password));
            StatusMessage = $"已导出备份（含 {secretCount} 项密钥，已用口令加密）。请妥善保管该文件与口令。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// 导入备份包：先完整解密校验通过，再落地。口令错误或文件损坏时原有配置分毫不动。
    /// </summary>
    private void ImportBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 QuotaFlow 配置备份",
            Filter = "QuotaFlow 备份 (*.qfbackup)|*.qfbackup|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "已取消导入";
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取备份文件失败：{ex.GetType().Name}";
            return;
        }

        var password = Views.PasswordPromptWindow.AskExistingPassword(WindowForDialog());
        if (string.IsNullOrEmpty(password))
        {
            StatusMessage = "已取消导入";
            return;
        }

        var result = SettingsBackup.Import(content, password);
        if (!result.Succeeded)
        {
            StatusMessage = result.Failure switch
            {
                BackupImportFailure.NotABackupFile => "这不是 QuotaFlow 备份文件，或文件已损坏",
                BackupImportFailure.UnsupportedVersion => "备份文件来自更新版本的 QuotaFlow，请先升级本机版本",
                _ => "口令不正确，或备份文件已被修改。现有配置未做任何改动。",
            };
            return;
        }

        try
        {
            var payload = result.Payload!;

            // 先写凭据、再写设置：设置里含自定义平台定义，凭据键与其 Id 一一对应，
            // 顺序反了会出现"平台已出现但密钥还没到位"的短暂空窗。
            foreach (var (key, value) in payload.Secrets)
            {
                if (key == TokenPlanCookieKeyName)
                {
                    _credentialStore.SaveLarge(key, value);
                }
                else
                {
                    _credentialStore.Save(key, value, useUtf8: key == TokenPlanSecTokenKeyName);
                }
            }

            // WindowDisplay 是本机的窗口位置/显示模式，跟着备份跨机器搬没有意义（分辨率、
            // 显示器布局都不一样），保留本机现有值。
            var current = _settingsStore.Load();
            payload.Settings.WindowDisplay = current.WindowDisplay;
            _settingsStore.Save(payload.Settings);
            _applyHotkeySettings(payload.Settings);
            SettingsSaved?.Invoke(this, payload.Settings);

            StatusMessage = $"已导入备份（含 {payload.Secrets.Count} 项密钥）。设置已生效，重新打开设置页可看到导入后的内容。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入过程中出错：{ex.GetType().Name}，部分内容可能未生效";
        }
    }

    /// <summary>
    /// 在文件资源管理器里打开配置目录。
    ///
    /// 用 explorer.exe 显式打开而不是 <c>UseShellExecute</c> 直接开目录：后者在某些
    /// 关联被改过的环境上会被别的程序接管。失败时只提示，不抛出——这只是个便利入口。
    /// </summary>
    private void OpenDataDirectory()
    {
        try
        {
            var dir = DataDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                StatusMessage = "配置目录尚不存在（保存一次设置后会自动创建）";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开配置目录失败：{ex.GetType().Name}，可手动访问上方路径";
        }
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

    /// <summary>
    /// 由当前设置页编辑态构建一份完整 AppSettings（含自定义平台逐行校验）。"保存设置"和
    /// "百炼一键登录成功后触发面板刷新"共用这一份快照，保证两条路径对面板应用的是同一套设置。
    /// </summary>
    private (AppSettings Settings, List<string> SkippedNames) BuildSettingsSnapshot()
    {
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

        // 以磁盘上的当前设置为基线做"读-改-写"，只覆盖本页真正拥有的字段。
        //
        // 早期实现是 new AppSettings { ... } 从零构造：凡是没在初始化器里列出的字段都会取
        // 默认值，然后被 Save 整体写盘。WindowDisplay 恰好没列出来，于是点一次「保存设置」
        // 就把显示模式、不透明度、置顶、锁定位置、紧凑布局、材质、边缘吸附以及各显示器
        // 记住的窗口位置全部重置回默认值——实测可稳定复现（勾选紧凑布局 → 保存 → 重启即丢失）。
        // WindowPresentationCoordinator.Persist() 早就是"读-改-写"，两条持久化路径不对称
        // 才是根因；这里改成同样的写法，顺带让将来新增的"面板即时生效字段"默认就不会被误伤。
        var settings = _settingsStore.Load();
        settings.AutoRefreshIntervalMinutes = AutoRefreshIntervalMinutes;
        settings.RefreshOnStartup = RefreshOnStartup;
        settings.StartWithWindows = StartWithWindows;
        settings.Theme = Theme;
        settings.MiniMaxRegion = MiniMaxRegion;
        settings.ShowUnknownWindows = ShowUnknownWindows;
        settings.ClockDisplayFormat = ClockDisplayFormat;
        // 接口地址覆盖：空串转 null（= 用内置默认）。
        settings.ClaudeEndpointOverride = ToNullIfEmpty(ClaudeEndpointOverride);
        settings.CodexEndpointOverride = ToNullIfEmpty(CodexEndpointOverride);
        settings.MiniMaxEndpointOverride = ToNullIfEmpty(MiniMaxEndpointOverride);
        settings.DeepSeekEndpointOverride = ToNullIfEmpty(DeepSeekEndpointOverride);
        settings.CustomPlatforms = customPlatforms;
        // 只记"被隐藏的"，不记"显示的"：将来新增内置平台时，老配置里自然不会出现它的 id，
        // 默认就是显示，不需要迁移。
        var hiddenPlatforms = BuiltInPanelVisibility()
            .Where(p => !p.Show)
            .Select(p => p.ProviderId)
            .ToArray();
        settings.HiddenPlatforms = hiddenPlatforms.Length > 0 ? hiddenPlatforms : null;
        settings.HotkeyEnabled = HotkeyEnabled;
        settings.HotkeyModifiers = HotkeyModifiers;
        settings.HotkeyKey = HotkeyKey;
        // PlatformOrder / QuotaDisplaySemantic / WindowDisplay 都由面板即时持久化，不属于本页，
        // 基线里是什么就保留什么——从磁盘读比用打开设置页时的快照更准确：用户在设置页开着的
        // 同时调整了卡片顺序，也不会被这次保存回退。
        return (settings, skippedNames);
    }

    private void SaveGeneralSettings()
    {
        var autoStartWasEnabled = AutoStartService.IsEnabled();
        var autoStartWriteSucceeded = AutoStartService.SetEnabled(StartWithWindows);

        var (settings, skippedNames) = BuildSettingsSnapshot();
        _settingsStore.Save(settings);
        var hotkeyApplied = _applyHotkeySettings(settings);

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

        var autoStartNote = BuildAutoStartNote(autoStartWasEnabled, StartWithWindows, autoStartWriteSucceeded);
        // 快捷键注册失败通常是组合键被其它程序占用了——跟自动启动一样，"设置已保存"这句笼统提示
        // 盖不住"这个具体操作其实没生效"，必须点名。
        var hotkeyNote = HotkeyEnabled && !hotkeyApplied
            ? $"，但快捷键 {HotkeyDisplayText} 注册失败（可能被其它程序占用），请换一个组合"
            : string.Empty;

        StatusMessage = skippedNames.Count > 0
            ? $"设置已保存（以下自定义平台配置不完整未保存：{string.Join("、", skippedNames)}。请检查名称/接口地址/取值路径是否填写，自定义请求头方式还需填写请求头名称）{autoStartNote}{hotkeyNote}"
            : $"设置已保存{autoStartNote}{hotkeyNote}";
    }

    /// <summary>
    /// 拼一句开机自动启动的具体反馈，附在保存状态后面——"设置已保存"这种笼统提示不足以确认
    /// 这个具体操作是不是真的生效了（注册表写入理论上可能因权限失败）。只有状态真的发生变化，
    /// 或者写入失败时才附加这句，没变化又成功时不啰嗦。
    /// </summary>
    private static string BuildAutoStartNote(bool wasEnabled, bool requestedEnabled, bool writeSucceeded)
    {
        if (!writeSucceeded)
        {
            return requestedEnabled ? "，但开机自动启动开启失败，请检查权限后重试" : "，但开机自动启动关闭失败，请检查权限后重试";
        }

        if (wasEnabled == requestedEnabled)
        {
            return string.Empty;
        }

        return requestedEnabled ? "，开机自动启动已开启" : "，开机自动启动已关闭";
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
