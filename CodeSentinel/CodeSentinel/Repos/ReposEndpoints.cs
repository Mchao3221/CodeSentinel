using CodeSentinel.Audit;
using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Repos;

/// <summary>
/// 仓库登记、凭据与成员管理端点。
/// </summary>
public static class ReposEndpoints
{
    /// <summary>
    /// 创建仓库审计动作。
    /// </summary>
    public const string RepoCreatedAction = "REPO_CREATED";

    /// <summary>
    /// 更新仓库审计动作。
    /// </summary>
    public const string RepoUpdatedAction = "REPO_UPDATED";

    /// <summary>
    /// 更新仓库成员审计动作。
    /// </summary>
    public const string RepoMembersUpdatedAction = "REPO_MEMBERS_UPDATED";

    private const string RepoTargetType = "REPO";
    private const string TestingEnvironment = "Testing";
    private const int DefaultSyncIntervalSeconds = 300;
    private const int MaxNameLength = 100;
    private const int MaxBaseBranchLength = 200;

    /// <summary>
    /// 映射仓库相关端点，读取接口要求登录，写入接口要求管理员。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapReposEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repos").RequireAuthorization();

        group.MapGet(string.Empty, ListReposAsync);
        group.MapGet("/{id:guid}", GetRepoAsync);
        group.MapGet("/{id:guid}/members", ListMembersAsync);
        group.MapPost(string.Empty, CreateRepoAsync).RequireAuthorization(CurrentUser.AdminRole);
        group.MapPatch("/{id:guid}", PatchRepoAsync).RequireAuthorization(CurrentUser.AdminRole);
        group.MapPost("/{id:guid}/test-connection", TestConnectionAsync)
            .RequireAuthorization(CurrentUser.AdminRole);
        group.MapPut("/{id:guid}/members", ReplaceMembersAsync).RequireAuthorization(CurrentUser.AdminRole);
    }

    /// <summary>
    /// 分页查询仓库：管理员看到全部，成员只看到自己所属的仓库。
    /// </summary>
    /// <param name="page">页码，从 1 开始</param>
    /// <param name="pageSize">每页条数，默认 20，最大 100</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分页仓库列表</returns>
    private static async Task<IResult> ListReposAsync(
        int? page,
        int? pageSize,
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var isAdmin = CurrentUser.IsAdmin(httpContext);
        var currentPage = Pagination.NormalizePage(page);
        var currentPageSize = Pagination.NormalizePageSize(pageSize);

        var query = db.Repos.AsNoTracking();
        if (!isAdmin)
        {
            query = query.Where(m => db.RepoMembers.Any(
                member => member.RepoId == m.Id && member.UserId == userId));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(m => m.UpdatedAt)
            .ThenBy(m => m.Name)
            .Skip((currentPage - 1) * currentPageSize)
            .Take(currentPageSize)
            .Select(m => new RepoDto(
                m.Id,
                m.Name,
                m.GitUrl,
                m.DefaultBaseBranch,
                m.SyncIntervalSeconds,
                m.LastFetchedAt,
                m.LastError,
                m.IsActive,
                db.RepoMembers.Count(member => member.RepoId == m.Id),
                db.RepoMembers.Any(member => member.RepoId == m.Id
                    && member.UserId == userId
                    && member.IsMerger)))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<RepoDto>(rows, total, currentPage, currentPageSize));
    }

    /// <summary>
    /// 读取单个仓库详情，要求仓库成员或管理员身份。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>仓库详情</returns>
    private static async Task<IResult> GetRepoAsync(
        Guid id,
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var repo = await RepoAccess.EnsureAsync(db, httpContext, id, ct);
        var dto = await ToRepoDtoAsync(db, repo, CurrentUser.RequireId(httpContext), ct);
        return Results.Ok(dto);
    }

    /// <summary>
    /// 新增仓库：名称唯一、地址结构校验、保存前验证远端连通性，凭据加密后入库。
    /// </summary>
    /// <param name="request">创建请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="protector">凭据保护器</param>
    /// <param name="mirrorService">镜像服务，用于保存前连接测试</param>
    /// <param name="auditService">审计服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="environment">宿主环境</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的仓库</returns>
    private static async Task<IResult> CreateRepoAsync(
        CreateRepoRequest request,
        AppDbContext db,
        CredentialProtector protector,
        MirrorService mirrorService,
        AuditService auditService,
        HttpContext httpContext,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        var name = NormalizeName(request.Name);
        var gitUrl = RepoValidation.NormalizeUrl(request.GitUrl, IsTesting(environment));
        var baseBranch = NormalizeBaseBranch(request.DefaultBaseBranch);
        var syncInterval = NormalizeSyncInterval(request.SyncIntervalSeconds);
        var credential = PrepareCredential(protector, request.Credential, request.CredentialKind, null);
        await EnsureNameAvailableAsync(db, name, null, ct);

        // 连通性不通过时不落库
        var gitCredentials = ToGitCredentials(request.Credential, request.CredentialKind);
        await mirrorService.TestAsync(gitUrl, gitCredentials, ct);

        var repo = new Repo
        {
            Id = Guid.NewGuid(),
            Name = name,
            GitUrl = gitUrl,
            DefaultBaseBranch = baseBranch,
            CredentialCipher = credential.Cipher,
            CredentialKind = credential.Kind,
            SyncIntervalSeconds = syncInterval,
            IsActive = true
        };

        db.Repos.Add(repo);
        await SaveRepoSafeAsync(db, ct);
        await auditService.RecordAsync(
            CurrentUser.Id(httpContext),
            RepoCreatedAction,
            RepoTargetType,
            repo.Id,
            new { repo.Name, repo.GitUrl },
            ct);

        return Results.Created($"/api/repos/{repo.Id}", ToRepoDto(repo, 0, false));
    }

    /// <summary>
    /// 修改仓库信息，未提供的字段保持不变，保存前验证远端连通性。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="request">更新请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="protector">凭据保护器</param>
    /// <param name="mirrorService">镜像服务，用于保存前连接测试</param>
    /// <param name="auditService">审计服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="environment">宿主环境</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的仓库</returns>
    private static async Task<IResult> PatchRepoAsync(
        Guid id,
        PatchRepoRequest request,
        AppDbContext db,
        CredentialProtector protector,
        MirrorService mirrorService,
        AuditService auditService,
        HttpContext httpContext,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        var repo = await db.Repos.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw RepoNotFound();

        var existingCredentials = request.Credential is null
            ? RepoCredentials.Read(protector, repo)
            : null;
        var previousKind = repo.CredentialKind;
        var changes = await ApplyRepoPatchAsync(
            db, repo, request, protector, IsTesting(environment), ct);

        // 使用请求中的新凭据，未提供时沿用已存凭据；验证通过后才落库
        var gitCredentials = request.Credential is not null
            ? ToGitCredentials(request.Credential, request.CredentialKind ?? previousKind)
            : existingCredentials;
        await mirrorService.TestAsync(repo.GitUrl, gitCredentials, ct);

        await SaveRepoSafeAsync(db, ct);
        await auditService.RecordAsync(
            CurrentUser.Id(httpContext),
            RepoUpdatedAction,
            RepoTargetType,
            repo.Id,
            new { changed = changes },
            ct);

        var dto = await ToRepoDtoAsync(db, repo, CurrentUser.RequireId(httpContext), ct);
        return Results.Ok(dto);
    }

    /// <summary>
    /// 使用已保存的凭据测试仓库连通性。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="protector">凭据保护器</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>连接成功响应</returns>
    private static async Task<IResult> TestConnectionAsync(
        Guid id,
        AppDbContext db,
        CredentialProtector protector,
        MirrorService mirrorService,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw RepoNotFound();

        await mirrorService.TestAsync(repo.GitUrl, RepoCredentials.Read(protector, repo), ct);
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// 读取仓库成员与合并人标记，要求仓库成员或管理员身份；管理员可显式包含已停用成员。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="includeInactive">是否包含已停用成员，仅管理员生效</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成员列表</returns>
    private static async Task<IResult> ListMembersAsync(
        Guid id,
        bool? includeInactive,
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        await RepoAccess.EnsureAsync(db, httpContext, id, ct);
        var withInactive = includeInactive == true && CurrentUser.IsAdmin(httpContext);
        var items = await LoadMembersAsync(db, id, withInactive, ct);
        return Results.Ok(new RepoMemberListResponse(items));
    }

    /// <summary>
    /// 整体覆盖仓库成员与合并人标记，至少保留一名合并人。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="request">成员列表</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="auditService">审计服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>覆盖后的成员列表</returns>
    private static async Task<IResult> ReplaceMembersAsync(
        Guid id,
        ReplaceMembersRequest request,
        AppDbContext db,
        AuditService auditService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var repo = await db.Repos.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw RepoNotFound();

        var desired = NormalizeMemberInput(request.Members);
        if (!desired.Any(m => m.IsMerger))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "NO_MERGER", "仓库至少需要一名合并人");
        }

        await EnsureMembersExistAsync(db, desired.Select(m => m.UserId).ToList(), ct);
        var existing = await db.RepoMembers.Where(m => m.RepoId == id).ToListAsync(ct);
        var diff = ApplyMemberDiff(db, id, existing, desired);
        await db.SaveChangesAsync(ct);

        await auditService.RecordAsync(
            CurrentUser.Id(httpContext),
            RepoMembersUpdatedAction,
            RepoTargetType,
            repo.Id,
            diff,
            ct);

        var items = await LoadMembersAsync(db, id, false, ct);
        return Results.Ok(new RepoMemberListResponse(items));
    }

    /// <summary>
    /// 应用仓库局部更新，返回发生变化的字段名。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repo">待更新的仓库</param>
    /// <param name="request">更新请求</param>
    /// <param name="protector">凭据保护器</param>
    /// <param name="allowFile">是否允许 file 协议</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>发生变化的字段名列表</returns>
    private static async Task<List<string>> ApplyRepoPatchAsync(
        AppDbContext db,
        Repo repo,
        PatchRepoRequest request,
        CredentialProtector protector,
        bool allowFile,
        CancellationToken ct)
    {
        var changes = new List<string>();

        await ApplySourcePatchAsync(db, repo, request, allowFile, changes, ct);
        ApplySettingsPatch(repo, request, changes);

        if (request.Credential is not null || request.CredentialKind is not null)
        {
            ApplyCredential(repo, protector, request.Credential, request.CredentialKind);
            changes.Add("credential");
        }

        return changes;
    }

    /// <summary>
    /// 更新仓库名称、地址与默认目标分支。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repo">待更新的仓库</param>
    /// <param name="request">更新请求</param>
    /// <param name="allowFile">是否允许 file 协议</param>
    /// <param name="changes">发生变化的字段名列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private static async Task ApplySourcePatchAsync(
        AppDbContext db,
        Repo repo,
        PatchRepoRequest request,
        bool allowFile,
        List<string> changes,
        CancellationToken ct)
    {
        if (request.Name is not null)
        {
            var name = NormalizeName(request.Name);
            await EnsureNameAvailableAsync(db, name, repo.Id, ct);
            repo.Name = name;
            changes.Add("name");
        }

        if (request.GitUrl is not null)
        {
            repo.GitUrl = RepoValidation.NormalizeUrl(request.GitUrl, allowFile);
            changes.Add("gitUrl");
        }

        if (request.DefaultBaseBranch is not null)
        {
            repo.DefaultBaseBranch = NormalizeBaseBranch(request.DefaultBaseBranch);
            changes.Add("defaultBaseBranch");
        }
    }

    /// <summary>
    /// 更新仓库同步间隔与启用状态。
    /// </summary>
    /// <param name="repo">待更新的仓库</param>
    /// <param name="request">更新请求</param>
    /// <param name="changes">发生变化的字段名列表</param>
    private static void ApplySettingsPatch(
        Repo repo,
        PatchRepoRequest request,
        List<string> changes)
    {
        if (request.SyncIntervalSeconds is not null)
        {
            repo.SyncIntervalSeconds = NormalizeSyncInterval(request.SyncIntervalSeconds);
            changes.Add("syncIntervalSeconds");
        }

        if (request.IsActive is not null)
        {
            repo.IsActive = request.IsActive.Value;
            changes.Add("isActive");
        }
    }

    /// <summary>
    /// 写入或清空仓库凭据。
    /// </summary>
    /// <param name="repo">待更新的仓库</param>
    /// <param name="protector">凭据保护器</param>
    /// <param name="credential">明文凭据，空串表示清空</param>
    /// <param name="kind">凭据类型</param>
    private static void ApplyCredential(
        Repo repo,
        CredentialProtector protector,
        string? credential,
        RepoCredentialKind? kind)
    {
        // 未显式指定类型时保留仓库原有类型，避免把 TOKEN 静默改成 PASSWORD
        var prepared = PrepareCredential(protector, credential, kind, repo.CredentialKind);
        repo.CredentialCipher = prepared.Cipher;
        repo.CredentialKind = prepared.Kind;
    }

    /// <summary>
    /// 按明文与类型生成待入库的密文，不提供凭据时返回空。
    /// </summary>
    /// <param name="protector">凭据保护器</param>
    /// <param name="credential">明文凭据</param>
    /// <param name="kind">凭据类型</param>
    /// <param name="fallbackKind">未指定类型时沿用的类型</param>
    /// <returns>密文与类型</returns>
    private static (string? Cipher, RepoCredentialKind? Kind) PrepareCredential(
        CredentialProtector protector,
        string? credential,
        RepoCredentialKind? kind,
        RepoCredentialKind? fallbackKind)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            if (kind is not null)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "INVALID_CREDENTIAL",
                    "未提供凭据时不能指定凭据类型");
            }

            return (null, null);
        }

        return (protector.Protect(credential), kind ?? fallbackKind ?? RepoCredentialKind.Password);
    }

    /// <summary>
    /// 把请求中的明文凭据转换为 git 进程凭据。
    /// </summary>
    /// <param name="credential">明文凭据</param>
    /// <param name="kind">凭据类型，未提供时按密码处理</param>
    /// <returns>git 凭据或 null</returns>
    private static GitCredentials? ToGitCredentials(string? credential, RepoCredentialKind? kind)
    {
        return string.IsNullOrWhiteSpace(credential)
            ? null
            : GitCredentials.FromStored(kind ?? RepoCredentialKind.Password, credential);
    }

    /// <summary>
    /// 保存仓库，唯一索引冲突时翻译为 409，覆盖并发创建同名仓库的竞态。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>保存任务</returns>
    private static async Task SaveRepoSafeAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "REPO_NAME_EXISTS", "仓库名称已存在");
        }
    }

    /// <summary>
    /// 判断异常是否为仓库名称唯一约束冲突。
    /// </summary>
    /// <param name="exception">EF 更新异常</param>
    /// <returns>是否为唯一约束冲突</returns>
    private static bool IsUniqueNameViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException { SqliteErrorCode: 19 };
    }

    /// <summary>
    /// 计算成员增删与合并人变化，并把差异写入跟踪实体。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="existing">现有成员</param>
    /// <param name="desired">目标成员</param>
    /// <returns>成员变化明细</returns>
    private static MemberDiff ApplyMemberDiff(
        AppDbContext db,
        Guid repoId,
        List<RepoMember> existing,
        IReadOnlyList<RepoMemberInput> desired)
    {
        var existingByUser = existing.ToDictionary(m => m.UserId);
        var desiredUsers = desired.Select(m => m.UserId).ToHashSet();
        var added = new List<Guid>();
        var mergerAdded = new List<Guid>();
        var mergerRemoved = new List<Guid>();

        foreach (var item in desired)
        {
            if (!existingByUser.TryGetValue(item.UserId, out var row))
            {
                db.RepoMembers.Add(new RepoMember
                {
                    Id = Guid.NewGuid(),
                    RepoId = repoId,
                    UserId = item.UserId,
                    IsMerger = item.IsMerger
                });
                added.Add(item.UserId);
                if (item.IsMerger)
                {
                    mergerAdded.Add(item.UserId);
                }

                continue;
            }

            if (row.IsMerger != item.IsMerger)
            {
                row.IsMerger = item.IsMerger;
                (item.IsMerger ? mergerAdded : mergerRemoved).Add(item.UserId);
            }
        }

        var removed = existing.Where(m => !desiredUsers.Contains(m.UserId)).ToList();
        db.RepoMembers.RemoveRange(removed);

        return new MemberDiff(
            added,
            removed.Select(m => m.UserId).ToList(),
            mergerAdded,
            removed.Where(m => m.IsMerger).Select(m => m.UserId).ToList());
    }

    /// <summary>
    /// 校验成员 userId 不重复且非空。
    /// </summary>
    /// <param name="members">请求中的成员列表</param>
    /// <returns>归一化后的成员列表</returns>
    private static List<RepoMemberInput> NormalizeMemberInput(List<RepoMemberInput>? members)
    {
        var result = new List<RepoMemberInput>();
        var seen = new HashSet<Guid>();

        foreach (var item in members ?? [])
        {
            if (item.UserId == Guid.Empty || !seen.Add(item.UserId))
            {
                throw InvalidMember("成员列表中存在无效或重复的用户");
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// 校验成员用户全部存在且处于启用状态。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="userIds">用户主键列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private static async Task EnsureMembersExistAsync(
        AppDbContext db,
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        var states = await db.Users.AsNoTracking()
            .Where(m => userIds.Contains(m.Id))
            .Select(m => new { m.Id, m.IsActive })
            .ToDictionaryAsync(m => m.Id, m => m.IsActive, ct);

        var invalid = userIds.Any(m => !states.TryGetValue(m, out var isActive) || !isActive);
        if (invalid)
        {
            throw InvalidMember("成员必须是存在且启用中的用户");
        }
    }

    /// <summary>
    /// 读取仓库成员并附带用户展示信息，合并人排在前面。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="includeInactive">是否包含已停用成员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成员列表</returns>
    private static async Task<List<RepoMemberDto>> LoadMembersAsync(
        AppDbContext db,
        Guid repoId,
        bool includeInactive,
        CancellationToken ct)
    {
        return await (
            from member in db.RepoMembers.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.RepoId == repoId && (includeInactive || user.IsActive)
            orderby member.IsMerger descending, user.Username
            select new RepoMemberDto(
                user.Id,
                user.Username,
                user.DisplayName,
                member.IsMerger,
                user.IsActive))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 校验仓库名称唯一（大小写不敏感）。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="name">仓库名称</param>
    /// <param name="excludeId">需要排除的仓库主键，新建时为 null</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private static async Task EnsureNameAvailableAsync(
        AppDbContext db,
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        var normalized = name.ToLowerInvariant();
        var exists = await db.Repos.AnyAsync(
            m => m.Name.ToLower() == normalized && (excludeId == null || m.Id != excludeId.Value),
            ct);

        if (exists)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "REPO_NAME_EXISTS", "仓库名称已存在");
        }
    }

    /// <summary>
    /// 校验并归一化仓库名称。
    /// </summary>
    /// <param name="name">原始名称</param>
    /// <returns>去空白后的名称</returns>
    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_REPO_NAME",
                $"仓库名称不能为空且不超过 {MaxNameLength} 个字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 校验并归一化默认目标分支。
    /// </summary>
    /// <param name="branch">原始分支名</param>
    /// <returns>去空白后的分支名</returns>
    private static string NormalizeBaseBranch(string? branch)
    {
        var trimmed = branch?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxBaseBranchLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_BASE_BRANCH",
                $"默认目标分支不能为空且不超过 {MaxBaseBranchLength} 个字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 校验并归一化同步间隔。
    /// </summary>
    /// <param name="seconds">原始秒数，未提供时取默认值</param>
    /// <returns>同步间隔秒数</returns>
    private static int NormalizeSyncInterval(int? seconds)
    {
        var value = seconds ?? DefaultSyncIntervalSeconds;
        if (value <= 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_SYNC_INTERVAL",
                "同步间隔必须大于 0 秒");
        }

        return value;
    }

    /// <summary>
    /// 读取仓库并组装带成员统计的响应。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repo">仓库</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>仓库响应</returns>
    private static async Task<RepoDto> ToRepoDtoAsync(
        AppDbContext db,
        Repo repo,
        Guid userId,
        CancellationToken ct)
    {
        var memberCount = await db.RepoMembers.CountAsync(m => m.RepoId == repo.Id, ct);
        var isMerger = await db.RepoMembers.AnyAsync(
            m => m.RepoId == repo.Id && m.UserId == userId && m.IsMerger,
            ct);

        return ToRepoDto(repo, memberCount, isMerger);
    }

    /// <summary>
    /// 组装仓库响应，永不输出凭据字段。
    /// </summary>
    /// <param name="repo">仓库</param>
    /// <param name="memberCount">成员数量</param>
    /// <param name="isMerger">当前用户是否为合并人</param>
    /// <returns>仓库响应</returns>
    private static RepoDto ToRepoDto(Repo repo, int memberCount, bool isMerger)
    {
        return new RepoDto(
            repo.Id,
            repo.Name,
            repo.GitUrl,
            repo.DefaultBaseBranch,
            repo.SyncIntervalSeconds,
            repo.LastFetchedAt,
            repo.LastError,
            repo.IsActive,
            memberCount,
            isMerger);
    }

    /// <summary>
    /// 判断当前是否为测试环境。
    /// </summary>
    /// <param name="environment">宿主环境</param>
    /// <returns>是否测试环境</returns>
    private static bool IsTesting(IHostEnvironment environment)
    {
        return environment.IsEnvironment(TestingEnvironment);
    }

    /// <summary>
    /// 构造统一的“仓库不存在”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException RepoNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "仓库不存在");
    }

    /// <summary>
    /// 构造统一的“成员不合法”异常。
    /// </summary>
    /// <param name="message">错误信息</param>
    /// <returns>业务异常</returns>
    private static ApiException InvalidMember(string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, "INVALID_MEMBER", message);
    }

    /// <summary>
    /// 成员变化明细，写入审计日志。
    /// </summary>
    /// <param name="Added">新增成员</param>
    /// <param name="Removed">移除成员</param>
    /// <param name="MergerAdded">新增合并人</param>
    /// <param name="MergerRemoved">移除合并人</param>
    private sealed record MemberDiff(
        IReadOnlyList<Guid> Added,
        IReadOnlyList<Guid> Removed,
        IReadOnlyList<Guid> MergerAdded,
        IReadOnlyList<Guid> MergerRemoved);
}

