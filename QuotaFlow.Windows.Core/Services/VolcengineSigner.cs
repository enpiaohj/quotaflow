using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 火山引擎控制面 OpenAPI 签名（Signature V4 的火山变体）。纯函数，不依赖网络/时钟以外的任何
/// 状态，可用固定输入做确定性单测。
///
/// 依据：火山方舟 Coding Plan / Agent Plan 的额度查询走的是控制面 OpenAPI 网关
/// （<c>open.volcengineapi.com</c>），不是推理 API（<c>ark.cn-beijing.volces.com/api/coding</c>），
/// 后者用的是 Bearer Token，查不到套餐用量。这个网关没有面向个人开发者的公开文档，本实现
/// 依据两份独立开源实现交叉核对：
///   1. <see href="https://github.com/lordqyxz/dsh-ark-quota">lordqyxz/dsh-ark-quota</see>
///      （MIT，<c>lib/signature.js</c>），其注释明确写明"已与 cc-switch 的可用实现及一次真实网关
///      探测交叉验证"；
///   2. <see href="https://github.com/farion1231/cc-switch">farion1231/cc-switch</see>
///      的 Issue/Release 说明独立确认了同样三处与标准 AWS SigV4 的差异。
///
/// 与标准 AWS SigV4 的三处差异（火山变体特有，不是笔误）：
///   1. Canonical Headers 用<b>固定顺序</b> <c>host;x-date;x-content-sha256;content-type</c>，
///      不是字母序；
///   2. 算法名是 <c>HMAC-SHA256</c>（没有 <c>AWS4</c> 前缀），Credential Scope 结尾是
///      <c>request</c>（不是 <c>aws4_request</c>），派生密钥链的输入也是不带前缀的原始 SK；
///   3. Canonical Query 仍按 key 字母序排列（这一点与 AWS 一致）。
///
/// 这几个动作（GetCodingPlanUsage / GetAFPUsage）请求体恒为空，签名固定基于空 body 的 SHA-256。
/// </summary>
public static class VolcengineSigner
{
    /// <summary>控制面 OpenAPI 网关主机名，固定值——不接受用户覆盖，避免设置页手误改错域名。</summary>
    public const string Host = "open.volcengineapi.com";

    public const string Service = "ark";
    public const string DefaultRegion = "cn-beijing";
    public const string DefaultVersion = "2024-01-01";

    /// <summary>固定顺序，见类型文档第 1 条。</summary>
    public const string SignedHeaders = "host;x-date;x-content-sha256;content-type";

    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>空字符串的 SHA-256（请求体恒为空），提前算好避免每次请求重复计算同一个常量。</summary>
    public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public sealed record SignedRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// 构造一次已签名的请求。<paramref name="now"/> 可注入，供单测使用固定时间得到确定性输出；
    /// 不传时取当前 UTC 时间。
    /// </summary>
    public static SignedRequest BuildSignedRequest(
        string accessKeyId,
        string secretAccessKey,
        string action,
        string region = DefaultRegion,
        string version = DefaultVersion,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var moment = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var query = CanonicalQuery(action, region, version);

        // ISO8601 基本格式（无分隔符）：20260915T101112Z。DateTimeOffset 的 "yyyyMMddTHHmmssZ"
        // 格式串对 UTC 时刻直接可用（不需要再手动去掉 -/: /毫秒，比照搬 JS 的字符串替换更不容易错）。
        var xDate = moment.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = xDate[..8];

        var canonicalHeaders =
            $"host:{Host}\n" +
            $"x-date:{xDate}\n" +
            $"x-content-sha256:{EmptyBodySha256}\n" +
            $"content-type:{ContentType}\n";

        var canonicalRequest = $"POST\n/\n{query}\n{canonicalHeaders}\n{SignedHeaders}\n{EmptyBodySha256}";
        var credentialScope = $"{shortDate}/{region}/{Service}/request";
        var stringToSign = $"HMAC-SHA256\n{xDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var kDate = HmacSha256(Encoding.UTF8.GetBytes(secretAccessKey), shortDate);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, Service);
        var kSigning = HmacSha256(kService, "request");
        // Convert.ToHexStringLower 是 .NET 9 才加入的重载；本项目目标框架是 net8.0-windows，
        // 用 ToHexString + ToLowerInvariant 保持兼容。
        var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();

        var authorization = $"HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, " +
                             $"SignedHeaders={SignedHeaders}, Signature={signature}";

        var headers = new Dictionary<string, string>
        {
            ["X-Date"] = xDate,
            ["X-Content-Sha256"] = EmptyBodySha256,
            ["Content-Type"] = ContentType,
            ["Authorization"] = authorization,
        };

        return new SignedRequest($"https://{Host}/?{query}", headers);
    }

    /// <summary>
    /// <c>Action</c>/<c>Region</c>/<c>Version</c> 三个查询参数按 key 字母序拼接（本来就是字母序，
    /// 排序调用是防御性的，避免未来加参数时漏掉）。
    /// </summary>
    internal static string CanonicalQuery(string action, string region, string version)
    {
        var pairs = new (string Key, string Value)[]
        {
            ("Action", action),
            ("Region", region),
            ("Version", version),
        };

        return string.Join('&',
            pairs.OrderBy(p => p.Key, StringComparer.Ordinal)
                 .Select(p => $"{UriEncode(p.Key)}={UriEncode(p.Value)}"));
    }

    /// <summary>
    /// RFC 3986 未保留字符（字母、数字、<c>- _ . ~</c>）原样保留，其余一律 <c>%XX</c> 大写编码。
    /// 不直接用 <see cref="Uri.EscapeDataString"/>——不同 .NET 版本对 <c>! ' ( ) *</c> 这几个字符
    /// 的处理不一致，自己实现才能保证签名跨运行时环境稳定复现。
    /// </summary>
    internal static string UriEncode(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
