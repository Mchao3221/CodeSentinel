using CodeSentinel.Common;

namespace CodeSentinel.Repos;

/// <summary>
/// 仓库字段校验与归一化。
/// </summary>
public static class RepoValidation
{
    private const int MaxUrlLength = 500;
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string FileScheme = "file";

    /// <summary>
    /// 校验并归一化仓库地址：仅允许 http/https，测试环境额外允许 file。
    /// </summary>
    /// <param name="url">原始地址</param>
    /// <param name="allowFile">是否允许 file 协议</param>
    /// <returns>归一化后的地址，协议与主机名统一小写，路径与查询串保持原样</returns>
    public static string NormalizeUrl(string? url, bool allowFile)
    {
        var trimmed = url?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw InvalidUrl(allowFile);
        }

        if (!IsSchemeAllowed(uri.Scheme, allowFile) || HasEmbeddedCredential(uri) || !HasValidHost(uri))
        {
            throw InvalidUrl(allowFile);
        }

        var normalized = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.PathAndQuery}";
        if (normalized.Length > MaxUrlLength)
        {
            throw InvalidUrl(allowFile);
        }

        return normalized;
    }

    /// <summary>
    /// 判断地址协议是否允许。
    /// </summary>
    /// <param name="scheme">协议名</param>
    /// <param name="allowFile">是否允许 file 协议</param>
    /// <returns>是否允许</returns>
    private static bool IsSchemeAllowed(string scheme, bool allowFile)
    {
        if (string.Equals(scheme, HttpScheme, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowFile && string.Equals(scheme, FileScheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断地址中是否内嵌了用户名或密码。
    /// </summary>
    /// <param name="uri">已解析的地址</param>
    /// <returns>是否内嵌凭据</returns>
    private static bool HasEmbeddedCredential(Uri uri)
    {
        return !string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// 判断地址是否带有必要的主机名，file 协议允许主机名为空。
    /// </summary>
    /// <param name="uri">已解析的地址</param>
    /// <returns>主机名是否有效</returns>
    private static bool HasValidHost(Uri uri)
    {
        return string.Equals(uri.Scheme, FileScheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>
    /// 构造统一的不合法地址异常。
    /// </summary>
    /// <param name="allowFile">是否允许 file 协议</param>
    /// <returns>业务异常</returns>
    private static ApiException InvalidUrl(bool allowFile)
    {
        var message = allowFile
            ? "仓库地址必须是有效的 http、https 或 file 地址，且不能包含内嵌凭据"
            : "仓库地址必须是有效的 http 或 https 地址，且不能包含内嵌凭据";

        return new ApiException(StatusCodes.Status400BadRequest, "INVALID_REPO_URL", message);
    }
}
