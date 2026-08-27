using System.Runtime.InteropServices;
using System.Text;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 基于 Windows Credential Manager（通用凭据）的安全存储，专供用户手动填写的
/// MiniMax / DeepSeek API Key 使用。绝不写入普通 JSON、源码、日志或测试快照（文档 §7）。
///
/// Claude / Codex 的 OAuth token 不经过这里——它们由对应 CLI 自行管理和刷新，
/// QuotaFlow 只在内存中短暂读取，从不落地存储（见 <see cref="Authentication.ClaudeCredentialReader"/>
/// / <see cref="Authentication.CodexCredentialReader"/>）。
/// </summary>
public sealed class SecureCredentialStore
{
    private const string TargetPrefix = "QuotaFlow:";
    private const uint CredTypeGeneric = 1; // CRED_TYPE_GENERIC
    private const uint CredPersistLocalMachine = 2; // CRED_PERSIST_LOCAL_MACHINE

    /// <summary>写入（或覆盖）一条凭据。<paramref name="secret"/> 明文只在本方法调用期间存在于内存中。</summary>
    public void Save(string key, string secret)
    {
        var target = TargetPrefix + key;
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length == 0 ? 1 : blob.Length);

        try
        {
            if (blob.Length > 0)
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
            }

            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = "QuotaFlow provider API key",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = "QuotaFlow",
            };

            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"写入 Windows 凭据管理器失败（Win32 错误码 {error}）");
            }
        }
        finally
        {
            // 用完立即清零非托管内存，减少明文在进程地址空间内的残留时间。
            // （.NET 的 string/byte[] 本身是垃圾回收管理的，无法做到绝对不可恢复的擦除，
            // 这里只做力所能及的防御，不构成安全边界承诺。）
            ZeroAndFree(blobPtr, blob.Length);
            Array.Clear(blob);
        }
    }

    /// <summary>读取一条凭据；不存在或读取失败时返回 null，调用方应视为"未配置"。</summary>
    public string? TryRead(string key)
    {
        var target = TargetPrefix + key;
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>删除一条凭据（用于"清除缓存和安全凭据"设置项）。不存在时静默忽略。</summary>
    public void Delete(string key)
    {
        var target = TargetPrefix + key;
        CredDelete(target, CredTypeGeneric, 0);
    }

    private static void ZeroAndFree(IntPtr ptr, int length)
    {
        if (length > 0)
        {
            for (var i = 0; i < length; i++)
            {
                Marshal.WriteByte(ptr, i, 0);
            }
        }

        Marshal.FreeHGlobal(ptr);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten; // FILETIME，写入时由系统忽略此字段
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
}
