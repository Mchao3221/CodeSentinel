using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Auth;

/// <summary>
/// 基于 PasswordHasher 的账号密码校验与哈希服务。
/// </summary>
/// <param name="db">数据库上下文</param>
public sealed class PasswordAuthService(AppDbContext db)
{
    /// <summary>
    /// 密码最小长度。
    /// </summary>
    public const int MinPasswordLength = 8;

    private readonly PasswordHasher<User> passwordHasher = new();

    /// <summary>
    /// 校验用户名与密码，成功时返回用户。
    /// </summary>
    /// <param name="username">登录名（大小写不敏感）</param>
    /// <param name="password">明文密码</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>校验通过的用户，失败返回空</returns>
    public async Task<User?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var normalizedUsername = username.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            m => m.Username.ToLower() == normalizedUsername,
            ct);

        if (user is null)
        {
            return null;
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }

    /// <summary>
    /// 校验指定用户的密码。
    /// </summary>
    /// <param name="user">用户</param>
    /// <param name="password">明文密码</param>
    /// <returns>是否正确</returns>
    public bool VerifyPassword(User user, string password)
    {
        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result != PasswordVerificationResult.Failed;
    }

    /// <summary>
    /// 生成密码哈希。
    /// </summary>
    /// <param name="user">用户</param>
    /// <param name="password">明文密码</param>
    /// <returns>密码哈希</returns>
    public string HashPassword(User user, string password)
    {
        return passwordHasher.HashPassword(user, password);
    }

    /// <summary>
    /// 校验密码是否符合最小长度要求，不符合时抛出 400。
    /// </summary>
    /// <param name="password">明文密码</param>
    public static void EnsureValidPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PASSWORD",
                $"密码长度至少为 {MinPasswordLength} 位");
        }
    }
}
