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

    /// <summary>
    /// 写入（或覆盖）一条凭据。<paramref name="secret"/> 明文只在本方法调用期间存在于内存中。
    /// </summary>
    /// <param name="useUtf8">
    /// 凭据管理器单个凭据的明文 blob 上限是 2560 字节（CRED_MAX_CREDENTIAL_BLOB_SIZE）。
    /// 默认 UTF-16 编码下，超过约 1280 个字符的长值（如百炼 Console Cookie）会超出上限导致
    /// CredWrite 失败。Cookie/会话类 ASCII 长值用 UTF-8 存储可把体积减半，腾出更多余量；
    /// 读取时（<see cref="TryRead"/>）必须传同一个 <paramref name="useUtf8"/>，否则会解出乱码。
    /// </param>
    public void Save(string key, string secret, bool useUtf8 = false)
    {
        var target = TargetPrefix + key;
        var encoding = useUtf8 ? Encoding.UTF8 : Encoding.Unicode;
        var blob = encoding.GetBytes(secret);

        // 凭据管理器单条凭据的明文 blob 上限是 2560 字节；超限的 CredWrite 会失败（Win32 错误码
        // 1783），但那串错误码对用户没有意义。这里先显式拦截并给出可读信息，调用方能据此决定
        // 是精简内容还是改用其它存储方案。
        const int maxBlobBytes = 2560;
        if (blob.Length > maxBlobBytes)
        {
            throw new InvalidOperationException(
                $"凭据内容过大（{secret.Length} 字符 / {blob.Length} 字节），超过 Windows 凭据管理器单条凭据 {maxBlobBytes} 字节上限，请精简后重试");
        }

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

    /// <summary>读取一条凭据；不存在或读取失败时返回 null，调用方应视为"未配置"。
    /// <paramref name="useUtf8"/> 必须与写入时一致。</summary>
    public string? TryRead(string key, bool useUtf8 = false)
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
            var encoding = useUtf8 ? Encoding.UTF8 : Encoding.Unicode;
            return encoding.GetString(bytes);
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

    // ---- 分片存储：用于长度不可控、且不能靠"精简内容"适配 2560 字节上限的长值 ----
    // （典型场景：阿里云百炼 Console Cookie——完整会话可能涉及多个域，人为按域名/字段名
    // 精简等于在猜哪些 Cookie 是查询必需的，一旦猜错就会复现"每次都提示需要重新登录"这类问题。
    // 分片存储保证内容原样保留，用多条凭据换取不设上限。）

    private const string ChunkCountSuffix = ":chunks";

    /// <summary>单个分片的字节预算，明显低于 2560 硬上限留出安全余量。</summary>
    private const int ChunkBudgetBytes = 2000;

    /// <summary>按字节预算切成多个分片凭据（<c>key:chunks</c> 记录分片数，<c>key:0</c>/<c>key:1</c>/…
    /// 存各分片），全部使用 UTF-8（长值多为 ASCII，UTF-8 更省空间）。</summary>
    public void SaveLarge(string key, string secret)
    {
        // 先清理旧分片，避免新值变短后旧的多余分片残留（读取时会把它们一起拼接进去）。
        DeleteLarge(key);

        var chunks = SplitByByteBudget(secret, Encoding.UTF8, ChunkBudgetBytes);
        for (var i = 0; i < chunks.Count; i++)
        {
            Save($"{key}{ChunkCountSuffix}:{i}", chunks[i], useUtf8: true);
        }

        Save($"{key}{ChunkCountSuffix}", chunks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>读取并拼接分片值；分片计数缺失或任一分片缺失都视为"未配置"（返回 null），
    /// 不拼出一份残缺内容。</summary>
    public string? TryReadLarge(string key)
    {
        var countText = TryRead($"{key}{ChunkCountSuffix}");
        if (countText is null || !int.TryParse(countText, out var count) || count <= 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var chunk = TryRead($"{key}{ChunkCountSuffix}:{i}", useUtf8: true);
            if (chunk is null)
            {
                return null;
            }

            sb.Append(chunk);
        }

        return sb.ToString();
    }

    /// <summary>删除分片计数与全部已知分片；不存在时静默忽略。</summary>
    public void DeleteLarge(string key)
    {
        var countText = TryRead($"{key}{ChunkCountSuffix}");
        if (countText is not null && int.TryParse(countText, out var count) && count > 0)
        {
            for (var i = 0; i < count; i++)
            {
                Delete($"{key}{ChunkCountSuffix}:{i}");
            }
        }

        Delete($"{key}{ChunkCountSuffix}");
    }

    /// <summary>把字符串按字节预算切片，逐字符累加字节数、超预算就切一刀。Cookie/Token 类内容
    /// 按 RFC 6265 基本是 ASCII，逐字符 GetByteCount 不构成性能问题（几 KB 级别的输入）。</summary>
    private static List<string> SplitByByteBudget(string value, Encoding encoding, int maxBytesPerChunk)
    {
        var chunks = new List<string>();
        if (value.Length == 0)
        {
            chunks.Add(string.Empty);
            return chunks;
        }

        var current = new StringBuilder();
        var currentBytes = 0;
        foreach (var ch in value)
        {
            var chBytes = encoding.GetByteCount([ch]);
            if (currentBytes + chBytes > maxBytesPerChunk && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentBytes = 0;
            }

            current.Append(ch);
            currentBytes += chBytes;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
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
