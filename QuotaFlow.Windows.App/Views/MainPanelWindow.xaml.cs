using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.App.ViewModels;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 面板窗口。三种显示模式（托盘弹出/悬浮/桌面看板）共用同一个窗口实例和同一套
/// <c>WindowStyle="None" AllowsTransparency="True"</c> 边框——WPF 里这两个属性在窗口句柄创建后
/// 就不能再改（<see cref="InvalidOperationException"/>），而这个窗口在托盘应用生命周期内只
/// Show()/Hide()，从不重新创建，所以模式切换只能通过仍然可以运行期修改的属性来表达：
/// <see cref="Window.Topmost"/>、<see cref="Window.ShowInTaskbar"/>、位置/尺寸、背景层透明度。
/// 具体切换逻辑都在 <see cref="WindowPresentationCoordinator"/>，这里只暴露它需要驱动的最小接口。
/// </summary>
public partial class MainPanelWindow : Window
{
    private bool _isDragInProgress;

    public event EventHandler? SettingsRequested;

    /// <summary>一次拖动结束（松开鼠标）后触发，供协调器把当前位置存盘——不是每个像素都写，
    /// 只在拖动完成这一刻存一次。</summary>
    public event EventHandler? DragCompleted;

    /// <summary>
    /// 无参构造：组合根（App.xaml.cs）需要先有窗口实例才能构造依赖它的
    /// <see cref="WindowPresentationCoordinator"/>，而后者又是 <see cref="MainPanelViewModel"/>
    /// 的构造依赖——三者存在环形依赖，用"窗口先建、ViewModel 随后经 <see cref="AttachViewModel"/>
    /// 挂上"打破这个环，而不是引入额外的 DI 容器。
    /// </summary>
    public MainPanelWindow()
    {
        InitializeComponent();
    }

