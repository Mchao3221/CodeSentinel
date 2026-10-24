using CodeSentinel.Common;
using Microsoft.Extensions.Configuration;

namespace CodeSentinel.Git;

/// <summary>
/// 仓库镜像与连接测试：负责远端连通性校验、裸镜像创建、fetch 与引用读取。
/// </summary>
public sealed class MirrorService
{
    /// <summary>
    /// 默认远端名称。
    /// </summary>
    public const string RemoteName = "origin";

    private const string MirrorsDirectoryName = "mirrors";
    private const string RemoteRefPrefix = "refs/remotes/origin/";
    private const string RemoteRefDisplayPrefix = "origin/";
    private const string FetchRefSpec = "+refs/heads/*:refs/remotes/origin/*";
    private const string DefaultStorageRoot = "data";

    private readonly GitCli _git;
    private readonly string _storageRoot;

    /// <summary>
    /// 按应用配置创建镜像服务，Storage:Root 相对内容根目录解析。
    /// </summary>
    /// <param name="git">git 命令封装</param>
    /// <param name="configuration">应用配置</param>
    /// <param name="environment">宿主环境</param>
    public MirrorService(GitCli git, IConfiguration configuration, IHostEnvironment environment)
        : this(git, ResolveStorageRoot(configuration, environment.ContentRootPath))
    {
    }

    /// <summary>
    /// 使用已解析的存储根目录创建镜像服务。
    /// </summary>
    /// <param name="git">git 命令封装</param>
    /// <param name="storageRoot">存储根目录</param>
    public MirrorService(GitCli git, string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _git = git;
        _storageRoot = Path.GetFullPath(storageRoot);
    }

    /// <summary>
    /// 计算仓库镜像路径：{Storage:Root}/mirrors/{repoId}.git。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <returns>镜像目录绝对路径</returns>
    public string GetMirrorPath(Guid repoId)
    {
        return Path.Combine(_storageRoot, MirrorsDirectoryName, $"{repoId}.git");
    }

    /// <summary>
    /// 用 git ls-remote 验证远端连通性与凭据，失败时抛出 REPO_UNREACHABLE。
    /// </summary>
    /// <param name="url">仓库地址</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>验证任务</returns>
    public async Task TestAsync(string url, GitCredentials? credentials, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var result = await _git.RunAsync(
            Path.GetTempPath(),
            ["ls-remote", "--heads", url],
            credentials,
            ct);

        if (result.ExitCode != 0)
        {
            throw Unreachable(result, credentials);
        }
    }

    /// <summary>
    /// 幂等创建裸镜像并维护 origin 地址。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="url">仓库地址</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建或更新任务</returns>
    public async Task EnsureAsync(
        Guid repoId,
        string url,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var mirrorPath = GetMirrorPath(repoId);
        if (!IsInitialized(mirrorPath))
        {
            Directory.CreateDirectory(mirrorPath);
            var init = await _git.RunAsync(mirrorPath, ["init", "--bare", "."], credentials, ct);
            EnsureSucceeded(init, "初始化镜像失败", credentials);
        }

        await EnsureRemoteAsync(mirrorPath, url, credentials, ct);
    }

    /// <summary>
    /// 拉取远端所有分支到 refs/remotes/origin/*，并清理已删除分支。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="url">仓库地址</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>fetch 任务</returns>
    public async Task FetchAsync(
        Guid repoId,
        string url,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        await EnsureAsync(repoId, url, credentials, ct);

        var result = await _git.RunAsync(
            GetMirrorPath(repoId),
            ["fetch", "--prune", RemoteName, FetchRefSpec],
            credentials,
            ct);

        if (result.ExitCode != 0)
        {
            throw Unreachable(result, credentials);
        }
    }

    /// <summary>
    /// 读取镜像中的远端分支列表，去掉 origin/ 前缀与 origin/HEAD。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分支名与 head SHA</returns>
    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(Guid repoId, CancellationToken ct)
    {
        var result = await _git.RunAsync(
            GetMirrorPath(repoId),
            ["for-each-ref", "--format=%(refname:short) %(objectname)", "refs/remotes/origin"],
            null,
            ct);

        EnsureSucceeded(result, "读取镜像分支失败", null);
        return ParseBranches(result.StdOut);
    }

    /// <summary>
    /// 读取镜像中某个分支的 head SHA，分支不存在时返回 null。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="branch">分支名，允许传完整引用</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>提交 SHA 或 null</returns>
    public async Task<string?> GetHeadAsync(Guid repoId, string branch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (!Directory.Exists(GetMirrorPath(repoId)))
        {
            return null;
        }

        var reference = branch.StartsWith("refs/", StringComparison.Ordinal)
            ? branch
            : $"{RemoteRefPrefix}{branch}";

        var result = await _git.RunAsync(
            GetMirrorPath(repoId),
            ["rev-parse", "--verify", "--quiet", reference],
            null,
            ct);

        var sha = result.StdOut.Trim();
        return result.ExitCode == 0 && sha.Length > 0 ? sha : null;
    }

    /// <summary>
    /// 判断 ancestor 是否为 descendant 的祖先，供“分支领先目标分支”校验使用。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ancestor">候选祖先提交</param>
    /// <param name="descendant">候选后代提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否为祖先关系</returns>
    public async Task<bool> IsAncestorAsync(
        Guid repoId,
        string ancestor,
        string descendant,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ancestor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descendant);

