using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Credentials.UI;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 查看明文 API Key 前的身份验证。优先走 Windows Hello（指纹/人脸/PIN），
/// 设备没有配置 Windows Hello 时自动退回到"重新输入当前 Windows 账号密码"。
/// 两条路径都只返回"验证是否通过"，不经手、不缓存、不记录用户的生物特征或密码。
/// </summary>
public static class IdentityVerifier
{
    public static async Task<bool> VerifyAsync(string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == UserConsentVerifierAvailability.Available)
            {
                var result = await UserConsentVerifier.RequestVerificationAsync(message);
                return result == UserConsentVerificationResult.Verified;
            }
        }
        catch (Exception)
        {
            // Windows Hello 运行时异常（极少见，例如安全子系统故障）：降级走密码验证，而不是直接放行。
        }

        return WindowsPasswordVerifier.Verify(message);
    }
}

/// <summary>
/// Windows Hello 不可用时的兜底：弹出系统凭据对话框，要求重新输入当前登录账号的密码，
/// 用 LogonUser 校验是否正确。只用来"确认是本人"，不获取、不存储、不使用该密码做其他事。
/// </summary>
internal static class WindowsPasswordVerifier
{
    private const uint CredUiWinGeneric = 0x1;
    private const uint CredUiFlagsExcludeCertificates = 0x8;
    private const int LogonTypeInteractive = 2;
    private const int LogonProviderDefault = 0;

    public static bool Verify(string message)
    {
        var credui = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            pszCaptionText = "QuotaFlow 身份验证",
            pszMessageText = message,
        };

        uint authPackage = 0;
        var save = false;

        var result = CredUIPromptForWindowsCredentials(
            ref credui, 0, ref authPackage, IntPtr.Zero, 0,
            out var outAuthBuffer, out var outAuthBufferSize, ref save, CredUiWinGeneric | CredUiFlagsExcludeCertificates);

        if (result != 0 || outAuthBuffer == IntPtr.Zero)
        {
            return false; // 用户取消，或对话框调用失败——都视为未通过验证。
        }

        try
        {
            var userName = new StringBuilder(256);
            var domain = new StringBuilder(256);
            var password = new StringBuilder(256);
            var userNameLen = (uint)userName.Capacity;
            var domainLen = (uint)domain.Capacity;
            var passwordLen = (uint)password.Capacity;

            if (!CredUnPackAuthenticationBuffer(0, outAuthBuffer, outAuthBufferSize,
                    userName, ref userNameLen, domain, ref domainLen, password, ref passwordLen))
            {
                return false;
            }

            var loggedOn = LogonUser(
                userName.ToString(),
                domain.Length > 0 ? domain.ToString() : Environment.MachineName,
                password.ToString(),
                LogonTypeInteractive, LogonProviderDefault, out var token);

            if (loggedOn && token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            return loggedOn;
        }
        finally
        {
            // 认证缓冲区里含明文密码，用完立即释放，不在内存里多停留。
            CoTaskMemFree(outAuthBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint CredUIPromptForWindowsCredentials(
        ref CREDUI_INFO notificationDataStruct, uint authError, ref uint authPackage,
        IntPtr inAuthBuffer, uint inAuthBufferSize, out IntPtr outAuthBuffer, out uint outAuthBufferSize,
        ref bool save, uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(
        uint flags, IntPtr authBuffer, uint authBufferSize,
        StringBuilder userName, ref uint maxUserName,
        StringBuilder domainName, ref uint maxDomainName,
        StringBuilder password, ref uint maxPassword);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string userName, string domain, string password,
        int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
