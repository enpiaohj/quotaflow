using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.App.ViewModels;
using QuotaFlow.Windows.App.Views;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Core.Services;
using Application = System.Windows.Application;

namespace QuotaFlow.Windows.App;

/// <summary>
/// 组合根：这里是唯一一处把 HttpClient、四个 Provider、RefreshCoordinator、LocalCache、
/// SecureCredentialStore 拼在一起的地方。没有用完整 DI 容器——托盘应用的对象图很浅，
/// 手动组装比引入容器框架更符合"轻量"的定位。
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private IntPtr _trayIconHandle;
    private MainPanelWindow? _panelWindow;
    private MainPanelViewModel? _panelViewModel;
    private IWindowPresentationCoordinator _presentation = null!;
    private readonly ThemeManager _themeManager = new();
    private AppSettingsStore _settingsStore = null!;
    private SecureCredentialStore _credentialStore = null!;
    private LocalCache _cache = null!;
    private HttpClient _httpClient = null!;

    /// <summary>当前生效的代理设置，由 <see cref="ConfigurableProxy"/> 每次请求时读取。</summary>
    private (ProxyMode Mode, string? Address) _proxySettings;
    private System.Windows.Forms.Timer? _trayClickDebounceTimer;
    private bool _trayPendingSingleClick;
    private readonly GlobalHotkeyService _hotkeyService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 托盘常驻应用的硬性要求：任何一个窗口/绑定/第三方钩子（输入法、Shell 扩展……）抛出的
        // 未处理异常都不能把整个进程带崩——那样用户会觉得"点一下设置，托盘图标就消失了"。
        // 异常会记到 %LOCALAPPDATA%\QuotaFlow\crash.log，方便事后诊断，但绝不终止进程。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        _credentialStore = new SecureCredentialStore();
        _cache = new LocalCache();
        _settingsStore = new AppSettingsStore();

        var settings = _settingsStore.Load();

        // 代理走可配置实现：每次请求现查当前设置，所以在设置页切换后立即生效，不必重建
        // HttpClient（重建会波及所有 Provider 持有的引用，还要处理在途请求与旧 handler 的释放）。
        // _proxySettings 在设置保存时更新，见下方 SettingsSaved 处理。
        _proxySettings = (settings.ProxyMode, settings.ProxyAddress);
        var proxy = new ConfigurableProxy(() => _proxySettings);
        _httpClient = new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        // 网络类错误提示带上实际使用的代理——代理配错时"请检查网络连接"会把人引向错误方向。
        HttpErrorClassifier.ProxyDescriber = () => proxy.DescribeFor(new Uri("https://api.anthropic.com"));
        _themeManager.Apply(settings.Theme);
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        var providers = BuildProviders(settings);
        var coordinator = new RefreshCoordinator(providers);

        // 三者环形依赖（窗口需要 ViewModel、ViewModel 需要显示模式协调器、协调器需要窗口）
        // 用"窗口先建、ViewModel 随后经 AttachViewModel 挂上"打破，详见 MainPanelWindow 的
        // 无参构造函数上的说明。
        _panelWindow = new MainPanelWindow();
        _presentation = new WindowPresentationCoordinator(_panelWindow, _settingsStore,
            loadFullSettings: () => _settingsStore.Load(),
            persistFullSettings: s => _settingsStore.Save(s));

        _panelViewModel = new MainPanelViewModel(coordinator, _cache, _settingsStore, settings, BuildProviders, _presentation);
        _panelViewModel.SettingsRequested += (_, _) => OpenSettings();
        _panelViewModel.ExitRequested += (_, _) => Shutdown();

        _panelWindow.AttachViewModel(_panelViewModel);
        _panelWindow.SettingsRequested += (_, _) => OpenSettings();
        // 拖动结束（松开鼠标）后存盘一次——不是每个像素都写，只在这个时机存。
        _panelWindow.DragCompleted += (_, _) => _presentation.PersistCurrentPlacement();

        // 按设置里"启动后恢复上次模式"决定这次用什么模式初始化窗口属性（只重新配置窗口，
        // 不改变可见性——RestoreLastModeAsync 本身是同步实现的 Task.CompletedTask 包装，
        // 这里安全地阻塞等待，不会真的产生异步让步或死锁）。必须在下面的展示分支之前调用，
        // 否则 TrayPopup 的默认配置会覆盖掉这里恢复出来的悬浮/桌面看板位置。
        //
        // 显式 try/catch 兜底（而不是依赖 DispatcherUnhandledException）：这里抛出的异常
        // 发生在 OnStartup 同步执行期间，一旦向上抛出会中断 OnStartup 本身——后面的
        // SetupTrayIcon()/全局快捷键注册/启动时刷新全部不会执行，进程还活着但托盘图标
        // 压根没创建过，表现成"看进程在运行，但完全找不到应用"。实测事故：某些虚拟/远程桌面
        // 显示驱动报告的 DPI 是 0，导致窗口位置计算出 NaN/Infinity，写配置文件时崩在这里。
        // 那个根因已经修了，这里额外加一层兜底，防止显示模式这个子系统未来任何新问题
        // 再次连累托盘图标——托盘应用"图标必须出现"这条比其它任何子系统都优先级更高。
        try
        {
            _presentation.RestoreLastModeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }

        // 诊断/自检：--show-panel 启动后直接展示面板且失焦不自动收起。
        var isDiagnosticShow = Array.IndexOf(e.Args, "--show-panel") >= 0;
        // 开机自动启动（设置页"开机自动启动"写进 Run 注册表项时会带上这个参数，见
        // AutoStartService.SetEnabled）——这种启动不是用户主动点开的，不该突然弹一个窗口。
        var isAutoStartLaunch = Array.IndexOf(e.Args, "--autostart") >= 0;

        if (isDiagnosticShow)
        {
            _panelWindow.KeepVisibleOnDeactivate = true;
            _panelWindow.ShowNearTray();
        }
        else if (!isAutoStartLaunch)
        {
            // 用户手动启动（双击 exe、开始菜单、桌面快捷方式……）：主动展示一次面板，给一个
            // "确实打开了"的直接反馈——不然托盘常驻应用启动后界面上什么反应都没有，容易被当成
            // "没装上/没启动"（尤其图标默认还可能被 Windows 折叠进溢出区）。每次手动启动都展示，
            // 不是只展示一次；行为和点击托盘图标打开完全一样，点击外部照常自动收起，不强留。
            _panelWindow.ShowNearTray();
        }

        SetupTrayIcon();

        // 全局快捷键（默认 Alt+Z）：显示/隐藏面板，跟托盘单击同一个动作。绑定不需要面板已经
        // 显示过——AttachTo 会强制创建它的原生窗口句柄。注册失败（组合键被其它程序占用）时
        // 静默跳过，不弹错误——用户在设置页主动改快捷键时才需要看到"注册失败"这种反馈；
        // 启动时的注册失败更适合"安静地不生效"，而不是一开机就弹一个技术性错误。
        _hotkeyService.AttachTo(_panelWindow);
        _hotkeyService.HotkeyPressed += () => _panelWindow?.ToggleVisibility();
        ApplyHotkeySettings(settings);

        // 启动后先展示缓存（已在 MainPanelViewModel 构造函数里完成），再决定是否立即后台刷新。
        _ = _panelViewModel.RefreshOnStartupIfEnabledAsync();
    }

    /// <summary>
    /// 按当前设置注册（或反注册）全局热键。启动时、以及每次设置页保存后都会调用——统一走
    /// 这一个方法，不需要调用方关心"这次改没改"，反注册旧的、注册新的都是幂等操作。
    /// </summary>
    /// <returns>启用了但注册失败时返回 false（多半是组合键被占用）；未启用或注册成功都返回 true。</returns>
    private bool ApplyHotkeySettings(Core.Models.AppSettings settings)
    {
        if (!settings.HotkeyEnabled)
        {
            _hotkeyService.Unregister();
            return true;
        }

        return _hotkeyService.Register(settings.HotkeyModifiers, settings.HotkeyKey);
    }

    private IEnumerable<IQuotaProvider> BuildProviders(Core.Models.AppSettings settings)
    {
        var minimaxDomain = settings.MiniMaxRegion == Core.Models.MiniMaxRegion.International
            ? MiniMaxQuotaProvider.DomainIntl
            : MiniMaxQuotaProvider.DomainCn;

        // 未知额度窗口是否展示由设置决定；每次查询时现读设置文件，改完设置不用重启立即生效。
        // 接口地址覆盖在启动时捕获一次（与 MiniMax 区域一致：改完设置重启生效）。
        yield return new ClaudeQuotaProvider(_httpClient,
            includeUnknownWindows: () => _settingsStore.Load().ShowUnknownWindows,
            endpointOverride: settings.ClaudeEndpointOverride);
        yield return new CodexQuotaProvider(_httpClient, endpointOverride: settings.CodexEndpointOverride);
        yield return new MiniMaxQuotaProvider(_httpClient,
            () => _credentialStore.TryRead(SettingsViewModel.MiniMaxKeyName),
            minimaxDomain, settings.MiniMaxEndpointOverride);
        yield return new DeepSeekBalanceProvider(_httpClient,
            () => _credentialStore.TryRead(SettingsViewModel.DeepSeekKeyName),
            settings.DeepSeekEndpointOverride);

        // 阿里云百炼 Token Plan（个人版）——测试版本；凭据是 Console Cookie（+ 登录时抓的
        // SEC_TOKEN），只存 Windows 凭据管理器（SecureCredentialStore），与其它平台同规矩：
        // 不落盘、不写日志。Cookie 完整保留（不按域名精简）常年超过单条凭据 2560 字节上限，
        // 走分片存储（TryReadLarge）；SEC_TOKEN 很短，仍是常规 UTF-8 单条存取。
        yield return new AlibabaTokenPlanQuotaProvider(_httpClient,
            () => _credentialStore.TryReadLarge(SettingsViewModel.TokenPlanCookieKeyName),
            () => _credentialStore.TryRead(SettingsViewModel.TokenPlanSecTokenKeyName, useUtf8: true));

        // 设置页手动添加的自定义平台（OpenCode GO 等）。Id 固定为 custom-{n}，凭据键由此派生；
        // 防御性跳过配置残缺（Id/地址为空、撞内置 Id、重复自定义 Id、一个额度窗口都没有）的条目，
        // 不会让一条坏配置拖垮其余平台。
        // 注意：QuotaWindows 为空不代表"v1.0.5 旧格式尚未迁移"——AppSettingsStore.Load() 已经在
        // 读盘时就地完成了迁移，这里读到的 settings 永远是迁移后的新格式；空列表只可能是配置文件
        // 被手改坏，理应跳过。
        var builtInIds = new HashSet<string> { "claude", "codex", "minimax", "deepseek" };
        var seenIds = new HashSet<string>();
        foreach (var custom in settings.CustomPlatforms ?? [])
        {
            if (string.IsNullOrWhiteSpace(custom.Id) ||
                builtInIds.Contains(custom.Id) ||
                !seenIds.Add(custom.Id) ||
                string.IsNullOrWhiteSpace(custom.Endpoint) ||
                custom.QuotaWindows.Count == 0)
            {
                continue;
            }

            var keyName = custom.CredentialKeyName;
            yield return new CustomPlatformProvider(_httpClient, custom, () => _credentialStore.TryRead(keyName));
        }
    }

    /// <summary>
    /// 托盘交互：单击开关面板、双击打开并立即刷新、中键刷新全部、右键菜单（刷新/设置/
    /// 开机自动启动/退出）。
    ///
    /// 单击/双击消歧是这里唯一有陷阱的地方：WinForms 的双击手势本身会先连续触发两次
    /// MouseClick，再触发一次 MouseDoubleClick——如果单击直接切换显示，双击点两下会先把
    /// 面板"开了又关"，紧接着 MouseDoubleClick 处理器还要再抢一次状态，观感很怪、状态也可能对不上。
    /// 标准解法是把单击动作延后 <see cref="SystemInformation.DoubleClickTime"/>：这段时间内
    /// 如果第二次点击到达（判定为双击），就取消这次单击、改走双击逻辑（强制展示 + 刷新，
    /// 而不是 toggle，这样不管单击的两次残留状态是什么，最终结果都对）。
    /// </summary>
    private void SetupTrayIcon()
    {
        var (icon, handle) = TrayIconFactory.Create();
        _trayIconHandle = handle;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "QuotaFlow",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("刷新全部", null, (_, _) => _ = _panelViewModel?.RefreshAllAsync());
        menu.Items.Add("设置", null, (_, _) => OpenSettings());

        var autoStartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        autoStartItem.Click += (_, _) =>
        {
            // CheckOnClick 已经在 Click 触发前把 Checked 翻到了目标值，直接读它当作"想要的状态"。
            var ok = AutoStartService.SetEnabled(autoStartItem.Checked);
            if (!ok)
            {
                // 写注册表失败：把复选框状态改回真实情况，不能让菜单显示的状态跟实际生效状态不一致。
                autoStartItem.Checked = AutoStartService.IsEnabled();
                ShowTrayBalloon("开机自动启动设置失败", "请检查权限后重试", ToolTipIcon.Warning);
            }
            else
            {
                ShowTrayBalloon("QuotaFlow", autoStartItem.Checked ? "已开启开机自动启动" : "已关闭开机自动启动", ToolTipIcon.Info);
            }
        };
        menu.Items.Add(autoStartItem);
        // 菜单每次弹出前都从注册表现读一次真实状态——设置页那边也能改这个开关，两处入口要保持同步。
        menu.Opening += (_, _) => autoStartItem.Checked = AutoStartService.IsEnabled();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _notifyIcon.ContextMenuStrip = menu;

        _trayClickDebounceTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        _trayClickDebounceTimer.Tick += (_, _) =>
        {
            _trayClickDebounceTimer!.Stop();
            if (_trayPendingSingleClick)
            {
                _trayPendingSingleClick = false;
                _panelWindow?.ToggleVisibility();
            }
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _trayPendingSingleClick = true;
                _trayClickDebounceTimer!.Stop();
                _trayClickDebounceTimer.Start();
            }
            else if (args.Button == MouseButtons.Middle)
            {
                _ = _panelViewModel?.RefreshAllAsync();
            }
        };

        _notifyIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button != MouseButtons.Left)
            {
                return;
            }

            _trayClickDebounceTimer!.Stop();
            _trayPendingSingleClick = false;
            // 强制展示（不是 toggle）：不管前面两次残留的单击状态是开是关，双击的语义都是
            // "打开并立即刷新"，展示动作必须是确定性的。
            _panelWindow?.ShowNearTray();
            _ = _panelViewModel?.RefreshAllAsync();
        };
    }

    private void ShowTrayBalloon(string title, string text, ToolTipIcon icon)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var currentSettings = _settingsStore.Load();
        var settingsViewModel = new SettingsViewModel(_settingsStore, _credentialStore, _cache, currentSettings,
            _presentation, applyHotkeySettings: ApplyHotkeySettings);
        settingsViewModel.SettingsSaved += (_, newSettings) =>
        {
            _themeManager.Apply(newSettings.Theme);
            // 代理设置立即生效：ConfigurableProxy 每次请求都读这个字段，无需重建 HttpClient。
            _proxySettings = (newSettings.ProxyMode, newSettings.ProxyAddress);
            _panelViewModel?.UpdateSettings(newSettings);
            // 保存后立即刷新：改顺序、清除/新增 Key、开关键等立即反映到面板，不用等下一个自动刷新周期。
            _ = _panelViewModel?.RefreshAllAsync();
        };

        settingsViewModel.StartClockPreviewTimer();
        _settingsWindow = new SettingsWindow(settingsViewModel);
        _settingsWindow.Closed += (_, _) =>
        {
            settingsViewModel.StopClockPreviewTimer();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true; // 吞掉异常，UI 线程继续跑，托盘图标不会消失。
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// 记录异常类型/消息/堆栈，不包含请求体或凭据（这些异常来自 UI 层，正常不会携带密钥；
    /// Provider 内部的网络异常早已在 Core 里被分类成 ProviderSnapshot，不会走到这里）。
    /// </summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaFlow");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");

            // 单文件轮转：超过上限就把当前文件改名成 .1（覆盖上一份），重新从空文件开始写。
            // 这样最多占用 2×上限，且最近一段历史仍然留得住——反复触发同一个异常时不会把
            // 磁盘慢慢写满，也不至于为了限制体积把刚发生的现场直接丢掉。
            const long maxBytes = 1024 * 1024;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(path, line);
        }
        catch
        {
            // 连日志都写不进去就算了，绝不能因为记日志本身又抛一次异常。
        }
    }

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            _themeManager.ReapplyIfFollowingSystem();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _panelViewModel?.Dispose();
        _hotkeyService.Dispose();
        _trayClickDebounceTimer?.Stop();
        _trayClickDebounceTimer?.Dispose();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        TrayIconFactory.Destroy(_trayIconHandle);
        _httpClient?.Dispose();

        base.OnExit(e);
    }
}
