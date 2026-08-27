using System.Windows;
using QuotaFlow.Windows.App.ViewModels;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 设置窗口代码后置只做一件事：把 PasswordBox.Password 桥接到 ViewModel——
/// WPF 出于安全考虑不允许直接 {Binding} PasswordBox.Password，这是官方推荐的例外处理方式。
/// 保存后立即清空输入框，避免明文密钥停留在界面上。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Deactivated += (_, _) => ViewModel.HideAllRevealedKeys();
        Closed += (_, _) => ViewModel.HideAllRevealedKeys();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void OnSaveMiniMaxKeyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.MiniMaxApiKeyInput = MiniMaxKeyBox.Password;
        ViewModel.SaveMiniMaxKeyCommand.Execute(null);
        MiniMaxKeyBox.Clear();
    }

    private void OnSaveDeepSeekKeyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.DeepSeekApiKeyInput = DeepSeekKeyBox.Password;
        ViewModel.SaveDeepSeekKeyCommand.Execute(null);
        DeepSeekKeyBox.Clear();
    }
}
