using CodeSentinel.Data.Entities;

namespace CodeSentinel.Git;

/// <summary>
/// git 进程使用的只读凭据，只通过子进程环境变量注入，不写入命令行或 URL。
/// </summary>
/// <param name="Username">HTTP 用户名</param>
/// <param name="Password">HTTP 密码或访问令牌</param>
public sealed record GitCredentials(string Username, string Password)
{
    /// <summary>
    /// 令牌凭据使用的固定用户名。
    /// </summary>
    public const string TokenUsername = "token";

    /// <summary>
    /// 未显式提供用户名时的默认值。
    /// </summary>
    public const string DefaultUsername = "git";

    /// <summary>
    /// 把入库的单一凭据字符串转换为 git 凭据：
    /// 允许 "用户名:密码/令牌" 形式，未提供用户名时按类型取默认值。
    /// </summary>
    /// <param name="kind">凭据类型</param>
    /// <param name="secret">解密后的凭据明文</param>
    /// <returns>git 凭据</returns>
    public static GitCredentials FromStored(RepoCredentialKind kind, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var separator = secret.IndexOf(':');
        if (separator > 0)
        {
            return new GitCredentials(secret[..separator], secret[(separator + 1)..]);
        }

        var defaultUsername = kind == RepoCredentialKind.Token ? TokenUsername : DefaultUsername;
        return new GitCredentials(defaultUsername, secret);
    }

    /// <summary>
    /// 输出脱敏后的凭据描述，避免密码或令牌进入日志与错误信息。
    /// </summary>
    /// <returns>脱敏描述</returns>
    public override string ToString()
    {
        return $"GitCredentials {{ Username = {Username}, Password = *** }}";
    }
}