        var result = await _git.RunAsync(
            GetMirrorPath(repoId),
            ["merge-base", "--is-ancestor", ancestor, descendant],
            null,
            ct);

        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw GitFailed(result, "判断提交祖先关系失败")
        };
    }

    /// <summary>
    /// 把 git 输出解析为分支列表，忽略 origin/HEAD 与非法行。
    /// </summary>
    /// <param name="stdOut">for-each-ref 输出</param>
    /// <returns>按分支名排序的分支列表</returns>
    private static IReadOnlyList<BranchInfo> ParseBranches(string stdOut)
    {
        var branches = new List<BranchInfo>();

        foreach (var rawLine in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var reference = line[..separator];
            var sha = line[(separator + 1)..].Trim();
            if (!reference.StartsWith(RemoteRefDisplayPrefix, StringComparison.Ordinal)
                || string.Equals(reference, "origin/HEAD", StringComparison.Ordinal)
                || sha.Length == 0)
            {
                continue;
            }

            branches.Add(new BranchInfo(
                reference[RemoteRefDisplayPrefix.Length..],
                sha));
        }

        return branches.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 判断镜像是否已经初始化。
    /// </summary>
    /// <param name="mirrorPath">镜像目录</param>
    /// <returns>是否已完成 git init --bare</returns>
    private static bool IsInitialized(string mirrorPath)
    {
        return Directory.Exists(mirrorPath)
            && File.Exists(Path.Combine(mirrorPath, "HEAD"));
    }

    /// <summary>
    /// 校验并更新 origin 地址：不存在则 add，存在且不一致则 set-url。
    /// </summary>
    /// <param name="mirrorPath">镜像目录</param>
    /// <param name="url">仓库地址</param>
    /// <param name="credentials">只读凭据</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新任务</returns>
    private async Task EnsureRemoteAsync(
        string mirrorPath,
        string url,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        var current = await _git.RunAsync(
            mirrorPath,
            ["remote", "get-url", RemoteName],
            credentials,
            ct);

        if (current.ExitCode != 0)
        {
            var added = await _git.RunAsync(
                mirrorPath,
                ["remote", "add", RemoteName, url],
                credentials,
                ct);
            EnsureSucceeded(added, "添加远端地址失败", credentials);
            return;
        }

        if (string.Equals(current.StdOut.Trim(), url, StringComparison.Ordinal))
        {
            return;
        }

        var updated = await _git.RunAsync(
            mirrorPath,
            ["remote", "set-url", RemoteName, url],
            credentials,
            ct);
        EnsureSucceeded(updated, "更新远端地址失败", credentials);
    }

    /// <summary>
    /// 校验 git 命令成功，失败时抛出通用 git 错误。
    /// </summary>
    /// <param name="result">git 执行结果</param>
    /// <param name="message">错误描述</param>
    /// <param name="credentials">凭据，用于额外脱敏</param>
    private static void EnsureSucceeded(GitResult result, string message, GitCredentials? credentials)
    {
        if (result.ExitCode != 0)
        {
            throw GitFailed(result, message, credentials);
        }
    }

    /// <summary>
    /// 构造连接失败异常，错误信息经过 URL 与凭据脱敏。
    /// </summary>
    /// <param name="result">git 执行结果</param>
    /// <param name="credentials">只读凭据</param>
    /// <returns>业务异常</returns>
    private static ApiException Unreachable(GitResult result, GitCredentials? credentials)
    {
        var detail = Redact(result.StdErr, credentials);
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "REPO_UNREACHABLE",
            string.IsNullOrWhiteSpace(detail) ? "无法连接仓库" : $"无法连接仓库：{detail}");
    }

    /// <summary>
    /// 构造通用 git 失败异常，错误信息经过脱敏。
    /// </summary>
    /// <param name="result">git 执行结果</param>
    /// <param name="message">错误描述</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <returns>业务异常</returns>
    private static ApiException GitFailed(
        GitResult result,
        string message,
        GitCredentials? credentials = null)
    {
        var detail = Redact(result.StdErr, credentials);
        return new ApiException(
            StatusCodes.Status500InternalServerError,
            "GIT_FAILED",
            string.IsNullOrWhiteSpace(detail) ? message : $"{message}：{detail}");
    }

    /// <summary>
    /// 脱敏 git 输出中的 URL 与明文密码。
    /// </summary>
    /// <param name="value">原始输出</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <returns>脱敏后的文本</returns>
    private static string Redact(string? value, GitCredentials? credentials)
    {
        var redacted = GitUrlRedactor.Redact(value?.Trim()) ?? string.Empty;
        if (credentials is not null && !string.IsNullOrEmpty(credentials.Password))
        {
            redacted = redacted.Replace(credentials.Password, "***", StringComparison.Ordinal);
        }

        return redacted;
    }

    /// <summary>
    /// 解析存储根目录，相对路径以内容根目录为基准。
    /// </summary>
    /// <param name="configuration">应用配置</param>
    /// <param name="contentRootPath">内容根目录</param>
    /// <returns>绝对路径</returns>
    private static string ResolveStorageRoot(IConfiguration configuration, string contentRootPath)
    {
        var root = configuration.GetValue("Storage:Root", DefaultStorageRoot) ?? DefaultStorageRoot;
        return Path.GetFullPath(root, contentRootPath);
    }
}
