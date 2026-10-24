using CodeSentinel.Data.Entities;
using CodeSentinel.Git;

namespace CodeSentinel.Repos;

/// <summary>
/// 仓库凭据读取：把库中密文解密并转换为 git 进程凭据。
/// </summary>
public static class RepoCredentials
{
    /// <summary>
    /// 解密仓库已保存的只读凭据，未保存时返回空。
    /// </summary>
    /// <param name="protector">凭据解密器</param>
    /// <param name="repo">仓库</param>
    /// <returns>git 凭据或 null</returns>
    public static GitCredentials? Read(CredentialProtector protector, Repo repo)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(repo);

        if (repo.CredentialCipher is null || repo.CredentialKind is null)
        {
            return null;
        }

        return GitCredentials.FromStored(
            repo.CredentialKind.Value,
            protector.Unprotect(repo.CredentialCipher));
    }
}
