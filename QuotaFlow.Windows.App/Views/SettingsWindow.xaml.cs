using System.Windows;
using System.Windows.Media.Imaging;
using QuotaFlow.Windows.App.ViewModels;

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
