using System.Text.RegularExpressions;

namespace CodeSentinel.Git;

/// <summary>
/// 仓库 URL 脱敏：把内嵌的用户名密码替换为固定标记，供日志与异常信息使用。
/// </summary>
public static class GitUrlRedactor
{
    /// <summary>
    /// 匹配 "协议://用户信息@" 形式的前缀。
    /// </summary>
    private static readonly Regex UserInfoPattern = new(
        @"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@",
        RegexOptions.Compiled);

    /// <summary>
    /// 脱敏字符串中所有内嵌凭据的 URL。
    /// </summary>
    /// <param name="value">原始字符串</param>
    /// <returns>脱敏后的字符串，null 与空串原样返回</returns>
    public static string? Redact(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : UserInfoPattern.Replace(value, "$1***@");
    }
}