    public void AttachViewModel(MainPanelViewModel viewModel)
    {
        DataContext = viewModel;

        // "固定"按钮（ViewModel.IsPinned，v1.2.0）和显示模式（KeepVisibleOnDeactivate，本次新增）
        // 是两个独立开关，任一为 true 都不应该自动收起——固定是托盘模式下的临时会话状态，
        // KeepVisibleOnDeactivate 由 WindowPresentationCoordinator 按模式设置，两者用 OR 合并，
        // 互不覆盖对方（OnDeactivated 里体现）。
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainPanelViewModel.IsPinned))
            {
                IsPinned = viewModel.IsPinned;
            }
        };
    }

    /// <summary>
    /// True 时失焦不自动隐藏：诊断用途（--show-panel）或悬浮/桌面看板模式下由协调器设置。
    /// 正常托盘模式为 False，点击面板外部立即收起（除非 <see cref="IsPinned"/> 为 true）。
    /// </summary>
    public bool KeepVisibleOnDeactivate { get; set; }

    /// <summary>用户在面板上点了"固定"（v1.2.0，托盘模式下的会话临时状态，不持久化）。
    /// 与 <see cref="KeepVisibleOnDeactivate"/> 是 OR 关系，互不覆盖。</summary>
    public bool IsPinned { get; set; }

    /// <summary>是否允许通过头部拖动区域拖动窗口——托盘模式恒为 false，悬浮/桌面看板模式下
    /// 由协调器根据"位置锁定"设置切换。</summary>
    public bool IsDraggable { get; set; }

    /// <summary>拖动时是否吸附屏幕工作区边缘（按住 Shift 临时关闭），由协调器同步。</summary>
    public bool SnapToEdges { get; set; } = true;

    /// <summary>
    /// True 表示当前位置由 <see cref="WindowPresentationCoordinator"/> 管理（悬浮/桌面看板模式，
    /// 已按上次保存的坐标或多显示器修正算法算好了 Left/Top）——这种情况下 <see cref="ShowNearTray"/>
    /// 不应该再用"贴任务栏"的启发式覆盖它。托盘模式下为 false，走原来的贴边逻辑。
    /// </summary>
    public bool PositionManagedExternally { get; set; }

    public void ShowNearTray()
    {
        if (!PositionManagedExternally)
        {
            PositionNearTaskbar();
        }

        Show();
        Activate();
        Focus();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowNearTray();
        }
    }

    /// <summary>托盘模式的贴边定位算法，供协调器在切回 TrayPopup 时调用。</summary>
    public void PositionNearTaskbarPublic() => PositionNearTaskbar();

    private void PositionNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        const double margin = 8;

        double left, top;

        if (workArea.Bottom < screenHeight)
        {
            // 任务栏在底部（最常见情况）：面板贴右下角，出现在托盘区域上方。
            left = workArea.Right - Width - margin;
            top = workArea.Bottom - Height - margin;
        }
        else if (workArea.Right < screenWidth)
        {
            // 任务栏在右侧。
            left = workArea.Right - Width - margin;
            top = workArea.Bottom - Height - margin;
        }
        else if (workArea.Left > 0)
        {
            // 任务栏在左侧。
            left = workArea.Left + margin;
            top = workArea.Bottom - Height - margin;
        }
        else
        {
            // 任务栏在顶部。
            left = workArea.Right - Width - margin;
            top = workArea.Top + margin;
        }

        Left = left;
        Top = top;
    }

    /// <summary>
    /// 只调整背景层的透明度（<c>PanelBackgroundLayer</c>，见 XAML），不是整个 <see cref="Window.Opacity"/>——
    /// 后者会把文字、图标、进度条一起变淡，可读性会跟着透明度一起下降，文档 §8.2 明确要求避免这个问题。
    /// </summary>
    public void ApplyBackgroundOpacity(double opacity)
    {
        if (PanelBackgroundLayer is not null)
        {
            PanelBackgroundLayer.Opacity = opacity;
        }
    }

    /// <summary>头部拖动区域（标题文字所在的那一条）的按下事件——只有这块区域能触发拖动，
    /// 刷新/设置/模式切换等按钮都是它的兄弟元素，不会被这个处理器影响。</summary>
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDraggable || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _isDragInProgress = true;
        LocationChanged += OnLocationChangedDuringDrag;
        try
        {
            // WPF 正确的窗口拖动机制——不是靠 MouseMove 里手动改 Left/Top 模拟拖动，
            // DragMove() 内部走的是标准的 WM_NCLBUTTONDOWN + 系统移动循环。
            DragMove();
        }
        finally
        {
            LocationChanged -= OnLocationChangedDuringDrag;
            _isDragInProgress = false;
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 拖动过程中的实时吸附反馈：DragMove() 本身是阻塞调用，但 LocationChanged 仍然会随着系统的
    /// 移动循环持续触发，可以在这里叠加吸附逻辑，而不需要放弃 DragMove() 改用手动拖动。
    /// </summary>
    private void OnLocationChangedDuringDrag(object? sender, EventArgs e)
    {
        if (!_isDragInProgress || !SnapToEdges || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var monitors = MonitorService.GetAllMonitors();
        var current = WindowPlacementCalculator.FindNearestMonitor(monitors, Left + Width / 2, Top + Height / 2);
        if (current is null)
        {
            return;
        }

        const double threshold = 16;
        var newLeft = Left;
        var newTop = Top;

        if (Math.Abs(Left - current.WorkAreaLeft) <= threshold)
        {
            newLeft = current.WorkAreaLeft;
        }
        else if (Math.Abs(Left + Width - (current.WorkAreaLeft + current.WorkAreaWidth)) <= threshold)
        {
            newLeft = current.WorkAreaLeft + current.WorkAreaWidth - Width;
        }

        if (Math.Abs(Top - current.WorkAreaTop) <= threshold)
        {
            newTop = current.WorkAreaTop;
        }
        else if (Math.Abs(Top + Height - (current.WorkAreaTop + current.WorkAreaHeight)) <= threshold)
        {
            newTop = current.WorkAreaTop + current.WorkAreaHeight - Height;
        }

        if (Math.Abs(newLeft - Left) > 0.01 || Math.Abs(newTop - Top) > 0.01)
        {
            Left = newLeft;
            Top = newTop;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!KeepVisibleOnDeactivate && !IsPinned)
        {
            Hide();
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 文档 §13：Esc 只在托盘面板模式收起窗口；悬浮/桌面看板模式下不隐藏主窗口。
        if (e.Key == Key.Escape && DataContext is MainPanelViewModel vm &&
            vm.CurrentMode == Core.Models.WindowPresentationMode.TrayPopup)
        {
            Hide();
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);
}
