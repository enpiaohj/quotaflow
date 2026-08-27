using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.App.ViewModels;
using QuotaFlow.Windows.App.Views;
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
    private readonly ThemeManager _themeManager = new();
    private AppSettingsStore _settingsStore = null!;
    private SecureCredentialStore _credentialStore = null!;
    private LocalCache _cache = null!;
    private HttpClient _httpClient = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _credentialStore = new SecureCredentialStore();
        _cache = new LocalCache();
        _settingsStore = new AppSettingsStore();

        var settings = _settingsStore.Load();
        _themeManager.Apply(settings.Theme);
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        var providers = BuildProviders(settings.MiniMaxRegion);
        var coordinator = new RefreshCoordinator(providers);

        _panelViewModel = new MainPanelViewModel(coordinator, _cache, settings);
        _panelViewModel.SettingsRequested += (_, _) => OpenSettings();
        _panelViewModel.ExitRequested += (_, _) => Shutdown();

        _panelWindow = new MainPanelWindow(_panelViewModel);
        _panelWindow.SettingsRequested += (_, _) => OpenSettings();

        SetupTrayIcon();

        // 启动后先展示缓存（已在 MainPanelViewModel 构造函数里完成），再决定是否立即后台刷新。
        _ = _panelViewModel.RefreshOnStartupIfEnabledAsync();
    }

    private IEnumerable<IQuotaProvider> BuildProviders(Core.Models.MiniMaxRegion region)
    {
        var minimaxDomain = region == Core.Models.MiniMaxRegion.International
            ? MiniMaxQuotaProvider.DomainIntl
            : MiniMaxQuotaProvider.DomainCn;

        yield return new ClaudeQuotaProvider(_httpClient);
        yield return new CodexQuotaProvider(_httpClient);
        yield return new MiniMaxQuotaProvider(_httpClient, () => _credentialStore.TryRead(SettingsViewModel.MiniMaxKeyName), minimaxDomain);
        yield return new DeepSeekBalanceProvider(_httpClient, () => _credentialStore.TryRead(SettingsViewModel.DeepSeekKeyName));
    }

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
        menu.Items.Add("打开面板", null, (_, _) => _panelWindow?.ShowNearTray());
        menu.Items.Add("立即刷新", null, (_, _) => _ = _panelViewModel?.RefreshAllAsync());
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _panelWindow?.ToggleVisibility();
            }
        };
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
        var settingsViewModel = new SettingsViewModel(_settingsStore, _credentialStore, _cache, currentSettings);
        settingsViewModel.SettingsSaved += (_, newSettings) =>
        {
            _themeManager.Apply(newSettings.Theme);
            _panelViewModel?.UpdateSettings(newSettings);
        };

        _settingsWindow = new SettingsWindow(settingsViewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
