using CodeSentinel.Common;
using CodeSentinel.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSentinel.Tests;

/// <summary>
/// 镜像仓库与连接测试。
/// </summary>
public class MirrorTests
{
    /// <summary>
    /// 建镜像并 fetch 后应生成 origin 分支引用，且能读取分支与 head。
    /// </summary>
    [Fact]
    public async Task Ensure_and_fetch_create_remote_branch_refs()
    {
        using var source = await TestGitRepo.CreateAsync();
        var storageRoot = TestPaths.CreateTempDirectory("codesentinel-mirror");
        var repoId = Guid.NewGuid();

        try
        {
            var mirror = CreateMirrorService(storageRoot);
            await mirror.EnsureAsync(repoId, source.Url, null, CancellationToken.None);
            await mirror.FetchAsync(repoId, source.Url, null, CancellationToken.None);

            var mirrorPath = mirror.GetMirrorPath(repoId);
            Assert.True(Directory.Exists(mirrorPath));
            Assert.True(File.Exists(Path.Combine(mirrorPath, "refs", "remotes", "origin", "main")));

            var branches = await mirror.ListBranchesAsync(repoId, CancellationToken.None);
            var main = Assert.Single(branches);
            Assert.Equal("main", main.Name);
            Assert.Equal(source.MainSha, main.HeadSha);

            var head = await mirror.GetHeadAsync(repoId, "main", CancellationToken.None);
            Assert.Equal(source.MainSha, head);
            Assert.True(await mirror.IsAncestorAsync(repoId, head!, head!, CancellationToken.None));
        }
        finally
        {
            TestPaths.DeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 不存在的分支返回 null，而不是抛出异常。
    /// </summary>
    [Fact]
    public async Task Get_head_returns_null_for_missing_branch()
    {
        using var source = await TestGitRepo.CreateAsync();
        var storageRoot = TestPaths.CreateTempDirectory("codesentinel-mirror");
        var repoId = Guid.NewGuid();

        try
        {
            var mirror = CreateMirrorService(storageRoot);
            await mirror.EnsureAsync(repoId, source.Url, null, CancellationToken.None);

            var head = await mirror.GetHeadAsync(repoId, "not-a-branch", CancellationToken.None);

            Assert.Null(head);
        }
        finally
        {
            TestPaths.DeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 连接测试成功时不抛异常。
    /// </summary>
    [Fact]
    public async Task Test_connection_succeeds_for_reachable_repo()
    {
        using var source = await TestGitRepo.CreateAsync();
        var storageRoot = TestPaths.CreateTempDirectory("codesentinel-mirror");

        try
        {
            var mirror = CreateMirrorService(storageRoot);
            await mirror.TestAsync(source.Url, null, CancellationToken.None);
        }
        finally
        {
            TestPaths.DeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 连接失败必须抛出 REPO_UNREACHABLE，且错误信息经过 URL 脱敏。
    /// </summary>
    [Fact]
    public async Task Test_connection_throws_repo_unreachable_on_bad_url()
    {
        var storageRoot = TestPaths.CreateTempDirectory("codesentinel-mirror");
        var mirror = CreateMirrorService(storageRoot);

        try
        {
            var exception = await Assert.ThrowsAsync<ApiException>(
                () => mirror.TestAsync("http://127.0.0.1:1/repo.git", null, CancellationToken.None));

            Assert.Equal(400, exception.Status);
            Assert.Equal("REPO_UNREACHABLE", exception.Code);
            Assert.Contains("无法连接仓库", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestPaths.DeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 构造镜像服务，存储根目录使用测试临时目录。
    /// </summary>
    /// <param name="storageRoot">存储根目录</param>
    /// <returns>镜像服务</returns>
    private static MirrorService CreateMirrorService(string storageRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Root"] = storageRoot
            })
            .Build();

        var git = new GitCli(configuration, NullLogger<GitCli>.Instance);
        return new MirrorService(git, storageRoot);
    }
}
