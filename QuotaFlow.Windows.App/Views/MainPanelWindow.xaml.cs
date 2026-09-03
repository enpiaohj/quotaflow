using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.App.ViewModels;
using QuotaFlow.Windows.Core.Models;
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

    /// <summary>桌面看板悬停工具栏的"移开后延迟收起"计时器——鼠标移开不是立刻淡出，等
    /// <see cref="ToolbarHideDelay"/> 之后如果鼠标仍不在窗口内才开始淡出（文档 §10.2，
    /// 避免"划过就消失"的频繁闪烁）。</summary>
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    private static readonly Duration ToolbarFadeInDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration ToolbarFadeOutDuration = new(TimeSpan.FromMilliseconds(180));

    public event EventHandler? SettingsRequested;

    /// <summary>一次拖动结束（松开鼠标）后触发，供协调器把当前位置存盘——不是每个像素都写，
    /// 只在拖动完成这一刻存一次。</summary>
    public event EventHandler? DragCompleted;

    /// <summary>用户手动拖拽边框改变了窗口尺寸（不是程序设置的）。协调器据此把当前模式标记为
    /// "尺寸由用户决定"，之后不再自动跟内容伸缩。</summary>
    public event EventHandler? UserResized;

    /// <summary>
    /// 无参构造：组合根（App.xaml.cs）需要先有窗口实例才能构造依赖它的
    /// <see cref="WindowPresentationCoordinator"/>，而后者又是 <see cref="MainPanelViewModel"/>
    /// 的构造依赖——三者存在环形依赖，用"窗口先建、ViewModel 随后经 <see cref="AttachViewModel"/>
    /// 挂上"打破这个环，而不是引入额外的 DI 容器。
    /// </summary>
    /// <summary>为 true 时 <see cref="OnSizeChangedInternal"/> 认为这次尺寸变化来自程序自身
    /// （协调器套用位置、自适应高度），不当作用户手动调整。</summary>
    private bool _applyingSizeProgrammatically;

    /// <summary>
    /// 自适应高度的上限占工作区高度的比例。
    ///
    /// originally 0.8——在 6 个平台、工作区 1255px 的实机上，上限正好卡在 1004px，
    /// 面板停在这个数、最后一张卡片被截掉 6%，看起来像"自适应算错了"，实际是撞了上限。
    /// 面板本来就贴着任务栏、上方还有 8px 边距，0.9 仍留得出余量，却能多容纳一个平台。
    /// </summary>
    private const double AutoHeightWorkAreaRatio = 0.9;

    /// <summary>拖边框调整尺寸进行中（WM_SIZING 与 WM_EXITSIZEMOVE 之间）。</summary>
    private bool _userSizing;

    private const int WM_SIZING = 0x0214;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTOP = 12;
    private const int HTBOTTOM = 15;

    /// <summary>上下边框的可拖拽厚度（DIP）。</summary>
    private const double ResizeBorderThickness = 6;

    /// <summary>自适应高度的亚像素余量：Measure 结果与实际需要的高度差零点几像素，
    /// 不补上就会让 WPF 认为"还能滚动"从而画出滚动条。</summary>
    private const double SubPixelSlack = 2;

    public MainPanelWindow()
    {
        InitializeComponent();

        // 自适应高度靠 SizeToContent=Height 实现，而它会把高度钉死在内容尺寸上——
        // 用户拖边框时 WPF 会立刻把高度改回去，既拖不动、也收不到 SizeChanged。
        // 所以必须在 Win32 消息层拦截：用户一按下边框开始拖（WM_SIZING），就先退出
        // 自适应，这次拖拽才能真正生效。纯 WPF 事件做不到这一点。
        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(OnWindowMessage);
        };
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // WindowStyle=None + AllowsTransparency=True 的窗口是分层窗口，整个窗口都是客户区，
                // 系统没有可拖拽的调整边框——ResizeMode="CanResize" 单独设置不会有任何效果。
                // 这里手动把上下边缘各 6 DIP 报告成 HTTOP/HTBOTTOM，系统才会按调整尺寸处理。
                // 左右不开放：宽度由显示模式决定（托盘固定 420），放开会出现"改了又被模式切换覆盖"。
                var hit = HitTestVerticalResizeBorder(lParam);
                if (hit != HTCLIENT)
                {
                    handled = true;
                    return new IntPtr(hit);
                }

                break;

            case WM_SIZING when !_applyingSizeProgrammatically:
                // 用户开始拖边框：退出"跟随内容"，之后尊重用户定的高度。
                _autoHeightEnabled = false;
                _userSizing = true;
                break;

            case WM_EXITSIZEMOVE when _userSizing:
                _userSizing = false;
                UserResized?.Invoke(this, EventArgs.Empty);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 顶部手柄拖动：改变高度并保持底边不动（面板向上生长）。托盘弹出模式贴着任务栏上沿，
    /// 只有向上是可用方向；悬浮/看板模式下保持底边不动也符合"抓住顶边往上拉"的直觉。
    /// </summary>
    private void OnHeightGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        var bottom = Top + ActualHeight;
        var maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
        var newHeight = Math.Clamp(ActualHeight - e.VerticalChange, MinHeight, maxHeight);

        _autoHeightEnabled = false;
        _applyingSizeProgrammatically = true;
        try
        {
            SizeToContent = SizeToContent.Manual;
            MaxHeight = maxHeight;
            Height = newHeight;
            Top = bottom - newHeight;
        }
        finally
        {
            _applyingSizeProgrammatically = false;
        }
    }

    private void OnHeightGripDragCompleted(object sender, DragCompletedEventArgs e)
        => UserResized?.Invoke(this, EventArgs.Empty);

    /// <summary>双击手柄恢复"高度跟随内容"。手动调过之后总得有路回来，不然只能去设置里找。</summary>
    private void OnHeightGripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 高度由 ApplyAutoHeight 延迟到布局之后才真正落定，这里不能立刻用 ActualHeight 反算 Top，
        // 否则会按旧高度定位。底边的维持交给 MeasureAndApplyAutoHeight / 校正循环处理。
        ApplyAutoHeight();
        AutoHeightRestored?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>用户双击手柄要求恢复自适应高度，协调器据此清掉该模式的"手动尺寸"标记。</summary>
    public event EventHandler? AutoHeightRestored;

    /// <summary>命中上下边缘返回 HTTOP/HTBOTTOM，其余返回 HTCLIENT（交回 WPF 正常处理）。</summary>
    private int HitTestVerticalResizeBorder(IntPtr lParam)
    {
        if (ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
        {
            return HTCLIENT;
        }

        var raw = lParam.ToInt64();
        var screenPoint = new System.Windows.Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));

        System.Windows.Point local;
        try
        {
            local = PointFromScreen(screenPoint);
        }
        catch (InvalidOperationException)
        {
            return HTCLIENT; // 窗口尚未连上呈现源，这次命中测试交回系统。
        }

        if (local.X < 0 || local.X > ActualWidth)
        {
            return HTCLIENT;
        }

        if (local.Y >= 0 && local.Y <= ResizeBorderThickness)
        {
            return HTTOP;
        }

        if (local.Y <= ActualHeight && local.Y >= ActualHeight - ResizeBorderThickness)
        {
            return HTBOTTOM;
        }

        return HTCLIENT;
    }

    /// <summary>
    /// 让窗口高度跟随内容自适应，上限为当前工作区高度的
    /// <see cref="AutoHeightWorkAreaRatio"/>：平台少时窗口就矮，五个平台也能一屏看全，
    /// 超过上限才回落到滚动。宽度不参与自适应（由模式决定）。
    /// </summary>
    public void ApplyAutoHeight()
    {
        _autoHeightEnabled = true;

        // 标准视觉树与桌面看板视觉树由 Visibility 绑定切换，模式刚变时绑定还没重新求值，
        // 此刻测量会量到上一套树的高度——实测表现为切到桌面看板后窗口仍有近 1000px 高、
        // 内容只占 290px，下面一大片空白。等一次布局（Loaded 优先级）再测。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MeasureAndApplyAutoHeight));
    }

    private void MeasureAndApplyAutoHeight()
    {
        if (!_autoHeightEnabled)
        {
            return; // 等待期间用户已手动调整，尊重用户的尺寸。
        }

        var anchoredBottom = Top + ActualHeight;
        _applyingSizeProgrammatically = true;
        try
        {
            // 刻意不使用 SizeToContent=Height：它会把高度钉死在内容尺寸上，与系统的调整尺寸
            // 循环直接冲突——WM_NCHITTEST 已正确返回 HTTOP、拖拽也进入了调整循环，但每一帧
            // 都被 SizeToContent 改回内容高度，表现为"边框能抓住，窗口纹丝不动"（实测）。
            // 改为自己测一次内容想要的高度、直接赋值，窗口全程停留在 Manual，拖拽才真正可用。
            SizeToContent = SizeToContent.Manual;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);

            if (Content is FrameworkElement root && double.IsFinite(Width) && Width > 0)
            {
                root.UpdateLayout();
                root.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
                var desired = root.DesiredSize.Height;
                var cap = Math.Max(MinHeight, SystemParameters.WorkArea.Height * AutoHeightWorkAreaRatio);
                if (double.IsFinite(desired) && desired > 0)
                {
                    // 直接带上亚像素余量：Measure 的结果实测会差 0.2~0.3px，不加余量的话
                    // 每次重算都会先让滚动条冒出来、再由校正循环补回去，用户看到的就是闪一下
                    // 滚动条。宁可多两像素，也不要这个来回。
                    Height = Math.Clamp(desired + SubPixelSlack, MinHeight, cap);
                    AnchorAfterHeightChange(anchoredBottom);
                }
            }
        }
        finally
        {
            _applyingSizeProgrammatically = false;
        }

        // Measure 出来的期望高度会有零头误差（DPI 舍入；滚动条一出现就占掉宽度、导致文字换行
        // 变高，是个先有鸡还是先有蛋的问题）。实测表现为"自适应后仍差一点点，滚动条冒出来"。
        // 与其猜一个安全余量，不如让结果自己纠正：ScrollableHeight 就是"还差多少才装得下"，
        // 直接补上即可。一次不一定够（补高之后换行情况会变），所以有界重试到滚动条消失为止。
        _growAttempts = 0;
        ScheduleScrollbarCorrection();
    }

    private int _growAttempts;

    /// <summary>用 Background 优先级排队：必须等这一轮布局真正跑完，ScrollableHeight 才有效。
    /// 之前用 Loaded 优先级读到的是 0，导致校正逻辑每次都提前返回（实测"还是矮一点"）。</summary>
    private void ScheduleScrollbarCorrection()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(GrowToEliminateScrollbar));

    /// <summary>自适应高度后若仍有可滚动内容且未到上限，补足这段高度，消掉多余的滚动条。</summary>
    private void GrowToEliminateScrollbar()
    {
        if (!_autoHeightEnabled || CardsScrollViewer is null || _growAttempts++ >= 6)
        {
            return;
        }

        var scrollable = CardsScrollViewer.ScrollableHeight;
        if (scrollable <= 0.01)
        {
            return; // 已经全部装得下。
        }

        // 阈值必须贴近 0：实测收敛到 ScrollableHeight ≈ 0.26px（可见比例 99.97%）时，
        // 内容其实已经全看得见，WPF 却仍然认为"可滚动"并把滚动条画出来——用户看到的就是
        // "自适应完还是矮一点，冒出个滚动条"。补一点点余量把这段亚像素残留吃掉。
        var cap = Math.Max(MinHeight, SystemParameters.WorkArea.Height * AutoHeightWorkAreaRatio);
        var target = Math.Clamp(ActualHeight + scrollable + SubPixelSlack, MinHeight, cap);
        if (target - ActualHeight <= 0.5)
        {
            return; // 已经顶到上限，剩下的只能滚动。
        }

        var bottom = Top + ActualHeight;
        _applyingSizeProgrammatically = true;
        try
        {
            Height = target;
            AnchorAfterHeightChange(bottom);
        }
        finally
        {
            _applyingSizeProgrammatically = false;
        }

        ScheduleScrollbarCorrection();
    }

    /// <summary>当前是否处于"高度跟随内容"状态（用户没手动调过尺寸）。</summary>
    private bool _autoHeightEnabled = true;

    /// <summary>
    /// 程序改变高度后决定往哪个方向生长：
    /// 托盘弹出模式贴着任务栏上沿，只能向上长，所以保持底边不动；
    /// 悬浮/桌面看板的位置来自保存的 placement，必须保持顶边不动。
    ///
    /// 早期版本对所有模式都锚定底边，结果切到桌面看板时用"切换前的旧高度"算出底边，
    /// 再减去新高度，把窗口顶到了屏幕外（实测 Top=1812，工作区只到 1392，窗口彻底消失）。
    /// 末尾统一把位置钳回工作区：无论上游怎么算错，窗口都不该整个跑出屏幕。
    /// </summary>
    private void AnchorAfterHeightChange(double previousBottom)
    {
        if (!PositionManagedExternally && double.IsFinite(previousBottom))
        {
            Top = previousBottom - ActualHeight;
        }

        ClampIntoWorkArea();
    }

    /// <summary>把窗口位置钳进工作区，至少保留顶部一条可抓取的区域可见。</summary>
    private void ClampIntoWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return;
        }

        const double MinVisible = 32;
        if (double.IsFinite(Left))
        {
            Left = Math.Clamp(Left, work.Left - (ActualWidth - MinVisible), work.Right - MinVisible);
        }

        if (double.IsFinite(Top))
        {
            Top = Math.Clamp(Top, work.Top, work.Bottom - MinVisible);
        }
    }

    /// <summary>套用一个明确的高度（用户手动调过、或从配置恢复），关闭自适应。</summary>
    public void ApplyManualHeight(double height)
    {
        _autoHeightEnabled = false;
        _applyingSizeProgrammatically = true;
        try
        {
            SizeToContent = SizeToContent.Manual;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
            if (double.IsFinite(height) && height > 0)
            {
                Height = Math.Clamp(height, MinHeight, MaxHeight);
            }
        }
        finally
        {
            _applyingSizeProgrammatically = false;
        }
    }

    /// <summary>协调器套用位置/尺寸期间调用，避免把程序自身的改动误判成用户手动调整。</summary>
    public IDisposable SuppressUserResizeDetection()
    {
        _applyingSizeProgrammatically = true;
        return new ResizeSuppression(this);
    }

    private sealed class ResizeSuppression : IDisposable
    {
        private readonly MainPanelWindow _owner;
        public ResizeSuppression(MainPanelWindow owner) => _owner = owner;
        public void Dispose() => _owner._applyingSizeProgrammatically = false;
    }

    public void AttachViewModel(MainPanelViewModel viewModel)
    {
        DataContext = viewModel;

        // 卡片显示/隐藏、平台增删、额度窗口数量变化都会改变内容高度：仍在"跟随内容"状态时
        // 自动重算，用户改完设置回到面板不必再手动拉一次。手动调过尺寸的模式不受影响
        // （ApplyAutoHeight 内部会检查 _autoHeightEnabled）。
        viewModel.ContentHeightMayHaveChanged += (_, _) =>
        {
            if (_autoHeightEnabled)
            {
                ApplyAutoHeight();
            }
        };

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
            else if (e.PropertyName == nameof(MainPanelViewModel.CurrentMode))
            {
                // 每次重新进入桌面看板模式都从"工具栏隐藏"这个干净状态开始，不带着上次离开时
                // 可能还没来得及淡出的可见状态——否则下次进入桌面看板模式时工具栏可能不经悬停
                // 就已经显示，跟"常态透明、悬停才出现"的设计矛盾。
                if (viewModel.CurrentMode != WindowPresentationMode.DesktopPanel)
                {
                    _toolbarHideTimer.Stop();
                    ResetHoverToolbar();
                }
            }
        };

        _toolbarHideTimer.Tick += (_, _) =>
        {
            _toolbarHideTimer.Stop();
            HideHoverToolbar();
        };
    }

    private void OnWindowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _toolbarHideTimer.Stop();
        if (DataContext is MainPanelViewModel { CurrentMode: WindowPresentationMode.DesktopPanel })
        {
            ShowHoverToolbar();
        }
    }

    private void OnWindowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is MainPanelViewModel { CurrentMode: WindowPresentationMode.DesktopPanel })
        {
            _toolbarHideTimer.Start();
        }
    }

    /// <summary>鼠标移入窗口后 120～180ms 淡入悬停工具栏，同时把常态显示的紧凑更新时间淡出——
    /// 两者叠在同一个格子里，用互补的不透明度切换，不需要额外的布局空间（文档 §10.2）。</summary>
    private void ShowHoverToolbar()
    {
        if (HoverToolbar is null || CompactHeaderTimeBlock is null)
        {
            return;
        }

        HoverToolbar.IsHitTestVisible = true;
        AnimateOpacity(HoverToolbar, 1.0, ToolbarFadeInDuration);
        AnimateOpacity(CompactHeaderTimeBlock, 0.0, ToolbarFadeInDuration);
    }

    private void HideHoverToolbar()
    {
        if (HoverToolbar is null || CompactHeaderTimeBlock is null)
        {
            return;
        }

        HoverToolbar.IsHitTestVisible = false;
        AnimateOpacity(HoverToolbar, 0.0, ToolbarFadeOutDuration);
        AnimateOpacity(CompactHeaderTimeBlock, 1.0, ToolbarFadeOutDuration);
    }

    /// <summary>不带动画地把工具栏立即收回初始状态——用于"重新进入桌面看板模式"这种不该有过渡
    /// 动画的场景，跟鼠标悬停触发的淡入淡出（<see cref="ShowHoverToolbar"/>/<see cref="HideHoverToolbar"/>）区分开。</summary>
    private void ResetHoverToolbar()
    {
        if (HoverToolbar is null || CompactHeaderTimeBlock is null)
        {
            return;
        }

        HoverToolbar.IsHitTestVisible = false;
        HoverToolbar.BeginAnimation(UIElement.OpacityProperty, null);
        HoverToolbar.Opacity = 0;
        CompactHeaderTimeBlock.BeginAnimation(UIElement.OpacityProperty, null);
        CompactHeaderTimeBlock.Opacity = 1;
    }

    /// <summary>跟随 Windows"减少动画"系统设置（文档 §14）：该设置关闭时直接跳变到目标值，
    /// 不播放渐变过渡，而不是无视系统偏好硬播一段动画。</summary>
    private static void AnimateOpacity(UIElement element, double to, Duration duration)
    {
        var effectiveDuration = SystemParameters.ClientAreaAnimation ? duration : new Duration(TimeSpan.Zero);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, effectiveDuration));
    }

    /// <summary>桌面看板悬停工具栏"更多"按钮：左键点击就弹出菜单（不是右键专属），
    /// 复用 Button.ContextMenu 而不是另起一套 Popup。</summary>
    private void OnCompactMoreMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
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
        // 平台增减、额度窗口数量变化都会改变内容高度：仍在自适应状态时每次弹出都重算一次，
        // 用户就不必为了看全所有平台反复手动拉高。手动调过的尺寸不受影响。
        if (_autoHeightEnabled)
        {
            ApplyAutoHeight();
        }

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
