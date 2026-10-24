using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 仓库登记、凭据加密与成员/合并人相关测试。
/// </summary>
public class ReposTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private const string TestPassword = "Passw0rd!";
    private const string TestCredential = "Sup3rSecret-Repo-Pass!";

    /// <summary>
    /// 只允许 http/https，ftp 地址应被拒绝。
    /// </summary>
    [Fact]
    public async Task Create_repo_rejects_ftp_scheme()
    {
        var admin = await factory.LoginAsAdminAsync();

        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name = UniqueName("ftp"),
                gitUrl = "ftp://example.com/repo.git",
                defaultBaseBranch = "main"
            });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_REPO_URL", body.Code);
    }

    /// <summary>
    /// 地址中内嵌用户名密码属于结构错误，应在任何 git 调用前拒绝。
    /// </summary>
    [Fact]
    public async Task Create_repo_rejects_embedded_credentials()
    {
        var admin = await factory.LoginAsAdminAsync();

        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name = UniqueName("embedded"),
                gitUrl = "https://user:secret@example.com/repo.git",
                defaultBaseBranch = "main"
            });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_REPO_URL", body.Code);
    }

    /// <summary>
    /// 结构校验通过但无法连接的仓库应返回 400，且不落库。
    /// </summary>
    [Fact]
    public async Task Create_repo_rejects_unreachable_url_and_does_not_persist()
    {
        var admin = await factory.LoginAsAdminAsync();
        var name = UniqueName("unreachable");

        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name,
                gitUrl = "http://127.0.0.1:1/repo.git",
                defaultBaseBranch = "main"
            });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("REPO_UNREACHABLE", body.Code);

        var list = await admin.GetFromJsonAsync<RepoListBody>("/api/repos?pageSize=100");
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, m => m.Name == name);
    }

    /// <summary>
    /// 仓库名称重复（大小写不敏感）应返回 409。
    /// </summary>
    [Fact]
    public async Task Create_repo_rejects_duplicate_name()
    {
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var name = UniqueName("dup");

        var first = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new { name, gitUrl = source.Url, defaultBaseBranch = "main" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new { name = name.ToLowerInvariant(), gitUrl = source.Url, defaultBaseBranch = "main" });
        var body = await duplicate.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("REPO_NAME_EXISTS", body.Code);
    }

    /// <summary>
    /// 凭据必须加密入库：读数据库不能看到明文，且能用约定用途解密还原。
    /// </summary>
    [Fact]
    public async Task Credential_is_not_stored_in_plaintext()
    {
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();

        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name = UniqueName("credential"),
                gitUrl = source.Url,
                defaultBaseBranch = "main",
                credential = TestCredential,
                credentialKind = "PASSWORD"
            });
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // 响应里不能出现密文或明文凭据
        Assert.DoesNotContain("credentialCipher", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestCredential, rawBody, StringComparison.Ordinal);

        var created = JsonSerializer.Deserialize<RepoBody>(rawBody, JsonSerializerOptions.Web);
        Assert.NotNull(created);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Repos.AsNoTracking().SingleAsync(m => m.Id == created.Id);

        Assert.Equal(RepoCredentialKind.Password, stored.CredentialKind);
        Assert.False(string.IsNullOrWhiteSpace(stored.CredentialCipher));
        Assert.DoesNotContain(TestCredential, stored.CredentialCipher!, StringComparison.Ordinal);

        // 用约定的用途名解密，验证加密链路与读取链路一致
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var decrypted = provider.CreateProtector("repo-credentials").Unprotect(stored.CredentialCipher!);
        Assert.Equal(TestCredential, decrypted);
    }

    /// <summary>
    /// 只更新凭据时保留仓库原有凭据类型，不能把 TOKEN 静默改成 PASSWORD。
    /// </summary>
    [Fact]
    public async Task Patch_keeps_existing_credential_kind_when_only_credential_given()
    {
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(
            admin,
            UniqueName("token-kind"),
            source.Url,
            "original-token",
            "TOKEN");

        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}",
            new { credential = "rotated-token" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Repos.AsNoTracking().SingleAsync(m => m.Id == repo.Id);

        Assert.Equal(RepoCredentialKind.Token, stored.CredentialKind);
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var decrypted = provider.CreateProtector("repo-credentials").Unprotect(stored.CredentialCipher!);
        Assert.Equal("rotated-token", decrypted);
    }

    /// <summary>
    /// 保存成员时没有合并人应返回 400。
    /// </summary>
    [Fact]
    public async Task Members_put_requires_at_least_one_merger()
    {
        using var source = await TestGitRepo.CreateAsync();
        var first = factory.SeedUser(UniqueName("member-a"), TestPassword);
        var second = factory.SeedUser(UniqueName("member-b"), TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("no-merger"), source.Url);

        var response = await admin.PutJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}/members",
            new
            {
                members = new[]
                {
                    new { userId = first.Id, isMerger = false },
                    new { userId = second.Id, isMerger = false }
                }
            });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("NO_MERGER", body.Code);
    }

    /// <summary>
    /// 非仓库成员访问仓库详情应返回 404，避免暴露仓库存在性。
    /// </summary>
    [Fact]
    public async Task Non_member_gets_404_on_repo()
    {
        using var source = await TestGitRepo.CreateAsync();
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("private"), source.Url);

        var client = await factory.LoginAsync(outsider.Username, TestPassword);
        var response = await client.GetAsync($"/api/repos/{repo.Id}");
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("NOT_FOUND", body.Code);
    }

    /// <summary>
    /// 成员整体覆盖后，管理员与成员都能读到成员列表，列表标记合并人。
    /// </summary>
    [Fact]
    public async Task Members_can_be_replaced_and_listed()
    {
        using var source = await TestGitRepo.CreateAsync();
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("members"), source.Url);

        var put = await admin.PutJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}/members",
            new
            {
                members = new[]
                {
                    new { userId = merger.Id, isMerger = true },
                    new { userId = reviewer.Id, isMerger = false }
                }
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var members = await admin.GetFromJsonAsync<RepoMemberListBody>(
            $"/api/repos/{repo.Id}/members");
        Assert.NotNull(members);
        Assert.Equal(2, members.Items.Count);
        Assert.True(members.Items.Single(m => m.UserId == merger.Id).IsMerger);
        Assert.False(members.Items.Single(m => m.UserId == reviewer.Id).IsMerger);

        // 成员列表只包含自己所属仓库，按当前用户标记 isMerger
        var mergerClient = await factory.LoginAsync(merger.Username, TestPassword);
        var mergerList = await mergerClient.GetFromJsonAsync<RepoListBody>("/api/repos?pageSize=100");
        Assert.NotNull(mergerList);
        var mergerRepo = mergerList.Items.Single(m => m.Id == repo.Id);
        Assert.True(mergerRepo.IsMerger);
        Assert.Equal(2, mergerRepo.MemberCount);

        var reviewerClient = await factory.LoginAsync(reviewer.Username, TestPassword);
        var reviewerList = await reviewerClient.GetFromJsonAsync<RepoListBody>("/api/repos?pageSize=100");
        Assert.NotNull(reviewerList);
        Assert.False(reviewerList.Items.Single(m => m.Id == repo.Id).IsMerger);
    }

    /// <summary>
    /// 停用用户后，成员列表应排除该用户，避免其继续出现在审核人候选中。
    /// </summary>
    [Fact]
    public async Task Members_list_excludes_inactive_users()
    {
        using var source = await TestGitRepo.CreateAsync();
        var merger = factory.SeedUser(UniqueName("active-merger"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("inactive-reviewer"), TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("inactive-member"), source.Url);

        var put = await admin.PutJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}/members",
            new
            {
                members = new[]
                {
                    new { userId = merger.Id, isMerger = true },
                    new { userId = reviewer.Id, isMerger = false }
                }
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var deactivate = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{reviewer.Id}",
            new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var members = await admin.GetFromJsonAsync<RepoMemberListBody>(
            $"/api/repos/{repo.Id}/members");

        Assert.NotNull(members);
        Assert.Single(members.Items);
        Assert.Equal(merger.Id, members.Items[0].UserId);
        Assert.True(members.Items[0].IsActive);

        // 管理员可显式包含停用成员，便于管理页标注“保存后将移除”
        var withInactive = await admin.GetFromJsonAsync<RepoMemberListBody>(
            $"/api/repos/{repo.Id}/members?includeInactive=true");
        Assert.NotNull(withInactive);
        Assert.Equal(2, withInactive.Items.Count);
        Assert.False(withInactive.Items.Single(m => m.UserId == reviewer.Id).IsActive);
    }

    /// <summary>
    /// 修改仓库字段、停用仓库并用空串清空凭据。
    /// </summary>
    [Fact]
    public async Task Patch_repo_updates_fields_and_clears_credential()
    {
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("patch"), source.Url, TestCredential);
        var newName = UniqueName("patched");

        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}",
            new
            {
                name = newName,
                defaultBaseBranch = "develop",
                syncIntervalSeconds = 120,
                isActive = false,
                credential = "",
                credentialKind = (string?)null
            });
        var updated = await patch.Content.ReadFromJsonAsync<RepoBody>();

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(newName, updated.Name);
        Assert.Equal("develop", updated.DefaultBaseBranch);
        Assert.Equal(120, updated.SyncIntervalSeconds);
        Assert.False(updated.IsActive);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Repos.AsNoTracking().SingleAsync(m => m.Id == repo.Id);

        Assert.Null(stored.CredentialCipher);
        Assert.Null(stored.CredentialKind);
    }

    /// <summary>
    /// 连接测试端点使用已保存的凭据验证仓库，成功返回 ok。
    /// </summary>
    [Fact]
    public async Task Test_connection_endpoint_reports_ok_for_stored_repo()
    {
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var repo = await CreateRepoAsync(admin, UniqueName("connection"), source.Url);

        var response = await admin.PostJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}/test-connection",
            new { });
        var body = await response.Content.ReadFromJsonAsync<TestConnectionBody>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.True(body.Ok);
    }

    /// <summary>
    /// 生成不重复的仓库或用户名称。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 以管理员身份创建仓库，返回响应体。
    /// </summary>
    /// <param name="admin">管理员客户端</param>
    /// <param name="name">仓库名称</param>
    /// <param name="gitUrl">仓库地址</param>
    /// <param name="credential">只读凭据明文，可选</param>
    /// <param name="credentialKind">凭据类型，可选</param>
    /// <returns>仓库响应</returns>
    private static async Task<RepoBody> CreateRepoAsync(
        HttpClient admin,
        string name,
        string gitUrl,
        string? credential = null,
        string? credentialKind = "PASSWORD")
    {
        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name,
                gitUrl,
                defaultBaseBranch = "main",
                credential,
                credentialKind = credential is null ? null : credentialKind
            });
        var body = await response.Content.ReadFromJsonAsync<RepoBody>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body);
        return body;
    }
}

