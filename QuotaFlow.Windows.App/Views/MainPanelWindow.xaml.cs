using System.Windows;
using System.Windows.Input;
using QuotaFlow.Windows.App.ViewModels;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 托盘弹出面板。无边框、置顶、点击外部自动收起，尺寸固定约 420x560（文档 §5.1）。
/// 定位算法只是"贴到任务栏所在的那条屏幕边"的通用启发式，不做 Shell 层面的
/// 精确图标坐标查询——对绝大多数用户（任务栏在屏幕底部）效果等同于"出现在托盘上方"。
/// </summary>
public partial class MainPanelWindow : Window
{
    public event EventHandler? SettingsRequested;

    public MainPanelWindow(MainPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // "固定"按钮（ViewModel.IsPinned）和诊断用的 --show-panel 走的是同一个开关——
        // 用户固定面板本质上就是"这次先别自动收起"，跟诊断场景要的效果一样，没必要拆两个字段。
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainPanelViewModel.IsPinned))
            {
                KeepVisibleOnDeactivate = viewModel.IsPinned;
            }
        };
    }

    /// <summary>
    /// True 时失焦不自动隐藏：要么是诊断用途（--show-panel），要么是用户在面板上点了"固定"
    /// （<see cref="MainPanelViewModel.IsPinned"/>，见构造函数里的同步）。正常使用默认 False，
    /// 点击面板外部会立即收起；固定/诊断状态下才不会。
    /// </summary>
    public bool KeepVisibleOnDeactivate { get; set; }

    public void ShowNearTray()
    {
        PositionNearTaskbar();
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

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!KeepVisibleOnDeactivate)
        {
            Hide();
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);
}
