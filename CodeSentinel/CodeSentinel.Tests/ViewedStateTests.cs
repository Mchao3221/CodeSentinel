using System.Diagnostics;
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
/// 逐文件“已看过”集成测试：在真实 SQLite、真实 git 仓库与真实 HTTP 上验证标记、读取与跨版本语义。
/// </summary>
public class ViewedStateTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 标记已看过可以读回，取消后行被删除，重复取消幂等；非 diff 文件与外部成员分别返回 404。
    /// </summary>
    [Fact]
    public async Task Toggle_viewed_and_read_back()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var headSha = await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("changed a\n"),
                ["b.txt"] = Text("new b\n")
            });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        Assert.Equal(headSha, review.HeadSha);

        var initial = await ReadFilesAsync(client, review.Id);
        Assert.Equal(2, initial.Total);
        Assert.Equal(2, initial.FileCount);
        Assert.Equal(0, initial.ViewedCount);
        Assert.All(initial.Items, m => Assert.False(m.Viewed));

        var marked = await SetViewedAsync(client, review.Id, "a.txt", true);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var markedBody = await marked.Content.ReadFromJsonAsync<ViewedStateBody>();
        Assert.NotNull(markedBody);
        Assert.Equal("a.txt", markedBody.Path);
        Assert.True(markedBody.Viewed);

        var expectedBlob = await RevParseBlobAsync(source, headSha, "a.txt");
        var stored = await ReadViewRowAsync(factory, review.Id, context.Submitter.Id, "a.txt");
        Assert.NotNull(stored);
        Assert.Equal(expectedBlob, stored.BlobSha);

        var afterMark = await ReadFilesAsync(client, review.Id);
        Assert.True(Assert.Single(afterMark.Items, m => m.Path == "a.txt").Viewed);
        Assert.False(Assert.Single(afterMark.Items, m => m.Path == "b.txt").Viewed);
        Assert.Equal(1, afterMark.ViewedCount);
        Assert.Equal(2, afterMark.FileCount);

        var unmarked = await SetViewedAsync(client, review.Id, "a.txt", false);
        Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);
        var unmarkedBody = await unmarked.Content.ReadFromJsonAsync<ViewedStateBody>();
        Assert.NotNull(unmarkedBody);
        Assert.False(unmarkedBody.Viewed);
        Assert.Null(await ReadViewRowAsync(factory, review.Id, context.Submitter.Id, "a.txt"));
        Assert.Equal(0, (await ReadFilesAsync(client, review.Id)).ViewedCount);

        // 取消两次仍然成功：删除是幂等的
        var repeated = await SetViewedAsync(client, review.Id, "a.txt", false);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.False((await repeated.Content.ReadFromJsonAsync<ViewedStateBody>())!.Viewed);

        var missing = await SetViewedAsync(client, review.Id, "nope.txt", true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("FILE_NOT_IN_DIFF", (await missing.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var denied = await SetViewedAsync(outsiderClient, review.Id, "a.txt", true);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("NOT_FOUND", (await denied.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 已看过状态按用户隔离：一个人标记不影响其他人的列表与计数。
    /// </summary>
    [Fact]
    public async Task Viewed_is_per_user()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("changed a\n") });
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);

        var marked = await SetViewedAsync(submitter, review.Id, "a.txt", true);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var submitterFiles = await ReadFilesAsync(submitter, review.Id);
        Assert.True(Assert.Single(submitterFiles.Items, m => m.Path == "a.txt").Viewed);
        Assert.Equal(1, submitterFiles.ViewedCount);

        var reviewerFiles = await ReadFilesAsync(reviewer, review.Id);
        Assert.False(Assert.Single(reviewerFiles.Items, m => m.Path == "a.txt").Viewed);
        Assert.Equal(0, reviewerFiles.ViewedCount);

        // 审核人各自标记后互不覆盖
        var reviewerMarked = await SetViewedAsync(reviewer, review.Id, "a.txt", true);
        Assert.Equal(HttpStatusCode.OK, reviewerMarked.StatusCode);
        Assert.Equal(2, await CountViewRowsAsync(factory, review.Id));
        Assert.Equal(1, (await ReadFilesAsync(submitter, review.Id)).ViewedCount);
    }

    /// <summary>
    /// 新版本中内容未变的文件保持已看过：blob 相同则状态延续。
    /// </summary>
    [Fact]
    public async Task Unchanged_blob_survives_new_revision()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("changed a\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetViewedAsync(client, review.Id, "a.txt", true)).StatusCode);

        var newHead = await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]> { ["c.txt"] = Text("added c\n") });
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var files = await ReadFilesAsync(client, review.Id);
        Assert.Equal(2, files.Total);
        Assert.Equal(2, files.FileCount);
        Assert.True(Assert.Single(files.Items, m => m.Path == "a.txt").Viewed);
        Assert.False(Assert.Single(files.Items, m => m.Path == "c.txt").Viewed);
        Assert.Equal(1, files.ViewedCount);
    }

    /// <summary>
    /// 新版本中内容变化的文件自动回到未看过，旧行保留但不删除。
    /// </summary>
    [Fact]
    public async Task Changed_blob_resets_viewed()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var firstHead = await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("changed once\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetViewedAsync(client, review.Id, "a.txt", true)).StatusCode);

        var newHead = await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("changed twice\n") });
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var files = await ReadFilesAsync(client, review.Id);
        Assert.False(Assert.Single(files.Items, m => m.Path == "a.txt").Viewed);
        Assert.Equal(0, files.ViewedCount);
        Assert.Equal(1, files.FileCount);

        // 旧行按约定保留，blob 仍是第一次标记时的值
        var stored = await ReadViewRowAsync(factory, review.Id, context.Submitter.Id, "a.txt");
        Assert.NotNull(stored);
        Assert.Equal(await RevParseBlobAsync(source, firstHead, "a.txt"), stored.BlobSha);
    }

    /// <summary>
    /// 进度只统计当前版本仍存在的文件：新版本中消失的文件不进入 fileCount 与 viewedCount。
    /// </summary>
    [Fact]
    public async Task Progress_counts_only_current_revision_files()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("changed a\n"),
                ["b.txt"] = Text("added b\n")
            });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetViewedAsync(client, review.Id, "a.txt", true)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetViewedAsync(client, review.Id, "b.txt", true)).StatusCode);
        var before = await ReadFilesAsync(client, review.Id);
        Assert.Equal(2, before.Total);
        Assert.Equal(2, before.ViewedCount);

        // 新版本删掉 b.txt，a.txt 内容未变：b.txt 相对于基线已完全不存在
        var newHead = await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]>(),
            deletes: ["b.txt"]);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var files = await ReadFilesAsync(client, review.Id);
        Assert.Equal(1, files.Total);
        Assert.Equal(1, files.FileCount);
        Assert.Equal(1, files.ViewedCount);
        Assert.True(Assert.Single(files.Items, m => m.Path == "a.txt").Viewed);
        Assert.DoesNotContain(files.Items, m => m.Path == "b.txt");

        // b.txt 的历史行仍在库中，但不再计入当前版本进度
        Assert.NotNull(await ReadViewRowAsync(factory, review.Id, context.Submitter.Id, "b.txt"));
    }

    /// <summary>
    /// 删除的文件按合并基的旧侧 blob 标记已看过，仍可读回并计入进度。
    /// </summary>
    [Fact]
    public async Task Deleted_file_can_be_marked_viewed()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await CommitFeatureAsync(
            context,
            new Dictionary<string, byte[]>(),
            deletes: ["del.txt"]);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        var files = await ReadFilesAsync(client, review.Id);
        Assert.Equal("DELETED", Assert.Single(files.Items, m => m.Path == "del.txt").Status);

        var marked = await SetViewedAsync(client, review.Id, "del.txt", true);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var mirror = factory.Services.GetRequiredService<MirrorService>();
        var mergeBase = await mirror.GetMergeBaseAsync(
            context.Repo.Id, review.BaseSha, review.HeadSha, CancellationToken.None)
            ?? review.BaseSha;
        var expectedBlob = await RevParseBlobAsync(source, mergeBase, "del.txt");
        var stored = await ReadViewRowAsync(factory, review.Id, context.Submitter.Id, "del.txt");
        Assert.NotNull(stored);
        Assert.Equal(expectedBlob, stored.BlobSha);

        var afterMark = await ReadFilesAsync(client, review.Id);
        Assert.True(Assert.Single(afterMark.Items, m => m.Path == "del.txt").Viewed);
        Assert.Equal(1, afterMark.ViewedCount);
    }

    /// <summary>
    /// 准备仓库、成员关系、基线提交与空的 feature 分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>已看过测试上下文</returns>
    private static async Task<ViewedContext> SeedContextAsync(TestAppFactory factory, TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("viewed"));
        SeedMember(factory, repo.Id, submitter.Id);
        SeedMember(factory, repo.Id, reviewer.Id);

        var baseSha = await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("base a\n"),
                ["del.txt"] = Text("remove me\n")
            },
            message: "base files");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await FetchMirrorAsync(factory, repo);
        return new ViewedContext(factory, source, repo, submitter, reviewer, baseSha);
    }

    /// <summary>
    /// 在 feature 分支提交一组变更并同步到本地镜像。
    /// </summary>
    /// <param name="context">已看过测试上下文</param>
    /// <param name="writes">写入或覆盖的文件</param>
    /// <param name="deletes">待删除的文件</param>
    /// <returns>提交后的 feature 分支 SHA</returns>
    private static async Task<string> CommitFeatureAsync(
        ViewedContext context,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyList<string>? deletes = null)
    {
        var sha = await context.Source.CommitChangesAsync(HeadBranch, writes, deletes);
        await FetchMirrorAsync(context.Factory, context.Repo);
        return sha;
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
    /// <param name="context">已看过测试上下文</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        ViewedContext context)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId = context.Repo.Id,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title = UniqueName("已看过"),
                description = (string?)null,
                reviewerIds = new[] { context.Reviewer.Id }
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 调用文件列表接口并返回强类型响应。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <returns>文件列表响应</returns>
    private static async Task<ViewedFileListBody> ReadFilesAsync(HttpClient client, Guid reviewId)
    {
        var body = await client.GetFromJsonAsync<ViewedFileListBody>(
            $"/api/reviews/{reviewId}/files");
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 调用标记已看过接口。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="viewed">目标状态</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> SetViewedAsync(
        HttpClient client,
        Guid reviewId,
        string path,
        bool viewed)
    {
        return client.PutJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/files/viewed",
            new { path, viewed });
    }

    /// <summary>
    /// 直接读取当前用户的已看过行。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="path">文件路径</param>
    /// <returns>已看过行，未命中返回 null</returns>
    private static async Task<ReviewFileView?> ReadViewRowAsync(
        TestAppFactory factory,
        Guid reviewId,
        Guid userId,
        string path)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReviewFileViews.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ReviewId == reviewId
                && m.UserId == userId
                && m.FilePath == path);
    }

    /// <summary>
    /// 统计评审单下的已看过行数量。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <returns>行数</returns>
    private static async Task<int> CountViewRowsAsync(TestAppFactory factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReviewFileViews.AsNoTracking()
            .CountAsync(m => m.ReviewId == reviewId);
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
    /// 在裸仓库中读取指定提交下文件内容的 blob id，用于独立校验服务写入的 blob_sha。
    /// </summary>
    /// <param name="source">真实 git 测试仓库</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <returns>blob id</returns>
    private static async Task<string> RevParseBlobAsync(
        TestGitRepo source,
        string commitSha,
        string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = source.BarePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--verify");
        startInfo.ArgumentList.Add($"{commitSha}:{path}");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git rev-parse {commitSha}:{path} 执行失败（{process.ExitCode}）：{stdErr}");
        }

        return stdOut.Trim();
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
    /// 已看过测试上下文：工厂、真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Factory">测试宿主工厂</param>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    /// <param name="BaseSha">基线提交</param>
    private sealed record ViewedContext(
        TestAppFactory Factory,
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer,
        string BaseSha);
}