/// <summary>
/// 连接测试响应 DTO。
/// </summary>
/// <param name="Ok">是否连接成功</param>
public sealed record TestConnectionBody(bool Ok);

/// <summary>
/// 仓库响应的测试 DTO。
/// </summary>
/// <param name="Id">仓库主键</param>
/// <param name="Name">仓库名称</param>
/// <param name="GitUrl">仓库地址</param>
/// <param name="DefaultBaseBranch">默认目标分支</param>
/// <param name="SyncIntervalSeconds">同步间隔（秒）</param>
/// <param name="LastFetchedAt">最近同步时间</param>
/// <param name="LastError">最近同步错误</param>
/// <param name="IsActive">是否启用</param>
/// <param name="MemberCount">成员数量</param>
/// <param name="IsMerger">当前用户是否为合并人</param>
public sealed record RepoBody(
    Guid Id,
    string Name,
    string GitUrl,
    string DefaultBaseBranch,
    int SyncIntervalSeconds,
    DateTime? LastFetchedAt,
    string? LastError,
    bool IsActive,
    int MemberCount,
    bool IsMerger);

/// <summary>
/// 仓库列表响应的测试 DTO。
/// </summary>
/// <param name="Items">当前页数据</param>
/// <param name="Total">总条数</param>
/// <param name="Page">当前页码</param>
/// <param name="PageSize">每页条数</param>
public sealed record RepoListBody(IReadOnlyList<RepoBody> Items, int Total, int Page, int PageSize);

/// <summary>
/// 仓库成员响应的测试 DTO。
/// </summary>
/// <param name="UserId">用户主键</param>
/// <param name="Username">登录名</param>
/// <param name="DisplayName">显示名</param>
/// <param name="IsMerger">是否合并人</param>
public sealed record RepoMemberBody(
    Guid UserId,
    string Username,
    string DisplayName,
    bool IsMerger,
    bool IsActive);

/// <summary>
/// 仓库成员列表响应的测试 DTO。
/// </summary>
/// <param name="Items">成员列表</param>
public sealed record RepoMemberListBody(IReadOnlyList<RepoMemberBody> Items);
