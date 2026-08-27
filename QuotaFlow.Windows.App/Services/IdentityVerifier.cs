using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Credentials.UI;

namespace QuotaFlow.Windows.App.Services;

/// <summary>身份验证结果：通过 / 用户主动取消 / 验证失败。</summary>
public enum IdentityVerificationResult
{
    Verified,
    Cancelled,
    Failed,
}

/// <summary>
/// 查看明文 API Key 前的身份验证。优先走 Windows Hello（指纹/人脸/PIN），
/// Windows Hello 不可用、未配置或验证未通过时，一律降级到"重新输入当前 Windows 账号密码"。
/// 两条路径都只返回"验证是否通过"，不经手、不缓存、不记录用户的生物特征或密码。
/// </summary>
public static class IdentityVerifier
{
    public static async Task<IdentityVerificationResult> VerifyAsync(string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == UserConsentVerifierAvailability.Available)
            {
                var result = await UserConsentVerifier.RequestVerificationAsync(message);
                if (result == UserConsentVerificationResult.Verified)
                {
                    return IdentityVerificationResult.Verified;
                }

                // 关键：很多机器 CheckAvailability 会报 Available（比如存在指纹/人脸设备），但
                // 实际并没有给当前用户配置 PIN/生物识别——此时 RequestVerificationAsync 会不弹任何
                // UI 直接返回 DeviceNotPresent/NotConfiguredForUser。旧代码在这里直接返回失败，
                // 导致"点眼睛却什么都不弹"。这里不能直接拒绝，统一降级到系统密码验证。
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
/// Windows Hello 不可用/未配置时的兜底：弹出系统凭据对话框，要求重新输入当前登录账号的密码，
/// 用 LogonUser 校验是否正确。只用来"确认是本人"，不获取、不存储、不使用该密码做其他事。
/// </summary>
internal static class WindowsPasswordVerifier
{
    private const uint CredUiWinGeneric = 0x1;
    private const uint CredUiCancel = 1223; // ERROR_CANCELLED：用户主动点了"取消"
    private const int LogonTypeInteractive = 2;
    private const int LogonProviderDefault = 0;

    public static IdentityVerificationResult Verify(string message)
    {
        var credui = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            pszCaptionText = "QuotaFlow 身份验证",
            pszMessageText = message,
            // 注意：hwndParent 必须留 IntPtr.Zero。实测传 GetForegroundWindow() 的句柄会让
            // CredUIPromptForWindowsCredentials 直接返回 ERROR_INVALID_PARAMETER(0x57)、弹窗根本不出现。
            // 不设父窗口时凭据框会居中弹出，行为正常。
        };

        uint authPackage = 0;
        var save = false;

        // flags 只用 CREDUIWIN_GENERIC。实测叠加 CREDUIWIN_EXCLUDE_CERTIFICATES(0x8) 会让
        // 本机直接返回 ERROR_INVALID_PARAMETER(0x57)、弹窗不出现。
        var result = CredUIPromptForWindowsCredentials(
            ref credui, 0, ref authPackage, IntPtr.Zero, 0,
            out var outAuthBuffer, out var outAuthBufferSize, ref save, CredUiWinGeneric);

        if (result == CredUiCancel)
        {
            return IdentityVerificationResult.Cancelled; // 用户主动取消，不算"验证失败"
        }

        if (result != 0 || outAuthBuffer == IntPtr.Zero)
        {
            return IdentityVerificationResult.Failed;
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
                return IdentityVerificationResult.Failed;
            }

            var user = userName.ToString();
            var typedDomain = domain.Length > 0 ? domain.ToString() : string.Empty;

            // Microsoft 账号 / 本地账号的"用户名 + 域"组合有差异（比如 MSA 通常输邮箱、域为空），
            // 逐个候选尝试，任一通过即视为验证成功。
            foreach (var candidate in new[] { typedDomain, Environment.MachineName, string.Empty })
            {
                if (VerifyLogon(user, candidate, password.ToString()))
                {
                    return IdentityVerificationResult.Verified;
                }
            }

            return IdentityVerificationResult.Failed;
        }
        finally
        {
            // 认证缓冲区里含明文密码，用完立即释放，不在内存里多停留。
            CoTaskMemFree(outAuthBuffer);
        }
    }

    private static bool VerifyLogon(string userName, string domain, string password)
    {
        var loggedOn = LogonUser(
            userName,
            string.IsNullOrEmpty(domain) ? null : domain,
            password,
            LogonTypeInteractive, LogonProviderDefault, out var token);

        if (loggedOn && token != IntPtr.Zero)
        {
            CloseHandle(token);
        }

        return loggedOn;
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
    private static extern bool LogonUser(string userName, string? domain, string password,
        int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
