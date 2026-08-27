namespace QuotaFlow.Windows.Core.Authentication;

/// <summary>本机 CLI 凭据文件的读取结果状态。</summary>
public enum CredentialStatus
{
    /// <summary>凭据有效，可以直接用于请求。</summary>
    Valid,

    /// <summary>凭据已过期（或距上次刷新过久，可能已过期）。</summary>
    Expired,

    /// <summary>找不到凭据文件——对应 CLI 未安装或未登录。</summary>
    NotFound,

    /// <summary>凭据文件存在但无法解析——文件损坏或上游格式发生了变化。</summary>
    ParseError,
}
