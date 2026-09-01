using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 备份包的明文内容：应用设置 + 凭据键值对。仅在内存与加密前后短暂存在，绝不落盘。
/// </summary>
public sealed class BackupPayload
{
    public AppSettings Settings { get; set; } = new();

    /// <summary>凭据管理器里的键值对（键名如 <c>minimax:ApiKey</c>）。不包含密钥时为空字典。</summary>
    public Dictionary<string, string> Secrets { get; set; } = [];
}

/// <summary>导入失败的原因，供界面给出准确提示而不是笼统报错。</summary>
public enum BackupImportFailure
{
    None,
    NotABackupFile,
    UnsupportedVersion,
    WrongPasswordOrCorrupted,
}

public sealed class BackupImportResult
{
    public BackupPayload? Payload { get; init; }
    public BackupImportFailure Failure { get; init; }
    public bool Succeeded => Payload is not null;
}

/// <summary>
/// 跨设备迁移用的加密备份包。
///
/// 为什么需要独立于 <see cref="AppSettingsStore"/> 的另一套加密：本机设置用 DPAPI
/// （<c>CurrentUser</c> 作用域）保护，只有同一台机器的同一个 Windows 用户能解开，
/// 天然无法跨设备；而 API Key 与登录态根本不在设置文件里，它们在 Windows 凭据管理器中。
/// 所以迁移必须是"显式导出 + 用户自己设的口令"这一条独立路径。
///
/// 安全取舍（用户明确选择了"密钥也导出"）：
/// - 口令经 PBKDF2-HMAC-SHA256 派生（迭代次数见 <see cref="Pbkdf2Iterations"/>，随包一起记录，
///   便于将来提高强度时仍能读旧包），配 16 字节随机盐。
/// - 内容用 AES-256-GCM 加密，12 字节随机 nonce，16 字节认证标签。GCM 是 AEAD：
///   口令错误或文件被篡改都会在解密时被标签校验挡下，不会解出一堆垃圾数据去覆盖用户配置。
/// - 明文（含密钥）只在内存中存在；本类不写任何文件、不打日志，密钥绝不出现在明文包外。
/// </summary>
public static class SettingsBackup
{
    private const string FormatMarker = "quotaflow-backup";
    private const int FormatVersion = 1;

    /// <summary>PBKDF2 迭代次数，取 OWASP 对 PBKDF2-HMAC-SHA256 的推荐量级。</summary>
    public const int Pbkdf2Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12; // AES-GCM 标准 nonce 长度
    private const int TagBytes = 16;
    private const int KeyBytes = 32;   // AES-256

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 信封用 camelCase 且读取时忽略大小写：这个文件是要给人看、也可能被手工检查的，
    /// 键名风格统一更省事；忽略大小写则让手工编辑过的文件也仍能读入。
    /// </summary>
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = FormatMarker;
        public int Version { get; set; } = FormatVersion;
        public string Kdf { get; set; } = "pbkdf2-sha256";
        public int Iterations { get; set; } = Pbkdf2Iterations;
        public string Cipher { get; set; } = "aes-256-gcm";
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }

    /// <summary>把设置与凭据打成一个用口令加密的备份包（返回可直接写盘的 JSON 文本）。</summary>
    public static string Export(BackupPayload payload, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, PayloadJson));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return JsonSerializer.Serialize(
            new Envelope
            {
                Iterations = Pbkdf2Iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext),
            },
            EnvelopeJson);
    }

    /// <summary>
    /// 解开备份包。口令错误、文件被改动、或根本不是备份文件时返回对应的失败原因，
    /// 绝不抛异常——调用方拿到的要么是完整可用的内容，要么是明确的失败，不存在中间状态。
    /// </summary>
    public static BackupImportResult Import(string fileContent, string password)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(fileContent, EnvelopeJson);
        }
        catch (JsonException)
        {
            return new BackupImportResult { Failure = BackupImportFailure.NotABackupFile };
        }

        if (envelope is null || envelope.Format != FormatMarker)
        {
            return new BackupImportResult { Failure = BackupImportFailure.NotABackupFile };
        }

        if (envelope.Version > FormatVersion || envelope.Kdf != "pbkdf2-sha256" || envelope.Cipher != "aes-256-gcm")
        {
            return new BackupImportResult { Failure = BackupImportFailure.UnsupportedVersion };
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(envelope.Salt);
            nonce = Convert.FromBase64String(envelope.Nonce);
            tag = Convert.FromBase64String(envelope.Tag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException)
        {
            return new BackupImportResult { Failure = BackupImportFailure.NotABackupFile };
        }

        if (salt.Length != SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes ||
            envelope.Iterations is < 1 or > 10_000_000)
        {
            return new BackupImportResult { Failure = BackupImportFailure.NotABackupFile };
        }

        var key = DeriveKey(password, salt, envelope.Iterations);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            // 口令错误或内容被篡改时 GCM 标签校验失败，这里会抛 CryptographicException——
            // 正是我们要的：绝不会解出垃圾数据再去覆盖用户已有的配置。
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return new BackupImportResult { Failure = BackupImportFailure.WrongPasswordOrCorrupted };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<BackupPayload>(Encoding.UTF8.GetString(plaintext), PayloadJson);
            return payload is null
                ? new BackupImportResult { Failure = BackupImportFailure.WrongPasswordOrCorrupted }
                : new BackupImportResult { Payload = payload };
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            return new BackupImportResult { Failure = BackupImportFailure.WrongPasswordOrCorrupted };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