/// <summary>
/// 文件列表响应 DTO。
/// </summary>
/// <param name="Items">文件摘要列表</param>
/// <param name="Total">真实文件总数</param>
/// <param name="ViewedCount">当前用户在当前版本已看过的文件数</param>
/// <param name="FileCount">当前版本文件总数</param>
/// <param name="Truncated">是否因超过上限被截断</param>
public sealed record ViewedFileListBody(
    IReadOnlyList<ViewedFileBody> Items,
    int Total,
    int ViewedCount,
    int FileCount,
    bool Truncated);

/// <summary>
/// 文件摘要 DTO。
/// </summary>
/// <param name="Path">当前路径</param>
/// <param name="OldPath">重命名前路径</param>
/// <param name="Status">变更状态</param>
/// <param name="Additions">新增行数</param>
/// <param name="Deletions">删除行数</param>
/// <param name="IsBinary">是否二进制</param>
/// <param name="IsTooLarge">是否超出大小限制</param>
/// <param name="Viewed">当前用户是否已看过</param>
public sealed record ViewedFileBody(
    string Path,
    string? OldPath,
    string Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool IsTooLarge,
    bool Viewed);

/// <summary>
/// 标记已看过响应 DTO。
/// </summary>
/// <param name="Path">文件路径</param>
/// <param name="Viewed">标记后的状态</param>
public sealed record ViewedStateBody(string Path, bool Viewed);
