using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaFlow.Windows.App.ViewModels;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 设置窗口代码后置只负责用代码设置标题栏图标，而不是 XAML 里的 Icon="..."——
/// 实测在动态 new 出来的窗口（不是 StartupUri 指定的主窗口）上，Icon 的 XAML
/// 类型转换器会在 InitializeComponent() 阶段抛 XamlParseException，把整个进程带崩；
/// 换成 BitmapImage + pack URI 就没问题。
/// API Key 的掩码/明文/编辑都通过绑定 ViewModel 的单行输入框完成，无需后置桥接。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        TrySetIcon();
        Deactivated += (_, _) => ViewModel.HideAllRevealedKeys();
        Closed += (_, _) => ViewModel.HideAllRevealedKeys();

        // 设置页很长，"自定义平台"卡片在中段偏下；新增一行后自动把它滚动进可视区域，
        // 不然用户点了"+ 添加自定义平台"却看不到任何变化，会误以为没生效。
        viewModel.CustomPlatformRows.CollectionChanged += OnCustomPlatformRowsChanged;
        Closed += (_, _) => viewModel.CustomPlatformRows.CollectionChanged -= OnCustomPlatformRowsChanged;

        Closing += OnClosingConfirmUnsaved;
        PreviewKeyDown += OnPreviewKeyDownCloseOnEscape;
    }

    /// <summary>
    /// Esc 关闭设置窗口，与主面板的行为保持一致。走 <see cref="Window.Close"/> 而不是直接
    /// 隐藏，好让下面的未保存确认照常生效。
    /// </summary>
    private void OnPreviewKeyDownCloseOnEscape(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // 快捷键捕获框正在等用户按组合键时，Esc 属于它的输入，不能拿去关窗。
        if (Equals(Keyboard.FocusedElement, HotkeyCaptureBox))
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    /// <summary>
    /// 关窗前挽留：本页是"编辑草稿 + 点保存"模式，直接关掉会静默丢弃改动。
    /// 「显示与窗口」那一页是即时生效的，不参与这里的比对（它的值本来就已经在磁盘上了）。
    /// </summary>
    private void OnClosingConfirmUnsaved(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ViewModel.HasUnsavedChanges)
        {
            return;
        }

        // 显式限定命名空间：项目同时引用了 WinForms（只为托盘 NotifyIcon），
        // MessageBox / KeyEventArgs 这些名字在两个命名空间里都有。
        var choice = System.Windows.MessageBox.Show(
            this,
            "有尚未保存的设置改动，要保存吗？",
            "QuotaFlow 设置",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        switch (choice)
        {
            case MessageBoxResult.Yes:
                ViewModel.SavePendingChanges();
                break;
            case MessageBoxResult.Cancel:
                e.Cancel = true; // 留在设置页继续编辑
                break;
            default:
                break; // No = 放弃改动，直接关闭
        }
    }

    private void OnCustomPlatformRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        // 用 Background 优先级延后到本轮布局/容器生成之后再取容器，否则新加的行还没有对应的可视化元素。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var item in e.NewItems)
            {
                if (CustomPlatformsItemsControl.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement element)
                {
                    element.BringIntoView();
                }
            }
        });
    }

    /// <summary>
    /// "组合键"捕获框：按下的第一个非修饰键就是新的主键，同时读 <see cref="Keyboard.Modifiers"/>
    /// 拿到当前按住的修饰键集合。Alt 组合在 WPF 里比较特殊——<c>e.Key</c> 会是
    /// <see cref="Key.System"/>，真正按下的键在 <see cref="KeyEventArgs.SystemKey"/> 里。
    /// 全程 <c>e.Handled = true</c>：这个框不是普通文本框，不能让 Tab/Alt 之类的键触发它们
    /// 平常的副作用（切焦点、弹菜单）。
    /// </summary>
    private void OnHotkeyCapturePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return; // 单按修饰键本身，还没构成一个完整组合，等用户按下主键。
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        ViewModel.SetHotkeyCombo(modifiers, key.ToString());
    }

    private void TrySetIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app_icon.png"));
        }
        catch (Exception)
        {
            // 图标只是装饰性的；万一在某些环境下资源加载仍然失败，宁可没有标题栏图标，
            // 也不能让这行代码把设置窗口（乃至整个托盘应用）搭进去。
        }
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;
}
