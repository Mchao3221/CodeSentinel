using System.Net;
using System.Net.Http.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 分支查询与“未开单分支”测试。
/// </summary>
public class BranchQueryTests
{
    /// <summary>
    /// 未开单分支应排除默认分支、已有活跃评审单的分支与不领先默认分支的分支。
    /// </summary>
    [Fact]
    public async Task Unreviewed_branches_exclude_default_branch_and_active_reviews()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();

        // 先制造分叉：diverged 从旧 main 拉出，随后 main 前进
        await source.CreateBranchAsync("diverged");
        await source.CommitOnMainAsync();

        var featureOneSha = await source.CreateBranchAsync("feature-one");
        var featureTwoSha = await source.CreateBranchAsync("feature-two", 2);
        var closedReviewSha = await source.CreateBranchAsync("closed-review");
        var openReviewSha = await source.CreateBranchAsync("under-review");
        var approvedReviewSha = await source.CreateBranchAsync("approved-review");

        var admin = await factory.LoginAsAdminAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("unreviewed"));
        SeedReview(factory, repo.Id, "under-review", ReviewStatus.Open, openReviewSha);
        SeedReview(factory, repo.Id, "approved-review", ReviewStatus.Approved, approvedReviewSha);
        SeedReview(factory, repo.Id, "closed-review", ReviewStatus.Closed, closedReviewSha);
        await FetchMirrorAsync(factory, repo);

        var body = await admin.GetFromJsonAsync<UnreviewedBranchesBody>(
            $"/api/repos/{repo.Id}/branches/unreviewed");

        Assert.NotNull(body);
        Assert.Equal(3, body.Items.Count);

        // 按 aheadBy 降序、同值按分支名升序
        Assert.Equal("feature-two", body.Items[0].Name);
        Assert.Equal(2, body.Items[0].AheadBy);
        Assert.Equal(featureTwoSha, body.Items[0].HeadSha);

        Assert.Equal("closed-review", body.Items[1].Name);
        Assert.Equal(1, body.Items[1].AheadBy);

        Assert.Equal("feature-one", body.Items[2].Name);
        Assert.Equal(1, body.Items[2].AheadBy);
        Assert.Equal(featureOneSha, body.Items[2].HeadSha);
    }

    /// <summary>
    /// 默认分支在镜像中不存在时返回空列表，不能返回 500。
    /// </summary>
    [Fact]
    public async Task Unreviewed_returns_empty_when_default_branch_missing()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("missing-base"), defaultBaseBranch: "release");
        await FetchMirrorAsync(factory, repo);

        var response = await admin.GetAsync($"/api/repos/{repo.Id}/branches/unreviewed");
        var body = await response.Content.ReadFromJsonAsync<UnreviewedBranchesBody>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    /// <summary>
    /// 分支列表支持大小写敏感的前缀过滤，且只读取本地镜像。
    /// </summary>
    [Fact]
    public async Task Branches_support_prefix_filter()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        await source.CreateBranchAsync("feature/alpha");
        await source.CreateBranchAsync("feature/beta", 2);
        await source.CreateBranchAsync("bugfix/gamma");

        var admin = await factory.LoginAsAdminAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("prefix"));
        await FetchMirrorAsync(factory, repo);

        var filtered = await admin.GetFromJsonAsync<BranchListBody>(
            $"/api/repos/{repo.Id}/branches?q=feature/");
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered.Items.Count);
        Assert.All(filtered.Items, m => Assert.StartsWith("feature/", m.Name, StringComparison.Ordinal));

        // 前缀过滤区分大小写
        var caseSensitive = await admin.GetFromJsonAsync<BranchListBody>(
            $"/api/repos/{repo.Id}/branches?q=Feature");
        Assert.NotNull(caseSensitive);
        Assert.Empty(caseSensitive.Items);

        var all = await admin.GetFromJsonAsync<BranchListBody>($"/api/repos/{repo.Id}/branches");
        Assert.NotNull(all);
        Assert.Equal(4, all.Items.Count);
        Assert.Contains(all.Items, m => m.Name == "main");
    }

    /// <summary>
    /// 镜像不存在时分支接口返回空列表，也不得创建空镜像目录。
    /// </summary>
    [Fact]
    public async Task Branches_return_empty_when_mirror_missing()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("no-mirror"));

        var response = await admin.GetAsync($"/api/repos/{repo.Id}/branches");
        var body = await response.Content.ReadFromJsonAsync<BranchListBody>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Empty(body.Items);

        var mirror = factory.Services.GetRequiredService<MirrorService>();
        Assert.False(Directory.Exists(mirror.GetMirrorPath(repo.Id)));
    }

    /// <summary>
    /// 写入一条评审单，用于构造活跃/非活跃评审分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="headBranch">评审分支</param>
    /// <param name="status">评审状态</param>
    /// <param name="headSha">评审分支提交</param>
    private static void SeedReview(
        TestAppFactory factory,
        Guid repoId,
        string headBranch,
        ReviewStatus status,
        string headSha)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Reviews.Add(new Review
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            BaseBranch = "main",
            HeadBranch = headBranch,
            SubmitterId = factory.AdminUserId,
            Title = headBranch,
            Status = status,
            BaseSha = headSha,
            HeadSha = headSha
        });
        db.SaveChanges();
    }

    /// <summary>
    /// 用真实 git 把远端分支拉进本地镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repo">仓库</param>
    /// <returns></returns>
    private static async Task FetchMirrorAsync(TestAppFactory factory, Repo repo)
    {
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        await mirror.FetchAsync(repo.Id, repo.GitUrl, null, CancellationToken.None);
    }

    /// <summary>
    /// 生成不重复的仓库名称。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }
}

/// <summary>
/// 分支项响应 DTO。
/// </summary>
/// <param name="Name">分支名</param>
/// <param name="HeadSha">分支 head 提交</param>
public sealed record BranchItemBody(string Name, string HeadSha);

/// <summary>
/// 分支列表响应 DTO。
/// </summary>
/// <param name="Items">分支列表</param>
public sealed record BranchListBody(IReadOnlyList<BranchItemBody> Items);

/// <summary>
/// 未开单分支项响应 DTO。
/// </summary>
/// <param name="Name">分支名</param>
/// <param name="HeadSha">分支 head 提交</param>
/// <param name="AheadBy">领先默认分支的提交数</param>
public sealed record UnreviewedBranchItemBody(string Name, string HeadSha, int AheadBy);

/// <summary>
/// 未开单分支列表响应 DTO。
/// </summary>
/// <param name="Items">分支列表</param>
public sealed record UnreviewedBranchesBody(IReadOnlyList<UnreviewedBranchItemBody> Items);