/// <summary>
/// 创建仓库请求。
/// </summary>
/// <param name="Name">仓库名称</param>
/// <param name="GitUrl">仓库地址</param>
/// <param name="DefaultBaseBranch">默认目标分支</param>
/// <param name="Credential">只读凭据明文，可选</param>
/// <param name="CredentialKind">凭据类型，可选，默认 PASSWORD</param>
/// <param name="SyncIntervalSeconds">同步间隔秒数，可选，默认 300</param>
public sealed record CreateRepoRequest(
    string? Name,
    string? GitUrl,
    string? DefaultBaseBranch,
    string? Credential,
    RepoCredentialKind? CredentialKind,
    int? SyncIntervalSeconds);

/// <summary>
/// 局部更新仓库请求，未提供的字段保持不变。
/// </summary>
/// <param name="Name">仓库名称</param>
/// <param name="GitUrl">仓库地址</param>
/// <param name="DefaultBaseBranch">默认目标分支</param>
/// <param name="Credential">只读凭据明文，空串表示清空</param>
/// <param name="CredentialKind">凭据类型</param>
/// <param name="SyncIntervalSeconds">同步间隔秒数</param>
/// <param name="IsActive">是否启用</param>
public sealed record PatchRepoRequest(
    string? Name,
    string? GitUrl,
    string? DefaultBaseBranch,
    string? Credential,
    RepoCredentialKind? CredentialKind,
    int? SyncIntervalSeconds,
    bool? IsActive);

