using Microsoft.AspNetCore.DataProtection;

namespace CodeSentinel.Repos;

/// <summary>
/// 仓库只读凭据的加密与解密。
/// </summary>
public sealed class CredentialProtector
{
    /// <summary>
    /// Data Protection 用途名，加密与解密双方必须一致。
    /// </summary>
    public const string Purpose = "repo-credentials";

    private readonly IDataProtector dataProtector;

    /// <summary>
    /// 初始化凭据保护器。
    /// </summary>
    /// <param name="provider">Data Protection 提供者</param>
    public CredentialProtector(IDataProtectionProvider provider)
    {
        dataProtector = provider.CreateProtector(Purpose);
    }

    /// <summary>
    /// 加密明文凭据。
    /// </summary>
    /// <param name="plain">明文凭据</param>
    /// <returns>密文凭据</returns>
    public string Protect(string plain)
    {
        ArgumentException.ThrowIfNullOrEmpty(plain);
        return dataProtector.Protect(plain);
    }

    /// <summary>
    /// 解密密文凭据。
    /// </summary>
    /// <param name="cipher">密文凭据</param>
    /// <returns>明文凭据</returns>
    public string Unprotect(string cipher)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipher);
        return dataProtector.Unprotect(cipher);
    }
}
