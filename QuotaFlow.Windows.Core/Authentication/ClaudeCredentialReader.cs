using System.Text.Json;

namespace QuotaFlow.Windows.Core.Authentication;

/// <summary>
/// Claude 侧凭据读取结果。<see cref="AccessToken"/> 只在内存中短暂存在，调用方
/// 用完即弃，绝不允许写入日志、异常消息或缓存文件。
/// </summary>
public sealed record ClaudeCredentialResult(
    string? AccessToken,
    CredentialStatus Status,
    string? Message);

/// <summary>
/// 读取 Claude Code CLI 在本机落地的 OAuth 凭据（~/.claude/.credentials.json）。
///
/// 只读取，不做登录、不做刷新、不要求用户输入密码——这是文档 §3.1 和 §7 的硬性要求。
/// 文件格式兼容两种历史 key 名（"claudeAiOauth" / "claude.ai_oauth"），参考并已用
/// cc-switch（MIT License, https://github.com/farion1231/cc-switch）源码交叉确认。
/// </summary>
public sealed class ClaudeCredentialReader
{
    private readonly string _credentialPath;

    public ClaudeCredentialReader(string? credentialPathOverride = null)
    {
        _credentialPath = credentialPathOverride ?? GetDefaultCredentialPath();
    }

    private static string GetDefaultCredentialPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", ".credentials.json");
    }

    public ClaudeCredentialResult Read()
    {
        if (!File.Exists(_credentialPath))
        {
            return new ClaudeCredentialResult(null, CredentialStatus.NotFound, null);
        }

        string content;
        try
        {
            content = File.ReadAllText(_credentialPath);
        }
        catch (IOException ex)
        {
            // 只记录异常类型/文件路径，绝不把文件内容（可能含 token）带进消息里。
            return new ClaudeCredentialResult(null, CredentialStatus.ParseError,
                $"无法读取凭据文件：{ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ClaudeCredentialResult(null, CredentialStatus.ParseError,
                $"无法读取凭据文件：{ex.GetType().Name}");
        }

        return Parse(content);
    }

    internal static ClaudeCredentialResult Parse(string content)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new ClaudeCredentialResult(null, CredentialStatus.ParseError, "凭据文件不是合法的 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("claudeAiOauth", out var entry) &&
                !root.TryGetProperty("claude.ai_oauth", out entry))
            {
                return new ClaudeCredentialResult(null, CredentialStatus.ParseError, "凭据文件中缺少 OAuth 条目");
            }

            if (!entry.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(tokenEl.GetString()))
            {
                return new ClaudeCredentialResult(null, CredentialStatus.ParseError, "accessToken 为空或缺失");
            }

            var token = tokenEl.GetString()!;

            if (entry.TryGetProperty("expiresAt", out var expiresEl) &&
                IsExpired(expiresEl))
            {
                return new ClaudeCredentialResult(token, CredentialStatus.Expired, "OAuth token 已过期");
            }

            return new ClaudeCredentialResult(token, CredentialStatus.Valid, null);
        }
    }

    private static bool IsExpired(JsonElement expiresAt)
    {
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (expiresAt.ValueKind)
        {
            case JsonValueKind.Number when expiresAt.TryGetInt64(out var ts):
            {
                // 区分秒/毫秒时间戳：毫秒级时间戳明显大于 1e12。
                var tsSeconds = ts > 1_000_000_000_000 ? ts / 1000 : ts;
                return tsSeconds < nowSeconds;
            }
            case JsonValueKind.String when DateTimeOffset.TryParse(expiresAt.GetString(), out var dt):
                return dt.ToUnixTimeSeconds() < nowSeconds;
            default:
                // 无法解析时不视为过期——瞬时解析失败不应升级成确定性的"已过期"判断。
                return false;
        }
    }
}
