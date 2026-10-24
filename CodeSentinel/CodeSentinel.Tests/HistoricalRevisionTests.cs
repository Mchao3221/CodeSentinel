using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 历史版本查询集成测试：files / diff / blob / comments 接受 revisionId 并按旧版本 SHA 返回。
/// </summary>
public class HistoricalRevisionTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// files / diff / blob 传入历史版本主键时应使用该版本的 BaseSha / HeadSha，
    /// 与当前版本结果可区分，且跨版本查询不改变当前版本行为。
    /// </summary>
    [Fact]
    public async Task Files_diff_and_blob_use_historical_revision_shas()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("v1\n"),
                ["b.txt"] = Text("b1\n")
            },
            message: "base files");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v2\n") });
        await FetchMirrorAsync(factory, context.Repo);

        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, [context.Reviewer.Id]);
        var revisionOne = review.Revisions.Single(m => m.Seq == 1).Id;

        // 第二版：a.txt 再次修改，并新增 c.txt
        var headTwo = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("v3\n"),
                ["c.txt"] = Text("c1\n")
            });
        await FetchMirrorAsync(factory, context.Repo);
        Assert.True(await AddRevisionAsync(factory, review.Id, headTwo));

        // 当前版本包含 a.txt 与 c.txt 两个文件
        var current = await client.GetFromJsonAsync<DiffFileListBody>(
            $"/api/reviews/{review.Id}/files");
        Assert.NotNull(current);
        Assert.Equal(2, current.Total);
        Assert.Contains(current.Items, m => m.Path == "c.txt");

        // 历史版本 1 只有 a.txt
        var historical = await client.GetFromJsonAsync<DiffFileListBody>(
            $"/api/reviews/{review.Id}/files?revisionId={revisionOne}");
        Assert.NotNull(historical);
        Assert.Equal(1, historical.Total);
        Assert.Contains(historical.Items, m => m.Path == "a.txt");
        Assert.DoesNotContain(historical.Items, m => m.Path == "c.txt");

        // blob：NEW 侧当前版本为 v3，历史版本 1 为 v2
        var currentBlob = await client.GetFromJsonAsync<BlobContentBody>(
            $"/api/reviews/{review.Id}/blob?path=a.txt&side=NEW");
        Assert.NotNull(currentBlob);
        Assert.Equal("v3\n", currentBlob.Content);

        var historicalBlob = await client.GetFromJsonAsync<BlobContentBody>(
            $"/api/reviews/{review.Id}/blob?path=a.txt&side=NEW&revisionId={revisionOne}");
        Assert.NotNull(historicalBlob);
        Assert.Equal("v2\n", historicalBlob.Content);

        // diff：历史版本 1 的新增行文本为 v2
        var historicalDiff = await client.GetFromJsonAsync<DiffFileBody>(
            $"/api/reviews/{review.Id}/diff?path=a.txt&revisionId={revisionOne}");
        Assert.NotNull(historicalDiff);
        Assert.Contains(
            historicalDiff.Hunks.SelectMany(m => m.Lines),
            m => m.Type == "ADD" && m.Text == "v2");
    }

    /// <summary>
    /// revisionId 属于其它评审单时，files 与 comments 都按不存在返回 404 NOT_FOUND。
    /// </summary>
    [Fact]
    public async Task Revision_from_another_review_returns_404()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v2\n") });
        await source.CreateBranchFromMainAsync("feature-2");
        await source.CommitChangesAsync(
            "feature-2",
            new Dictionary<string, byte[]> { ["other.txt"] = Text("other\n") });
        await FetchMirrorAsync(factory, context.Repo);

        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, [context.Reviewer.Id]);
        var foreign = await CreateReviewAsync(
            client, context.Repo.Id, [context.Reviewer.Id], "feature-2");
        var foreignRevision = foreign.Revisions.Single().Id;

        var files = await client.GetAsync(
            $"/api/reviews/{review.Id}/files?revisionId={foreignRevision}");
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);
        Assert.Equal(
            "NOT_FOUND",
            (await files.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var comments = await client.GetAsync(
            $"/api/reviews/{review.Id}/comments?revisionId={foreignRevision}");
        Assert.Equal(HttpStatusCode.NotFound, comments.StatusCode);
        Assert.Equal(
            "NOT_FOUND",
            (await comments.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// comments 传入 revisionId 时只返回该版本创建的评论，省略时返回全部版本。
    /// </summary>
    [Fact]
    public async Task Comments_are_filtered_by_revision()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v1\n") },
            message: "base");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v2\n") });
        await FetchMirrorAsync(factory, context.Repo);

        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, [context.Reviewer.Id]);
        var revisionOne = review.Revisions.Single(m => m.Seq == 1).Id;

        var first = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 1, "第一版评论");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 第二版只新增 c.txt，a.txt 保持不变
        var headTwo = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["c.txt"] = Text("c1\n") });
        await FetchMirrorAsync(factory, context.Repo);
        Assert.True(await AddRevisionAsync(factory, review.Id, headTwo));

        var second = await CreateCommentAsync(client, review.Id, "c.txt", "NEW", 1, "第二版评论");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<CommentBody>())!.Id;
        var secondId = (await second.Content.ReadFromJsonAsync<CommentBody>())!.Id;

        // 按第一版过滤只返回第一版评论
        var filtered = await client.GetFromJsonAsync<CommentListBody>(
            $"/api/reviews/{review.Id}/comments?revisionId={revisionOne}");
        Assert.NotNull(filtered);
        Assert.Equal([firstId], filtered.Items.Select(m => m.Id).ToArray());

        // 不带 revisionId 返回全部
        var all = await client.GetFromJsonAsync<CommentListBody>(
            $"/api/reviews/{review.Id}/comments");
        Assert.NotNull(all);
        Assert.Equal(2, all.Items.Count);
        Assert.Contains(all.Items, m => m.Id == firstId);
        Assert.Contains(all.Items, m => m.Id == secondId);
    }

    /// <summary>
    /// 准备仓库、提交人、审核人与成员关系，并把远端分支拉进本地镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>历史版本测试上下文</returns>
    private static async Task<HistoricalContext> SeedContextAsync(
        TestAppFactory factory,
        TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("historical"));
        SeedMember(factory, repo.Id, submitter.Id);
        SeedMember(factory, repo.Id, reviewer.Id);
        await FetchMirrorAsync(factory, repo);
        return new HistoricalContext(source, repo, submitter, reviewer);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    private static void SeedMember(TestAppFactory factory, Guid repoId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepoMembers.Add(new RepoMember
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            UserId = userId,
            IsMerger = false
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
    /// 通过 HTTP 创建评审单并返回详情。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <param name="headBranch">提交分支，默认 feature</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        IReadOnlyList<Guid> reviewerIds,
        string headBranch = HeadBranch)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch,
                baseBranch = BaseBranch,
                title = UniqueName("历史版本"),
                description = (string?)null,
                reviewerIds
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 直接调用版本服务生成新版本。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <returns>是否生成了新版本</returns>
    private static async Task<bool> AddRevisionAsync(
        TestAppFactory factory,
        Guid reviewId,
        string newHeadSha)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RevisionService>();
        return await service.AddRevisionAsync(reviewId, newHeadSha, CancellationToken.None);
    }

    /// <summary>
    /// 调用创建评论接口。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="side">OLD 或 NEW</param>
    /// <param name="line">行号</param>
    /// <param name="body">评论内容</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> CreateCommentAsync(
        HttpClient client,
        Guid reviewId,
        string path,
        string side,
        int line,
        string body)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/comments",
            new { path, side, line, body });
    }

    /// <summary>
    /// 把 UTF-8 文本转成字节数组。
    /// </summary>
    /// <param name="value">文本</param>
    /// <returns>字节内容</returns>
    private static byte[] Text(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    /// <summary>
    /// 生成不重复的用户名或仓库名。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 历史版本测试上下文：真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    private sealed record HistoricalContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer);
}
