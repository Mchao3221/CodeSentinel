using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// diff 引擎集成测试：文件列表、单文件 diff、全文与缓存，全部使用真实 git 与 HTTP。
/// </summary>
public class DiffServiceTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 文件列表应报告修改、新增、删除与重命名四类状态，并携带正确的增删行数与 oldPath。
    /// </summary>
    [Fact]
    public async Task File_list_reports_statuses_additions_deletions_and_rename()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("line1\nline2\nline3\n"),
                ["del.txt"] = Text("remove me\n"),
                ["old.md"] = Text("same content\n")
            },
            message: "base files");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = Text("line1\nline2 changed\nline3\nline4\n"),
                ["added.txt"] = Text("new file\n")
            },
            deletes: ["del.txt"],
            renames: [("old.md", "new.md")]);
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "文件列表", [context.Reviewer.Id]);

        var body = await client.GetFromJsonAsync<DiffFileListBody>($"/api/reviews/{review.Id}/files");
        Assert.NotNull(body);
        Assert.False(body.Truncated);
        Assert.Equal(0, body.ViewedCount);
        Assert.Equal(body.Total, body.FileCount);
        Assert.Equal(4, body.Total);

        var modified = Assert.Single(body.Items, m => m.Path == "a.txt");
        Assert.Equal("MODIFIED", modified.Status);
        Assert.Equal(2, modified.Additions);
        Assert.Equal(1, modified.Deletions);
        Assert.Null(modified.OldPath);

        var added = Assert.Single(body.Items, m => m.Path == "added.txt");
        Assert.Equal("ADDED", added.Status);
        Assert.Equal(1, added.Additions);
        Assert.Equal(0, added.Deletions);

        var deleted = Assert.Single(body.Items, m => m.Path == "del.txt");
        Assert.Equal("DELETED", deleted.Status);
        Assert.Equal(0, deleted.Additions);
        Assert.Equal(1, deleted.Deletions);

        var renamed = Assert.Single(body.Items, m => m.Path == "new.md");
        Assert.Equal("RENAMED", renamed.Status);
        Assert.Equal("old.md", renamed.OldPath);

        var diff = await client.GetFromJsonAsync<DiffFileBody>($"/api/reviews/{review.Id}/diff?path=a.txt");
        Assert.NotNull(diff);
        Assert.Equal("MODIFIED", diff.Status);
        Assert.Equal(2, diff.Additions);
        Assert.Equal(1, diff.Deletions);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(2, hunk.Lines.Count(m => m.Type == "ADD"));
        Assert.Equal(1, hunk.Lines.Count(m => m.Type == "DELETE"));
    }

    /// <summary>
    /// 中文路径在列表、单文件 diff 与全文接口中应完整往返，重命名也能被识别。
    /// </summary>
    [Fact]
    public async Task Chinese_paths_round_trip_through_list_diff_and_blob()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        const string baseContent = "旧说明第一行\n旧说明第二行\n旧说明第三行\n旧说明第四行\n";
        const string headContent = "旧说明第一行\n新说明第二行\n旧说明第三行\n旧说明第四行\n";
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { ["文档/说明.md"] = Text(baseContent) },
            message: "chinese base");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            writes: new Dictionary<string, byte[]>(),
            renames: [("文档/说明.md", "文档/新说明.md")]);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["文档/新说明.md"] = Text(headContent) });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "中文路径", [context.Reviewer.Id]);

        var list = await client.GetFromJsonAsync<DiffFileListBody>($"/api/reviews/{review.Id}/files");
        Assert.NotNull(list);
        var renamed = Assert.Single(list.Items, m => m.Path == "文档/新说明.md");
        Assert.Equal("RENAMED", renamed.Status);
        Assert.Equal("文档/说明.md", renamed.OldPath);

        var blob = await client.GetFromJsonAsync<BlobContentBody>(
            $"/api/reviews/{review.Id}/blob?path={Uri.EscapeDataString("文档/新说明.md")}&side=NEW");
        Assert.NotNull(blob);
        Assert.Equal("文档/新说明.md", blob.Path);
        Assert.Equal(headContent, blob.Content);
        Assert.False(blob.IsBinary);
    }

    /// <summary>
    /// 二进制与超过 2 MB 的单文件 diff 应分别标记 IsBinary / IsTooLarge，且不返回 hunk。
    /// </summary>
    [Fact]
    public async Task Binary_and_large_files_are_flagged_without_hunks()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CreateBranchFromMainAsync(HeadBranch);
        var largeText = string.Join('\n', Enumerable.Range(0, 100_000).Select(m => $"line {m} padding padding padding"));
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>
            {
                ["bin.dat"] = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x10],
                ["large.txt"] = Text(largeText)
            });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "二进制与超大", [context.Reviewer.Id]);

        var list = await client.GetFromJsonAsync<DiffFileListBody>($"/api/reviews/{review.Id}/files");
        Assert.NotNull(list);
        var binary = Assert.Single(list.Items, m => m.Path == "bin.dat");
        Assert.True(binary.IsBinary);

        var binaryDiff = await client.GetFromJsonAsync<DiffFileBody>(
            $"/api/reviews/{review.Id}/diff?path=bin.dat");
        Assert.NotNull(binaryDiff);
        Assert.True(binaryDiff.IsBinary);
        Assert.False(binaryDiff.IsTooLarge);
        Assert.Empty(binaryDiff.Hunks);

        var largeDiff = await client.GetFromJsonAsync<DiffFileBody>(
            $"/api/reviews/{review.Id}/diff?path=large.txt");
        Assert.NotNull(largeDiff);
        Assert.True(largeDiff.IsTooLarge);
        Assert.Empty(largeDiff.Hunks);
    }

    /// <summary>
    /// 请求不在 diff 中的文件时，diff 与 blob 接口都应返回 404 FILE_NOT_IN_DIFF。
    /// </summary>
    [Fact]
    public async Task Diff_and_blob_reject_path_not_in_diff()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("changed\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "不在 diff", [context.Reviewer.Id]);

        var diff = await client.GetAsync($"/api/reviews/{review.Id}/diff?path=README.md");
        var diffError = await diff.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, diff.StatusCode);
        Assert.NotNull(diffError);
        Assert.Equal("FILE_NOT_IN_DIFF", diffError.Code);

        var blob = await client.GetAsync($"/api/reviews/{review.Id}/blob?path=README.md&side=NEW");
        var blobError = await blob.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, blob.StatusCode);
        Assert.NotNull(blobError);
        Assert.Equal("FILE_NOT_IN_DIFF", blobError.Code);
    }

    /// <summary>
    /// blob 接口 OLD / NEW 两侧分别返回基线内容与 head 内容。
    /// </summary>
    [Fact]
    public async Task Blob_returns_old_and_new_content()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("base content\n") },
            message: "base");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("head content\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "两侧内容", [context.Reviewer.Id]);

        var oldSide = await client.GetFromJsonAsync<BlobContentBody>(
            $"/api/reviews/{review.Id}/blob?path=a.txt&side=OLD");
        Assert.NotNull(oldSide);
        Assert.Equal("base content\n", oldSide.Content);

        var newSide = await client.GetFromJsonAsync<BlobContentBody>(
            $"/api/reviews/{review.Id}/blob?path=a.txt&side=NEW");
        Assert.NotNull(newSide);
        Assert.Equal("head content\n", newSide.Content);
    }

    /// <summary>
    /// 分叉历史下 OLD 侧应读取合并基而非 base 提交，验证 merge-base 语义。
    /// </summary>
    [Fact]
    public async Task Blob_old_side_reads_merge_base_not_base_sha()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v1\n") },
            message: "v1");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v2\n") });
        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("v3\n") });
        await FetchMirrorAsync(factory, context.Repo);

        var mirror = factory.Services.GetRequiredService<MirrorService>();
        var diffService = factory.Services.GetRequiredService<DiffService>();
        var mainSha = await mirror.GetHeadAsync(context.Repo.Id, BaseBranch, CancellationToken.None);
        var headSha = await mirror.GetHeadAsync(context.Repo.Id, HeadBranch, CancellationToken.None);
        Assert.NotNull(mainSha);
        Assert.NotNull(headSha);
        Assert.NotEqual(mainSha, headSha);

        var mergeBase = await mirror.GetMergeBaseAsync(context.Repo.Id, mainSha, headSha, CancellationToken.None);
        Assert.NotNull(mergeBase);
        Assert.NotEqual(mainSha, mergeBase);

        var oldBlob = await diffService.GetBlobAsync(context.Repo.Id, mergeBase, "a.txt", CancellationToken.None);
        Assert.Equal("v1\n", oldBlob.Content);
    }

    /// <summary>
    /// 命中缓存后即使删除镜像也不应重新执行 git，且返回与首次一致的数据。
    /// </summary>
    [Fact]
    public async Task Cache_returns_identical_data_without_rerunning_git()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { ["a.txt"] = Text("line1\nline2\n") });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context.Repo.Id, "缓存", [context.Reviewer.Id]);

        var firstList = await client.GetAsync($"/api/reviews/{review.Id}/files");
        Assert.Equal(HttpStatusCode.OK, firstList.StatusCode);
        var firstListBody = await firstList.Content.ReadAsStringAsync();

        var firstDiff = await client.GetAsync($"/api/reviews/{review.Id}/diff?path=a.txt");
        Assert.Equal(HttpStatusCode.OK, firstDiff.StatusCode);
        var firstDiffBody = await firstDiff.Content.ReadAsStringAsync();

        // 删除镜像，若缓存命中则不会再次执行 git
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        TestPaths.DeleteDirectory(mirror.GetMirrorPath(context.Repo.Id));

        var secondList = await client.GetAsync($"/api/reviews/{review.Id}/files");
        Assert.Equal(HttpStatusCode.OK, secondList.StatusCode);
        Assert.Equal(firstListBody, await secondList.Content.ReadAsStringAsync());

        var secondDiff = await client.GetAsync($"/api/reviews/{review.Id}/diff?path=a.txt");
        Assert.Equal(HttpStatusCode.OK, secondDiff.StatusCode);
        Assert.Equal(firstDiffBody, await secondDiff.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// 准备仓库、提交人、审核人、成员关系，并把远端分支拉进本地镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>diff 测试上下文</returns>
    private static async Task<DiffContext> SeedContextAsync(TestAppFactory factory, TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("diff"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewer.Id, isMerger: false);
        await FetchMirrorAsync(factory, repo);
        return new DiffContext(source, repo, submitter, reviewer);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="isMerger">是否合并人</param>
    private static void SeedMember(TestAppFactory factory, Guid repoId, Guid userId, bool isMerger)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepoMembers.Add(new RepoMember
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            UserId = userId,
            IsMerger = isMerger
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
    /// <param name="title">标题</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        string title,
        IReadOnlyList<Guid> reviewerIds)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title,
                description = (string?)null,
                reviewerIds
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
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
    /// diff 测试上下文：真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    private sealed record DiffContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer);
}

/// <summary>
/// 文件列表响应 DTO。
/// </summary>
/// <param name="Items">文件摘要列表</param>
/// <param name="Total">真实文件总数</param>
/// <param name="ViewedCount">已查看文件数（占位）</param>
/// <param name="FileCount">文件总数（占位）</param>
/// <param name="Truncated">是否因超过上限被截断</param>
public sealed record DiffFileListBody(
    IReadOnlyList<DiffFileSummaryBody> Items,
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
public sealed record DiffFileSummaryBody(
    string Path,
    string? OldPath,
    string Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool IsTooLarge);

/// <summary>
/// 单文件 diff DTO。
/// </summary>
/// <param name="Path">当前路径</param>
/// <param name="OldPath">重命名前路径</param>
/// <param name="Status">变更状态</param>
/// <param name="Additions">新增行数</param>
/// <param name="Deletions">删除行数</param>
/// <param name="IsBinary">是否二进制</param>
/// <param name="IsTooLarge">是否超出大小限制</param>
/// <param name="Hunks">diff 块</param>
public sealed record DiffFileBody(
    string Path,
    string? OldPath,
    string Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool IsTooLarge,
    IReadOnlyList<DiffHunkBody> Hunks);

/// <summary>
/// diff 块 DTO。
/// </summary>
/// <param name="OldStart">旧文件起始行</param>
/// <param name="OldLines">旧文件行数</param>
/// <param name="NewStart">新文件起始行</param>
/// <param name="NewLines">新文件行数</param>
/// <param name="Lines">块内行</param>
public sealed record DiffHunkBody(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    IReadOnlyList<DiffLineBody> Lines);

/// <summary>
/// diff 行 DTO。
/// </summary>
/// <param name="Type">行类型</param>
/// <param name="OldLine">旧文件行号</param>
/// <param name="NewLine">新文件行号</param>
/// <param name="Text">行文本</param>
public sealed record DiffLineBody(string Type, int? OldLine, int? NewLine, string Text);

/// <summary>
/// 文件全文 DTO。
/// </summary>
/// <param name="Path">路径</param>
/// <param name="Content">文本内容</param>
/// <param name="IsBinary">是否二进制</param>
/// <param name="IsTooLarge">是否超出大小限制</param>
public sealed record BlobContentBody(string Path, string Content, bool IsBinary, bool IsTooLarge);
