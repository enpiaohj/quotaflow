using System.Text.Json;

namespace QuotaFlow.Windows.Core.Authentication;

/// <summary>
/// Codex 侧凭据读取结果。<see cref="AccessToken"/> 只在内存中短暂存在，用完即弃。
/// </summary>
public sealed record CodexCredentialResult(
    string? AccessToken,
    string? AccountId,
    CredentialStatus Status,
    string? Message);

/// <summary>
/// 读取 Codex CLI 在本机落地的 OAuth 凭据（~/.codex/auth.json）。
///
/// 只有 auth_mode == "chatgpt"（即 ChatGPT OAuth 登录）才携带可用于查询订阅额度的
/// access_token；API Key 模式（auth_mode == "apikey"）没有对应的订阅用量体系，
/// 文档 §3.2 明确要求不要用 OpenAI Admin Key 顶替。
/// 参考并已用 cc-switch（MIT License）源码交叉确认字段结构。
/// </summary>
public sealed class CodexCredentialReader
{
    private readonly string _authPath;

    public CodexCredentialReader(string? authPathOverride = null)
    {
        _authPath = authPathOverride ?? GetDefaultAuthPath();
    }

    private static string GetDefaultAuthPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "auth.json");
    }

    public CodexCredentialResult Read()
    {
        if (!File.Exists(_authPath))
        {
            return new CodexCredentialResult(null, null, CredentialStatus.NotFound, null);
        }

        string content;
        try
        {
            content = File.ReadAllText(_authPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodexCredentialResult(null, null, CredentialStatus.ParseError,
                $"无法读取凭据文件：{ex.GetType().Name}");
        }

        return Parse(content);
    }

    internal static CodexCredentialResult Parse(string content)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new CodexCredentialResult(null, null, CredentialStatus.ParseError, "凭据文件不是合法的 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var authMode = root.TryGetProperty("auth_mode", out var modeEl) ? modeEl.GetString() : null;
            if (authMode != "chatgpt")
            {
                return new CodexCredentialResult(null, null, CredentialStatus.NotFound,
                    "Codex 当前未使用 ChatGPT OAuth 登录模式（可能是 API Key 模式），无法查询订阅额度");
            }

            if (!root.TryGetProperty("tokens", out var tokens))
            {
                return new CodexCredentialResult(null, null, CredentialStatus.ParseError, "凭据文件中缺少 tokens 字段");
            }

            if (!tokens.TryGetProperty("access_token", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(tokenEl.GetString()))
            {
                return new CodexCredentialResult(null, null, CredentialStatus.ParseError, "access_token 为空或缺失");
            }

            var token = tokenEl.GetString()!;
            var accountId = tokens.TryGetProperty("account_id", out var accountEl)
                ? accountEl.GetString()
                : null;

            if (root.TryGetProperty("last_refresh", out var lastRefreshEl) &&
                lastRefreshEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(lastRefreshEl.GetString(), out var lastRefresh))
            {
                // Codex CLI 在距上次刷新 > 8 天时会自动刷新；超过这个窗口视为"可能已过期"，
                // 但仍然把 token 一并返回——调用方可以照 cc-switch 的做法"过期也试一把"。
                var age = DateTimeOffset.UtcNow - lastRefresh;
                if (age > TimeSpan.FromDays(8))
                {
                    return new CodexCredentialResult(token, accountId, CredentialStatus.Expired,
                        "Codex token 可能已过期（距上次刷新超过 8 天）");
                }
            }

            return new CodexCredentialResult(token, accountId, CredentialStatus.Valid, null);
        }
    }
}
