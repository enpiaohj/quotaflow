using QuotaFlow.Windows.App.Views;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// <see cref="IWindowPresentationCoordinator"/> 的具体实现，操作 <see cref="MainPanelWindow"/>。
/// 只持有一份内存中的 <see cref="WindowDisplaySettings"/>（构造时从 <see cref="AppSettings"/> 读入），
/// 每次状态变化后整体重新写回 <see cref="AppSettingsStore"/>——跟这个项目里其它"面板自己改、立即
/// 持久化"的设置（卡片顺序、已用/剩余语义）走同一套模式，不新增额外的保存触发路径。
/// </summary>
public sealed class WindowPresentationCoordinator : IWindowPresentationCoordinator
{
    private const double FloatingDefaultWidth = 420;
    private const double FloatingDefaultHeight = 560;
    private const double DesktopDefaultWidth = 320;
    private const double DesktopDefaultHeight = 260;

    private readonly MainPanelWindow _window;
    private readonly AppSettingsStore _settingsStore;
    private readonly Func<AppSettings> _loadFullSettings;
    private readonly Action<AppSettings> _persistFullSettings;
    private readonly WindowDisplaySettings _state;

    /// <summary>防止快速连续点击造成重入——上一次 Enter*Async 还没跑完时，新的请求直接忽略。</summary>
    private bool _transitioning;

    public WindowPresentationMode CurrentMode => _state.Mode;
    public bool IsAlwaysOnTop => _state.IsAlwaysOnTop;
    public bool IsPositionLocked => _state.IsPositionLocked;
    public bool IsCompactLayout => _state.IsCompactLayout;
    public double Opacity => _state.Opacity;
    public WindowMaterial Material => _state.Material;
    public bool SnapToEdges => _state.SnapToEdges;
    public bool RestoreLastModeOnStartup => _state.RestoreLastModeOnStartup;
    public bool EnhanceReadabilityOnHover => _state.EnhanceReadabilityOnHover;

    public event EventHandler? StateChanged;

    /// <param name="window">要驱动的面板窗口。</param>
    /// <param name="settingsStore">用于整体持久化 AppSettings。</param>
    /// <param name="loadFullSettings">拿到当前完整 AppSettings 的委托（避免这个协调器自己持有一份
    /// 可能过期的副本——跟 MainPanelViewModel 里 _settings 字段的更新时机保持一致，由调用方负责
    /// 传入"当下最新"的整份设置）。</param>
    /// <param name="persistFullSettings">写回完整 AppSettings 的委托，只替换 WindowDisplay 字段。</param>
    public WindowPresentationCoordinator(
        MainPanelWindow window,
        AppSettingsStore settingsStore,
        Func<AppSettings> loadFullSettings,
        Action<AppSettings> persistFullSettings)
    {
        _window = window;
        _settingsStore = settingsStore;
        _loadFullSettings = loadFullSettings;
        _persistFullSettings = persistFullSettings;
        _state = CloneState(_loadFullSettings().WindowDisplay);

        // DwmSetWindowAttribute 需要真实的窗口句柄，而 RestoreLastModeAsync 在启动时可能早于
        // 窗口第一次 Show()（例如 --autostart 静默启动）被调用——那时 ApplyOpacityAndMaterial()
        // 里的 WindowMaterialService.TryApply 会因为句柄还不存在而静默失败。SourceInitialized
        // 只在句柄真正创建的那一刻触发一次，这里补一次材质应用，覆盖"设置生效但窗口还没显示过"
        // 这个窗口期，不需要调用方（App.xaml.cs）关心这个时序细节。
        _window.SourceInitialized += (_, _) => ApplyOpacityAndMaterial();

        // "悬停时临时提高不透明度"：只影响背景层，鼠标移开后用 ApplyOpacityAndMaterial() 恢复到
        // 当前模式/状态该有的值，不需要另外记一份"悬停前的透明度"——它本来就是状态的函数。
        // 托盘弹出模式恒定不透明，不需要这个效果，直接跳过。
        _window.MouseEnter += (_, _) =>
        {
            if (_state.EnhanceReadabilityOnHover && _state.Mode != WindowPresentationMode.TrayPopup)
            {
                _window.ApplyBackgroundOpacity(1.0);
            }
        };
        _window.MouseLeave += (_, _) => ApplyOpacityAndMaterial();
    }

