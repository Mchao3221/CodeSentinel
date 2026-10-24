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
/// 评论重新定位测试：LineMapper 行号映射与 AnchorService 在真实 git、SQLite 上的行为。
/// </summary>
public class AnchorTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 新增三行后，旧文件第 10 行应通过上下文行映射到新文件第 13 行。
    /// </summary>
    [Fact]
    public void Maps_unchanged_line_through_added_lines_above()
    {
        var change = ParseSingleFile("""
            diff --git a/a.txt b/a.txt
            index 1111111..2222222 100644
            --- a/a.txt
            +++ b/a.txt
            @@ -8,3 +8,6 @@
             line8
             line9
            +alpha
            +beta
            +gamma
             line10
            """);

        Assert.Equal(13, LineMapper.Map(change, "NEW", 10));
    }

    /// <summary>
    /// 被删除的行无法映射到新文件，返回 null；其后的上下文行按行数差平移。
    /// </summary>
    [Fact]
    public void Returns_null_for_deleted_line()
    {
        var change = ParseSingleFile("""
            diff --git a/a.txt b/a.txt
            index 1111111..2222222 100644
            --- a/a.txt
            +++ b/a.txt
            @@ -9,3 +9,2 @@
             line9
            -line10
             line11
            """);

        Assert.Null(LineMapper.Map(change, "NEW", 10));
        Assert.Equal(9, LineMapper.Map(change, "NEW", 9));
        Assert.Equal(10, LineMapper.Map(change, "NEW", 11));
    }

    /// <summary>
    /// P31：基线提交固定，OLD 侧行号原样返回，不参与映射。
    /// </summary>
    [Fact]
    public void Old_side_line_is_returned_unchanged()
    {
        var change = ParseSingleFile("""
            diff --git a/a.txt b/a.txt
            index 1111111..2222222 100644
            --- a/a.txt
            +++ b/a.txt
            @@ -9,3 +9,2 @@
             line9
            -line10
             line11
            """);

        Assert.Equal(10, LineMapper.Map(change, "OLD", 10));
    }

    /// <summary>
    /// 文件内容未变化时评论保持原位置与状态，只同步 blob id。
    /// </summary>
    [Fact]
    public async Task Unchanged_file_keeps_comment_position()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 1, "首行评论");
        var comment = await ReadCommentBodyAsync(created);

        // 新版本只改动了其它文件，a.txt 完全未变
        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["b.txt"] = Text("other\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 1, Moved: 0, Outdated: 0), report);
        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal("a.txt", stored.FilePath);
        Assert.Equal(1, stored.LineNumber);
        Assert.Equal(CommentStatus.Open, stored.Status);
        Assert.Equal(fromRevisionId, stored.RevisionId);
        Assert.Equal(await RevParseBlobAsync(source, head2, "a.txt"), stored.BlobSha);
    }

    /// <summary>
    /// 文件顶部新增行后，评论行号被重新映射并保持 OPEN。
    /// </summary>
    [Fact]
    public async Task Shifted_line_is_remapped_and_kept_open()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 3, "第三行评论");
        var comment = await ReadCommentBodyAsync(created);

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("zero\none\ntwo\nthree\nfour\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 1, Moved: 0, Outdated: 0), report);
        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal(4, stored.LineNumber);
        Assert.Equal("three", stored.AnchorText);
        Assert.Equal(CommentStatus.Open, stored.Status);
    }

    /// <summary>
    /// 评论所在行被改写后无法定位，标记为 OUTDATED。
    /// </summary>
    [Fact]
    public async Task Changed_context_marks_comment_outdated()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 4, "第四行评论");
        var comment = await ReadCommentBodyAsync(created);

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nFOUR\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 0, Moved: 0, Outdated: 1), report);
        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal(CommentStatus.Outdated, stored.Status);
    }

    /// <summary>
    /// 文件重命名但内容不变时，评论保留并跟随到新路径。
    /// </summary>
    [Fact]
    public async Task Renamed_file_updates_path_and_keeps_comment()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["old.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["old.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "old.txt", "NEW", 2, "第二行评论");
        var comment = await ReadCommentBodyAsync(created);

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>(),
            renames: [("old.txt", "new.txt")]);
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 1, Moved: 0, Outdated: 0), report);
        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal("new.txt", stored.FilePath);
        Assert.Equal(2, stored.LineNumber);
        Assert.Equal(CommentStatus.Open, stored.Status);
        Assert.Equal(await RevParseBlobAsync(source, head2, "new.txt"), stored.BlobSha);
    }

    /// <summary>
    /// 文件在新版本中被删除时，评论标记为 OUTDATED。
    /// </summary>
    [Fact]
    public async Task Deleted_file_marks_comment_outdated()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 4, "第四行评论");
        var comment = await ReadCommentBodyAsync(created);

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>(),
            deletes: ["a.txt"]);
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 0, Moved: 0, Outdated: 1), report);
        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal(CommentStatus.Outdated, stored.Status);
    }

    /// <summary>
    /// Task 10 补充：NEW 侧评论传入重命名前的旧路径时返回 404 FILE_NOT_IN_DIFF 且不落库，
    /// OLD 侧评论统一落库为重命名前的旧路径。
    /// </summary>
    [Fact]
    public async Task New_side_comment_on_renamed_old_path_is_rejected()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["old.txt"] = Text("l1\nl2\nl3\nl4\nl5\nl6\n") },
            new Dictionary<string, byte[]> { ["old.txt"] = Text("l1\nl2-changed\nl3\nl4\nl5\nl6\nl7\n") },
            renames: [("old.txt", "new.txt")]);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        var rejected = await CreateCommentAsync(client, review.Id, "old.txt", "NEW", 7, "旧路径评论");
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.Equal(
            "FILE_NOT_IN_DIFF",
            (await rejected.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var accepted = await CreateCommentAsync(client, review.Id, "new.txt", "NEW", 7, "新路径评论");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var acceptedBody = await ReadCommentBodyAsync(accepted);
        Assert.Equal("new.txt", acceptedBody.Path);

        var oldSide = await CreateCommentAsync(client, review.Id, "new.txt", "OLD", 2, "OLD 侧评论");
        Assert.Equal(HttpStatusCode.Created, oldSide.StatusCode);
        var oldSideBody = await ReadCommentBodyAsync(oldSide);
        Assert.Equal("old.txt", oldSideBody.Path);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Comments.AsNoTracking()
            .SingleAsync(m => m.Id == acceptedBody.Id);
        Assert.Equal("new.txt", stored.FilePath);
        Assert.Equal("l7", stored.AnchorText);
        Assert.Equal(
            2,
            await db.Comments.AsNoTracking().CountAsync(m => m.ReviewId == review.Id));
    }

    /// <summary>
    /// 映射位置原文不一致时，在 ±5 行内找到锚点原文则移动评论并保持 OPEN。
    /// </summary>
    [Fact]
    public async Task Anchor_text_found_nearby_is_moved()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\nfive\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        // 直接写入一条锚点原文与行号错位的评论，模拟历史数据漂移
        var commentId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Comments.Add(new Comment
            {
                Id = commentId,
                ReviewId = review.Id,
                RevisionId = review.Revisions[0].Id,
                AuthorId = context.Submitter.Id,
                FilePath = "a.txt",
                Side = "NEW",
                LineNumber = 3,
                AnchorText = "five",
                BlobSha = await RevParseBlobAsync(source, context.HeadSha, "a.txt"),
                Body = "错位锚点",
                Status = CommentStatus.Open
            });
            await db.SaveChangesAsync();
        }

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("zero\none\ntwo\nthree\nfour\nfive\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var fromRevisionId = review.Revisions[0].Id;
        var toRevisionId = await InsertRevisionAsync(factory, review.Id, context.BaseSha, head2);

        var report = await ReanchorAsync(factory, review.Id, fromRevisionId, toRevisionId);

        Assert.Equal(new ReanchorReport(Kept: 0, Moved: 1, Outdated: 0), report);
        var stored = await ReadCommentAsync(factory, commentId);
        Assert.Equal(6, stored.LineNumber);
        Assert.Equal(CommentStatus.Open, stored.Status);
    }

    /// <summary>
    /// 跨两次版本迁移：评论在 rev1 创建，rev2、rev3 各在顶部插行，
    /// 最终行号累计移动且保持 OPEN，revision_id 仍为创建版本。
    /// </summary>
    [Fact]
    public async Task Comment_stays_anchored_across_multiple_revisions()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 3, "第三行评论");
        var comment = await ReadCommentBodyAsync(created);
        var originRevisionId = review.Revisions[0].Id;

        // 第二跳：顶部插入 zero，评论从第 3 行移动到第 4 行
        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("zero\none\ntwo\nthree\nfour\n") });
        await FetchMirrorAsync(factory, context.Repo);
        await AddRevisionAsync(factory, review.Id, head2);

        // 第三跳：再插入 minus，评论应累计移动到第 5 行
        var head3 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("minus\nzero\none\ntwo\nthree\nfour\n") });
        await FetchMirrorAsync(factory, context.Repo);
        await AddRevisionAsync(factory, review.Id, head3);

        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal(5, stored.LineNumber);
        Assert.Equal(CommentStatus.Open, stored.Status);
        Assert.Equal(originRevisionId, stored.RevisionId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            3,
            await db.ReviewRevisions.AsNoTracking().CountAsync(m => m.ReviewId == review.Id));
    }

    /// <summary>
    /// RevisionService 创建新版本后应自动触发重新定位，评论行号随之更新。
    /// </summary>
    [Fact]
    public async Task Revision_service_reanchors_after_new_head()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(
            factory,
            source,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\n") },
            new Dictionary<string, byte[]> { ["a.txt"] = Text("one\ntwo\nthree\nfour\n") });
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);
        var created = await CreateCommentAsync(client, review.Id, "a.txt", "NEW", 3, "第三行评论");
        var comment = await ReadCommentBodyAsync(created);

        var head2 = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("zero\none\ntwo\nthree\nfour\n") });
        await FetchMirrorAsync(factory, context.Repo);

        using (var scope = factory.Services.CreateScope())
        {
            var revisionService = scope.ServiceProvider.GetRequiredService<RevisionService>();
            Assert.True(await revisionService.AddRevisionAsync(
                review.Id, head2, CancellationToken.None));
        }

        var stored = await ReadCommentAsync(factory, comment.Id);
        Assert.Equal(4, stored.LineNumber);
        Assert.Equal(CommentStatus.Open, stored.Status);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            2,
            await db.ReviewRevisions.AsNoTracking().CountAsync(m => m.ReviewId == review.Id));
    }

    /// <summary>
    /// 解析单文件补丁，用于 LineMapper 单元测试。
    /// </summary>
    /// <param name="patch">补丁文本</param>
    /// <returns>解析出的文件 diff</returns>
    private static DiffFile ParseSingleFile(string patch)
    {
        return Assert.Single(UnifiedDiffParser.Parse(patch));
    }

    /// <summary>
    /// 准备真实 git 仓库、仓库成员、基线与提交分支内容，并拉取镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <param name="baseFiles">基线提交写入的文件</param>
    /// <param name="headFiles">提交分支写入的文件</param>
    /// <param name="renames">提交分支上的重命名操作</param>
    /// <returns>重新定位测试上下文</returns>
    private static async Task<AnchorContext> SeedContextAsync(
        TestAppFactory factory,
        TestGitRepo source,
        IReadOnlyDictionary<string, byte[]> baseFiles,
        IReadOnlyDictionary<string, byte[]> headFiles,
        IReadOnlyList<(string OldPath, string NewPath)>? renames = null)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("anchor"));
        SeedMember(factory, repo.Id, submitter.Id);
        SeedMember(factory, repo.Id, reviewer.Id);

        var baseSha = await source.CommitChangesAsync(BaseBranch, baseFiles, message: "base content");
        await source.CreateBranchFromMainAsync(HeadBranch);
        var headSha = await source.CommitChangesAsync(HeadBranch, headFiles, renames: renames);
        await FetchMirrorAsync(factory, repo);

        return new AnchorContext(source, repo, submitter, reviewer, baseSha, headSha);
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
    /// <param name="context">重新定位测试上下文</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        AnchorContext context)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId = context.Repo.Id,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title = UniqueName("重新定位测试"),
                description = (string?)null,
                reviewerIds = new[] { context.Reviewer.Id }
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
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
            new { path, side, line, body, parentId = (Guid?)null });
    }

    /// <summary>
    /// 读取创建评论响应体。
    /// </summary>
    /// <param name="response">创建评论响应</param>
    /// <returns>评论响应</returns>
    private static async Task<CommentBody> ReadCommentBodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 直接插入下一个版本，供 AnchorService 独立测试使用。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">新版本终点提交</param>
    /// <returns>新版本主键</returns>
    private static async Task<Guid> InsertRevisionAsync(
        TestAppFactory factory,
        Guid reviewId,
        string baseSha,
        string headSha)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seq = await db.ReviewRevisions.AsNoTracking()
            .Where(m => m.ReviewId == reviewId)
            .MaxAsync(m => m.Seq);
        var revision = new ReviewRevision
        {
            Id = Guid.NewGuid(),
            ReviewId = reviewId,
            Seq = seq + 1,
            BaseSha = baseSha,
            HeadSha = headSha
        };
        db.ReviewRevisions.Add(revision);
        await db.SaveChangesAsync();
        return revision.Id;
    }

    /// <summary>
    /// 直接调用 AnchorService 执行重新定位。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="fromRevisionId">旧版本主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <returns>重新定位结果</returns>
    private static async Task<ReanchorReport> ReanchorAsync(
        TestAppFactory factory,
        Guid reviewId,
        Guid fromRevisionId,
        Guid toRevisionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AnchorService>();
        return await service.ReanchorAsync(
            reviewId, fromRevisionId, toRevisionId, CancellationToken.None);
    }

    /// <summary>
    /// 直接调用 RevisionService 追加新版本，走完整的版本创建与重新定位链路。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <returns></returns>
    private static async Task AddRevisionAsync(
        TestAppFactory factory,
        Guid reviewId,
        string newHeadSha)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RevisionService>();
        Assert.True(await service.AddRevisionAsync(
            reviewId, newHeadSha, CancellationToken.None));
    }

    /// <summary>
    /// 读取评论最新状态。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="commentId">评论主键</param>
    /// <returns>只读评论</returns>
    private static async Task<Comment> ReadCommentAsync(TestAppFactory factory, Guid commentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Comments.AsNoTracking().SingleAsync(m => m.Id == commentId);
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
    /// 重新定位测试上下文：真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    /// <param name="BaseSha">基线提交</param>
    /// <param name="HeadSha">评审分支提交</param>
    private sealed record AnchorContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer,
        string BaseSha,
        string HeadSha);
}