/// <summary>
/// 仓库响应，不包含凭据密文或类型。
/// </summary>
/// <param name="Id">仓库主键</param>
/// <param name="Name">仓库名称</param>
/// <param name="GitUrl">仓库地址</param>
/// <param name="DefaultBaseBranch">默认目标分支</param>
/// <param name="SyncIntervalSeconds">同步间隔秒数</param>
/// <param name="LastFetchedAt">最近一次成功同步时间</param>
/// <param name="LastError">最近一次同步错误</param>
/// <param name="IsActive">是否启用</param>
/// <param name="MemberCount">成员数量</param>
/// <param name="IsMerger">当前用户是否为合并人</param>
public sealed record RepoDto(
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
/// 仓库成员响应。
/// </summary>
/// <param name="UserId">用户主键</param>
/// <param name="Username">登录名</param>
/// <param name="DisplayName">显示名</param>
/// <param name="IsMerger">是否合并人</param>
/// <param name="IsActive">用户是否启用</param>
public sealed record RepoMemberDto(
    Guid UserId,
    string Username,
    string DisplayName,
    bool IsMerger,
    bool IsActive);

/// <summary>
/// 仓库成员列表响应。
/// </summary>
/// <param name="Items">成员列表</param>
public sealed record RepoMemberListResponse(IReadOnlyList<RepoMemberDto> Items);

/// <summary>
/// 整体覆盖成员请求。
/// </summary>
/// <param name="Members">目标成员列表</param>
public sealed record ReplaceMembersRequest(List<RepoMemberInput>? Members);

/// <summary>
/// 成员输入项。
/// </summary>
/// <param name="UserId">用户主键</param>
/// <param name="IsMerger">是否合并人</param>
public sealed record RepoMemberInput(Guid UserId, bool IsMerger);