    private static WindowDisplaySettings CloneState(WindowDisplaySettings source) => new()
    {
        Mode = source.Mode,
        IsAlwaysOnTop = source.IsAlwaysOnTop,
        IsPositionLocked = source.IsPositionLocked,
        IsCompactLayout = source.IsCompactLayout,
        Opacity = source.Opacity,
        Material = source.Material,
        SnapToEdges = source.SnapToEdges,
        RestoreLastModeOnStartup = source.RestoreLastModeOnStartup,
        EnhanceReadabilityOnHover = source.EnhanceReadabilityOnHover,
        FloatingPlacement = source.FloatingPlacement,
        DesktopPlacement = source.DesktopPlacement,
    };

    public async Task EnterTrayPopupAsync() => await TransitionAsync(WindowPresentationMode.TrayPopup);

    public async Task EnterFloatingAsync() => await TransitionAsync(WindowPresentationMode.Floating);

    public async Task EnterDesktopPanelAsync() => await TransitionAsync(WindowPresentationMode.DesktopPanel);

    public async Task RestoreLastModeAsync()
    {
        var target = _state.RestoreLastModeOnStartup ? _state.Mode : WindowPresentationMode.TrayPopup;
        await TransitionAsync(target, forceReapply: true);
    }

    private Task TransitionAsync(WindowPresentationMode target, bool forceReapply = false)
    {
        if (_transitioning)
        {
            return Task.CompletedTask; // 上一次切换还没结束，丢弃这次重复点击。
        }

        if (!forceReapply && _state.Mode == target)
        {
            return Task.CompletedTask; // 已经在目标模式，幂等直接返回，不重复做一遍。
        }

        _transitioning = true;
        try
        {
            SavePreviousModePlacement();
            _state.Mode = target;

            switch (target)
            {
                case WindowPresentationMode.TrayPopup:
                    ApplyTrayPopup();
                    break;
                case WindowPresentationMode.Floating:
                    ApplyFloating();
                    break;
                case WindowPresentationMode.DesktopPanel:
                    ApplyDesktopPanel();
                    break;
            }

            Persist();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _transitioning = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>离开 Floating/DesktopPanel 前，把窗口当前的真实位置存回对应字段——用户可能拖动过
    /// 但还没触发过 PersistCurrentPlacement（例如直接点了模式切换按钮，而不是先松开鼠标）。</summary>
    private void SavePreviousModePlacement()
    {
        if (_state.Mode == WindowPresentationMode.Floating)
        {
            _state.FloatingPlacement = CaptureCurrentPlacement();
        }
        else if (_state.Mode == WindowPresentationMode.DesktopPanel)
        {
            _state.DesktopPlacement = CaptureCurrentPlacement();
        }
    }

    private SavedWindowPlacement CaptureCurrentPlacement() => new()
    {
        MonitorDeviceName = MonitorService.GetCurrentMonitorDeviceName(_window),
        LeftDip = _window.Left,
        TopDip = _window.Top,
        WidthDip = _window.Width,
        HeightDip = _window.Height,
        SavedDpiX = MonitorService.GetDpiForWindow(_window).DpiX,
        SavedDpiY = MonitorService.GetDpiForWindow(_window).DpiY,
        LastUpdatedAt = DateTimeOffset.UtcNow,
    };

    private void ApplyTrayPopup()
    {
        _window.KeepVisibleOnDeactivate = false;
        _window.Topmost = true; // 托盘面板始终置顶，不受 IsAlwaysOnTop 设置影响（该设置只作用于 Floating/DesktopPanel）。
        _window.ShowInTaskbar = false;
        _window.IsDraggable = false;
        _window.PositionManagedExternally = false;
        _window.Width = 420;
        _window.Height = 560;
        _window.PositionNearTaskbarPublic();
        ApplyOpacityAndMaterial();
    }

    private void ApplyFloating()
    {
        var isFirstEntry = _state.FloatingPlacement is null;
        if (isFirstEntry)
        {
            // 推荐默认值（文档 §4.2）：只在第一次进入这个模式时套用，之后用户自己的调整不会被覆盖。
            _state.IsAlwaysOnTop = true;
            _state.IsCompactLayout = false;
            _state.Opacity = 0.95;
        }

        var monitors = MonitorService.GetAllMonitors();
        var placement = WindowPlacementCalculator.Correct(_state.FloatingPlacement, monitors, FloatingDefaultWidth, FloatingDefaultHeight);
        _state.FloatingPlacement = placement;

        _window.KeepVisibleOnDeactivate = true; // 悬浮模式点击外部不关闭。
        _window.Topmost = _state.IsAlwaysOnTop;
        _window.ShowInTaskbar = false;
        _window.IsDraggable = !_state.IsPositionLocked;
        _window.SnapToEdges = _state.SnapToEdges;
        _window.PositionManagedExternally = true;
        ApplyPlacement(placement);
        ApplyOpacityAndMaterial();
    }

    private void ApplyDesktopPanel()
    {
        var isFirstEntry = _state.DesktopPlacement is null;
        if (isFirstEntry)
        {
            _state.IsAlwaysOnTop = false;
            _state.IsCompactLayout = true;
            _state.Opacity = 0.85;
        }

        var monitors = MonitorService.GetAllMonitors();
        var placement = WindowPlacementCalculator.Correct(_state.DesktopPlacement, monitors, DesktopDefaultWidth, DesktopDefaultHeight);
        _state.DesktopPlacement = placement;

        _window.KeepVisibleOnDeactivate = true; // 桌面看板点击外部不关闭。
        _window.Topmost = _state.IsAlwaysOnTop;
        _window.ShowInTaskbar = false;
        _window.IsDraggable = !_state.IsPositionLocked;
        _window.SnapToEdges = _state.SnapToEdges;
        _window.PositionManagedExternally = true;
        ApplyPlacement(placement);
        ApplyOpacityAndMaterial();
    }

    private void ApplyPlacement(SavedWindowPlacement placement)
    {
        _window.Left = placement.LeftDip;
        _window.Top = placement.TopDip;
        _window.Width = placement.WidthDip;
        _window.Height = placement.HeightDip;
    }

    private void ApplyOpacityAndMaterial()
    {
        _window.ApplyBackgroundOpacity(_state.Mode == WindowPresentationMode.TrayPopup ? 1.0 : _state.Opacity);
        WindowMaterialService.TryApply(_window, _state.Mode == WindowPresentationMode.TrayPopup ? WindowMaterial.Solid : _state.Material);
    }

    public void SetAlwaysOnTop(bool value)
    {
        _state.IsAlwaysOnTop = value;
        if (_state.Mode != WindowPresentationMode.TrayPopup)
        {
            _window.Topmost = value;
        }

        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPositionLocked(bool value)
    {
        _state.IsPositionLocked = value;
        _window.IsDraggable = !value && _state.Mode != WindowPresentationMode.TrayPopup;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCompactLayout(bool value)
    {
        _state.IsCompactLayout = value;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetOpacity(double value)
    {
        _state.Opacity = Math.Clamp(value, 0.7, 1.0);
        ApplyOpacityAndMaterial();
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMaterial(WindowMaterial value)
    {
        _state.Material = value;
        ApplyOpacityAndMaterial();
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSnapToEdges(bool value)
    {
        _state.SnapToEdges = value;
        _window.SnapToEdges = value;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRestoreLastModeOnStartup(bool value)
    {
        _state.RestoreLastModeOnStartup = value;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnhanceReadabilityOnHover(bool value)
    {
        _state.EnhanceReadabilityOnHover = value;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreDefaultPosition()
    {
        if (_state.Mode == WindowPresentationMode.Floating)
        {
            _state.FloatingPlacement = null;
            ApplyFloating();
        }
        else if (_state.Mode == WindowPresentationMode.DesktopPanel)
        {
            _state.DesktopPlacement = null;
            ApplyDesktopPanel();
        }

        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PersistCurrentPlacement()
    {
        if (_state.Mode == WindowPresentationMode.Floating)
        {
            _state.FloatingPlacement = CaptureCurrentPlacement();
        }
        else if (_state.Mode == WindowPresentationMode.DesktopPanel)
        {
            _state.DesktopPlacement = CaptureCurrentPlacement();
        }
        else
        {
            return; // 托盘模式不保存自由坐标（文档 §3.1）。
        }

        Persist();
    }

    private void Persist()
    {
        var settings = _loadFullSettings();
        settings.WindowDisplay = CloneState(_state);
        _persistFullSettings(settings);
    }
}
