using System.Windows;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 备份包口令输入框。
///
/// 用 <see cref="System.Windows.Controls.PasswordBox"/> 而不是普通 TextBox：口令不进
/// 可绑定属性、不参与任何数据绑定链路，只在用户点"确定"时取一次值交给调用方，用完即弃。
/// 本窗口不持久化口令，也不写任何日志。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private readonly bool _requireConfirmation;

    /// <summary>用户确认后的口令；取消时为 null。</summary>
    public string? Password { get; private set; }

    private PasswordPromptWindow(string title, string hint, bool requireConfirmation)
    {
        InitializeComponent();
        _requireConfirmation = requireConfirmation;
        TitleText.Text = title;
        HintText.Text = hint;
        ConfirmSection.Visibility = requireConfirmation ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>导出场景：需要二次确认，打错字会导致备份包再也打不开。</summary>
    public static string? AskNewPassword(Window? owner) => Show(
        owner,
        "为备份包设置口令",
        "导出的文件包含 API Key 与登录态，将用这个口令加密。口令不会被保存，忘记后无法找回，"
            + "备份包也就再也打不开——请务必记牢。建议使用足够长的口令。",
        requireConfirmation: true);

    /// <summary>导入场景：只需输入一次。</summary>
    public static string? AskExistingPassword(Window? owner) => Show(
        owner,
        "输入备份包口令",
        "请输入导出这个备份包时设置的口令。",
        requireConfirmation: false);

    private static string? Show(Window? owner, string title, string hint, bool requireConfirmation)
    {
        var dialog = new PasswordPromptWindow(title, hint, requireConfirmation);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = PasswordInput.Password.Length > 0;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_requireConfirmation && PasswordInput.Password != ConfirmInput.Password)
        {
            ErrorText.Text = "两次输入的口令不一致";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Password = PasswordInput.Password;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
