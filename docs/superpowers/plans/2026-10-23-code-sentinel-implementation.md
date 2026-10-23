# CodeSentinel 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个只读接入 git 仓库、支持分支评审、行级评论、审批闭环与合并检测的独立代码评审系统。

**Architecture:** 单进程 ASP.NET Core minimal API + EF Core(SQLite)，后台任务维护每个仓库的裸镜像、发现新提交与合并结果；Vue 3 SPA 通过同源 `/api` 访问；所有 git 操作通过调用命令行 `git` 完成，不引入任何 git 原生库。

**Tech Stack:** .NET 10、ASP.NET Core minimal API、EF Core(SQLite)、xUnit、Vue 3 + TypeScript + Vite + Element Plus + Pinia + Vue Router、Shiki、Vitest、Playwright。

**Spec:** `docs/superpowers/specs/2026-10-22-code-sentinel-design.md`

## Global Constraints

- 后端目标框架 `net10.0`；前端 Vue 3 + TypeScript；数据库只用 SQLite。
- 服务器必须安装 `git`（≥ 2.30）；生产与测试环境都是纯 HTTP。
- 仓库地址只接受 `http://` / `https://`；仅当 `ASPNETCORE_ENVIRONMENT=Testing` 时允许 `file://`（仅供集成测试）。不支持 SSH。
- 时间一律以 UTC 存储，JSON 用 ISO-8601；ID 一律 `Guid`；枚举在 JSON 中序列化为 `SNAKE_CASE_UPPER`（全局 `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)`）。
- 错误响应统一为 `{ "code": "...", "message": "...", "traceId": "..." }`；404 也必须走这个形状。
- 列表响应统一为 `{ "items": [...], "total": n, "page": 1, "pageSize": 20 }`；默认 20，最大 100；默认按 `updatedAt` 倒序。例外：`GET /api/reviews/{id}/files` 不分页，返回 `{ "items": [...], "total": n, "viewedCount": n }`。
- 无权访问某仓库时返回 **404**，不返回 403。
- 密码使用 `Microsoft.AspNetCore.Identity.PasswordHasher<User>` 哈希，最少 8 位；不使用 Identity 的 EF 存储。
- 会话使用同源 HttpOnly Cookie，`SameSite=Lax`，默认 8 小时；所有非 GET 请求必须携带 `X-CSRF-TOKEN`（来自 `GET /api/auth/csrf`），`/api/auth/login` 与 `/api/setup/admin` 除外。
- diff 上限：单次最多 300 个文件；单文件 diff 超过 2 MB 或二进制时标记 `isTooLarge` / `isBinary`，不返回 hunks。
- 同一仓库同一 `headBranch` 同时只能有一个活跃评审单（`OPEN` / `CHANGES_REQUESTED` / `APPROVED`）。
- 每个仓库至少一个合并人，任何会导致合并人为空的操作被拒绝。
- 提交人不能作为自己评审单的审核人。
- B 的 head 变化（含 amend/rebase/合并 A）一律新建版本并把所有结论重置为 `PENDING`；A 前进不重置结论。
- 逐文件“已看过”以 blob sha 比对，blob 变化即视为未看过。
- 日志、异常信息、进程参数中不得出现仓库密码或含凭据的 URL；输出前统一脱敏。
- 命名空间根为 `CodeSentinel`；后端按功能分目录，不建 Controllers。
- 前端只通过 `/api` 同源访问；开发期由 Vite 代理到 `http://localhost:5214`。

## Review Focus

以下五类输入/场景最容易在实现时被忽略，每个都在对应任务里有测试：

1. **中文或特殊字符路径、重命名文件**：git 默认会把非 ASCII 路径转义成 `"\\346\\226..."`，必须用 `-c core.quotePath=false` 和 `-z` 解析；一旦解析错，文件与评论全部对不上。（Task 4、Task 9）
2. **大文件/二进制/超限 diff**：不能让接口超时或返回坏结构，行评论要禁用而不是报错。（Task 9、Task 19）
3. **B 被 rebase/force push、分支被删除、A 前进**：不能出现“已通过但没人看过新代码”，也不能把评审单卡死。（Task 8、Task 11、Task 13）
4. **并发重复提交、并发结论**：唯一索引 + 幂等，不能产生两个活跃评审单，也不能丢结论。（Task 6、Task 7）
5. **凭据泄露**：错误信息、日志、进程参数、临时脚本都不能出现明文密码。（Task 3、Task 4、Task 15）

---

## 阶段一：后端地基（Task 1–5）

### Task 1: 项目骨架、实体、数据库与测试基座

**Files:**
- Modify: `CodeSentinel/CodeSentinel/CodeSentinel.csproj`
- Delete: `CodeSentinel/CodeSentinel/WeatherForecast.cs`、`CodeSentinel/CodeSentinel/Controllers/WeatherForecastController.cs`
- Create: `CodeSentinel/CodeSentinel/Data/AppDbContext.cs`
- Create: `CodeSentinel/CodeSentinel/Data/Entities/User.cs`、`Repo.cs`、`RepoMember.cs`、`Review.cs`、`ReviewRevision.cs`、`ReviewReviewer.cs`、`ReviewDecision.cs`、`Comment.cs`、`ReviewFileView.cs`、`Notification.cs`、`AuditLog.cs`
- Create: `CodeSentinel/CodeSentinel/Common/ApiException.cs`、`ApiExceptionMiddleware.cs`、`PagedResult.cs`
- Create: `.gitignore`（忽略 `bin/`、`obj/`、`data/`、`node_modules/`、`dist/`、`.vs/`、`*.user`）
- Modify: `CodeSentinel/CodeSentinel/Program.cs`、`appsettings.json`
- Create: `CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj`、`TestAppFactory.cs`、`FoundationTests.cs`
- Modify: `CodeSentinel/CodeSentinel.slnx`

**Interfaces:**
- Consumes: 无。
- Produces: 全部实体类；`AppDbContext`；`ApiException(int status, string code, string message)`；`PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)`；`public partial class Program`。

- [ ] **Step 1: 写失败测试**

```csharp
public class FoundationTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        var res = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_returns_error_shape()
    {
        var res = await factory.CreateClient().GetAsync("/api/nope");
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("NOT_FOUND", body!.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj`
Expected: FAIL，`/api/health` 返回 404 或测试项目不存在。

- [ ] **Step 3: 建项目与实体**

```bash
dotnet new xunit -o CodeSentinel/CodeSentinel.Tests
dotnet sln CodeSentinel/CodeSentinel.slnx add CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj
dotnet add CodeSentinel/CodeSentinel package Microsoft.EntityFrameworkCore.Sqlite
dotnet add CodeSentinel/CodeSentinel package Microsoft.EntityFrameworkCore.Design
dotnet add CodeSentinel/CodeSentinel.Tests package Microsoft.AspNetCore.Mvc.Testing
```

实体字段按 spec 第 10 节落地，枚举定义固定为：

```csharp
public enum ReviewStatus { Open, ChangesRequested, Approved, Merged, Closed }
public enum ReviewerState { Pending, Approved, ChangesRequested, Removed }
public enum CommentStatus { Open, Resolved, Outdated, Deleted }
public enum FileChangeStatus { Added, Modified, Deleted, Renamed }
public enum NotificationType { ReviewSubmitted, ChangesRequested, NewCommits, Approved, Merged, Closed, CommentReply }
public enum RepoCredentialKind { Password, Token }
```

`AppDbContext` 配置：枚举 `.HasConversion<string>()`；唯一索引 `users.username`、`repos.name`、`repo_members(repo_id,user_id)`、`review_reviewers(review_id,user_id)`、`review_file_views(review_id,user_id,file_path)`、`review_revisions(review_id,seq)`；`reviews` 上建带过滤的唯一索引 `(repo_id, head_branch)`，过滤条件 `status IN ('Open','ChangesRequested','Approved')`（枚举以字符串存储，值即枚举名）。

- [ ] **Step 4: 装配 API 与错误中间件**

`Program.cs`：注册 `AddDbContext`（连接串 `ConnectionStrings:Default`，默认 `Data Source=data/codesentinel.db`）、全局 JSON 枚举转换、`ApiExceptionMiddleware`、`UseStatusCodePages` 输出 404 错误形状、`MapGet("/api/health", ...)`。删除 `UseHttpsRedirection()`。末尾 `public partial class Program;`。

- [ ] **Step 5: 初始迁移并验证**

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate -p CodeSentinel/CodeSentinel -s CodeSentinel/CodeSentinel
dotnet ef database update -p CodeSentinel/CodeSentinel -s CodeSentinel/CodeSentinel
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj
```

Expected: 测试通过；`data/codesentinel.db` 生成；`TestAppFactory` 用临时 SQLite 文件 + `EnsureCreated()` 建库。

- [ ] **Step 6: 提交**

```bash
git add .
git commit -m "feat: scaffold api, entities, sqlite and test harness"
```

---

### Task 2: 账号、登录会话、CSRF、管理员初始化、审计

**Files:**
- Create: `CodeSentinel/CodeSentinel/Auth/AuthEndpoints.cs`、`PasswordAuthService.cs`、`CsrfEndpointFilter.cs`、`AdminBootstrap.cs`、`CurrentUser.cs`
- Create: `CodeSentinel/CodeSentinel/Users/UsersEndpoints.cs`
- Create: `CodeSentinel/CodeSentinel/Audit/AuditService.cs`
- Modify: `CodeSentinel/CodeSentinel/Program.cs`、`appsettings.json`、`Data/Entities/User.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/AuthTests.cs`、`UsersTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`、`User`、`AuditLog`。
- Produces: `PasswordAuthService.VerifyAsync(string username, string password, CancellationToken ct) -> Task<User?>`；`AuditService.RecordAsync(Guid? actorId, string action, string targetType, Guid? targetId, object? detail, CancellationToken ct) -> Task`；`CurrentUser.Id(HttpContext)` / `IsAdmin(HttpContext)`；`CsrfEndpointFilter`；策略 `Admin`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Login_with_seeded_admin_returns_me() { /* 登录后 GET /api/auth/me 返回 username 与 isAdmin=true */ }
[Fact] public async Task Login_with_wrong_password_returns_401_ERROR_SHAPE() { /* code == "INVALID_CREDENTIALS" */ }
[Fact] public async Task Mutating_request_without_csrf_returns_400() { /* POST /api/users 无 X-CSRF-TOKEN → 400 */ }
[Fact] public async Task Deactivated_user_cannot_login() { /* 401 */ }
[Fact] public async Task New_user_must_change_password_before_other_calls() { /* 首次登录后除 change-password 外均 403 PASSWORD_CHANGE_REQUIRED */ }
[Fact] public async Task Non_admin_cannot_create_user() { /* 403 */ }
[Fact] public async Task Login_writes_audit_row() { /* audit_log 有 LOGIN 记录 */ }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~UsersTests"`
Expected: FAIL（路由不存在）。

- [ ] **Step 3: 实现认证与管理端点**

- `AdminBootstrap`：启动时若无管理员，生成 32 字节随机口令，写控制台并写 `data/init-admin-token.txt`（文件权限尽量收紧），`POST /api/setup/admin`（匿名）用该口令创建第一个管理员，成功后删除文件。
- `POST /api/auth/login`（匿名，无 CSRF）：校验密码 → `SignInAsync`（Cookie）→ 写审计；响应 `{ id, username, displayName, isAdmin, mustChangePassword }`。
- Cookie 事件必须把未登录重定向改成状态码：`OnRedirectToLogin` → 401，`OnRedirectToAccessDenied` → 403（响应体仍走错误形状）。
- `CsrfEndpointFilter` 先判断是否已认证：未认证直接 401 `UNAUTHENTICATED`，再校验 token，避免匿名请求得到 400。
- `GET /api/auth/csrf`：返回 `{ token }` 并下发 antiforgery Cookie。
- `POST /api/auth/change-password`、`POST /api/auth/logout`、`GET /api/auth/me`。
- `CsrfEndpointFilter`：读 `X-CSRF-TOKEN`，调用 `IAntiforgery.ValidateRequestAsync`，失败抛 `ApiException(400, "CSRF_INVALID", ...)`。
- 用户管理（`Admin` 策略）：`GET /users`、`POST /users`、`PATCH /users/{id}`（停用/改名/改管理员）、`POST /users/{id}/reset-password`。
- 停用用户时：把其所有未结束评审中的 `review_reviewers.state` 置为 `Removed`，并通知对应提交人补人；历史评论与结论保留。
- `must_change_password=true` 的用户，除 `change-password` / `logout` / `me` / `csrf` 外，一律 403 `PASSWORD_CHANGE_REQUIRED`（用中间件或端点过滤器实现）。

- [ ] **Step 4: 运行测试至通过**

Run: `dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~UsersTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add CodeSentinel
git commit -m "feat: add cookie auth, csrf, admin bootstrap and user management"
```

---

### Task 3: 仓库登记、凭据加密与成员/合并人

**Files:**
- Create: `CodeSentinel/CodeSentinel/Repos/ReposEndpoints.cs`、`CredentialProtector.cs`、`RepoValidation.cs`
- Create: `CodeSentinel/CodeSentinel/Data/Entities/Repo.cs`（补齐导航）、`RepoMember.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/ReposTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`、`AuditService`、`CurrentUser`。
- Produces: `CredentialProtector.Protect(string plain) -> string`、`Unprotect(string cipher) -> string`；`RepoValidation.NormalizeUrl(string url, bool allowFile) -> string`；DTO `RepoDto`、`RepoMemberDto`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Create_repo_rejects_ftp_scheme() { /* 400 INVALID_REPO_URL */ }
[Fact] public async Task Create_repo_rejects_duplicate_name() { /* 409 */ }
[Fact] public async Task Credential_is_not_stored_in_plaintext() { /* 直接读库，断言不含密码明文 */ }
[Fact] public async Task Members_put_requires_at_least_one_merger() { /* 400 NO_MERGER */ }
[Fact] public async Task Non_member_gets_404_on_repo() { /* GET /api/repos/{id} → 404 */ }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~ReposTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `POST /api/repos`：`name` 唯一；`gitUrl` 只允许 http/https（Testing 下允许 file）；`defaultBaseBranch` 必填；凭据用 `IDataProtector`（purpose `"repo-credentials"`）加密，存 `credential_cipher`，并记录 `credential_kind`。
- `PATCH /api/repos/{id}`、`GET /api/repos`（成员可见；管理员可见全部）、`GET /api/repos/{id}`。连接测试与“保存前验证”在 Task 4 补上（本任务先按结构校验通过即保存）。
- `PUT /api/repos/{id}/members`：整体覆盖成员与合并人标记；校验每个 userId 存在且启用；**至少一个 `is_merger=true`**；写审计。`GET /api/repos/{id}/members` 返回成员与合并人标记（供新建评审选审核人）。
- 所有仓库级读取先过 `EnsureRepoAccess`（成员或管理员，否则 404）。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~ReposTests"
git add CodeSentinel && git commit -m "feat: add repo registration, encrypted credentials and members"
```

---

### Task 4: git 命令行封装、凭据注入、镜像与连接测试

**Files:**
- Create: `CodeSentinel/CodeSentinel/Git/GitCli.cs`、`GitCredentials.cs`、`AskPassScript.cs`、`MirrorService.cs`、`GitUrlRedactor.cs`
- Create: `CodeSentinel/CodeSentinel/Git/GitModels.cs`（`GitResult`、`BranchInfo`）
- Modify: `CodeSentinel/CodeSentinel/Repos/ReposEndpoints.cs`（test-connection）
- Test: `CodeSentinel/CodeSentinel.Tests/GitCliTests.cs`、`MirrorTests.cs`、`TestGitRepo.cs`

**Interfaces:**
- Consumes: `Repo`、`CredentialProtector`、`Storage:Root`。
- Produces: `GitCli.RunAsync(string workDir, IReadOnlyList<string> args, GitCredentials? creds, CancellationToken ct) -> Task<GitResult>`；`GitResult(int ExitCode, string StdOut, string StdErr)`；`MirrorService.TestAsync(string url, GitCredentials? creds, CancellationToken ct) -> Task`（失败抛 `ApiException(400,"REPO_UNREACHABLE", ...)`）；`EnsureAsync(Guid repoId, string url, GitCredentials? creds, ct)`；`FetchAsync(Guid repoId, string url, GitCredentials? creds, ct)`；`ListBranchesAsync(Guid repoId, ct) -> Task<IReadOnlyList<BranchInfo>>`；`GetHeadAsync(Guid repoId, string branch, ct) -> Task<string?>`；`BranchInfo(string Name, string HeadSha)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Redacts_credentials_in_urls() { /* https://u:p@h/x → https://***@h/x */ }
[Fact] public async Task Askpass_script_is_deleted_after_run() { /* 运行后临时目录为空 */ }
[Fact] public async Task Ensure_and_fetch_create_remote_branch_refs() { /* 用 TestGitRepo 建的 file:// 裸库，断言 refs/remotes/origin/main 存在 */ }
[Fact] public async Task Get_head_returns_null_for_missing_branch() { }
[Fact] public async Task Test_connection_throws_repo_unreachable_on_bad_url() { /* ApiException.Code == "REPO_UNREACHABLE" */ }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~GitCliTests|FullyQualifiedName~MirrorTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `GitCli`：`ProcessStartInfo` 逐参数传（`ArgumentList`），工作目录为镜像目录，设置 `GIT_TERMINAL_PROMPT=0`、`GIT_CONFIG_NOSYSTEM=1`；始终附加 `-c core.quotePath=false`。
- 凭据：`AskPassScript` 在临时目录写一个 `#!/bin/sh` 脚本，内容 `printf '%s\n' "$GIT_PASSWORD"`，`chmod 700`（Windows 下用 `File.SetUnixFileMode` 可能不生效，忽略异常）；环境变量只注入子进程 `GIT_ASKPASS`、`GIT_USERNAME`、`GIT_PASSWORD`；`finally` 删除脚本。
- 镜像路径：`{Storage:Root}/mirrors/{repoId}.git`；`EnsureAsync` 幂等（存在则检查 remote 并更新 URL）；`FetchAsync` 使用 `fetch --prune origin +refs/heads/*:refs/remotes/origin/*`。
- `ListBranchesAsync`：`for-each-ref --format=%(refname:short) %(objectname) refs/remotes/origin`，去掉 `origin/` 与 `HEAD`。
- `TestAsync`：`git ls-remote --heads <url>`，失败抛 `ApiException(400,"REPO_UNREACHABLE", 脱敏后的 stderr)`。
- `ReposEndpoints`：新增 `POST /api/repos/{id}/test-connection`；`POST /api/repos` 与 `PATCH /api/repos/{id}` 在保存前调用 `TestAsync`，失败返回 `400 REPO_UNREACHABLE` 且不落库；测试里用 `file://` 仓库或故意错误地址覆盖。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~GitCliTests|FullyQualifiedName~MirrorTests"
git add CodeSentinel && git commit -m "feat: add git cli wrapper, credential injection and mirror service"
```

---

### Task 5: 镜像同步后台任务、分支查询与“未开单分支”

**Files:**
- Create: `CodeSentinel/CodeSentinel/Sync/MirrorSyncWorker.cs`、`Sync/SyncOptions.cs`
- Create: `CodeSentinel/CodeSentinel/Repos/BranchEndpoints.cs`
- Modify: `CodeSentinel/CodeSentinel/Data/Entities/Repo.cs`（新增 `next_attempt_at`、`failure_count`）
- Modify: `CodeSentinel/CodeSentinel/Program.cs`、`appsettings.json`
- Test: `CodeSentinel/CodeSentinel.Tests/SyncTests.cs`、`BranchQueryTests.cs`

**Interfaces:**
- Consumes: `MirrorService`、`AppDbContext`。
- Produces: `MirrorSyncWorker.RunOnceAsync(CancellationToken) -> Task`（`internal`，测试可见）；`GET /api/repos/{id}/branches?q=`；`GET /api/repos/{id}/branches/unreviewed`；`POST /api/repos/{id}/sync`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Worker_fetches_due_repo_and_updates_last_fetched_at() { }
[Fact] public async Task Worker_records_last_error_and_backs_off() { /* 连续失败后 nextAttempt 递增，间隔 1/2/4/8/15 分钟 */ }
[Fact] public async Task Unreviewed_branches_exclude_default_branch_and_active_reviews() { }
[Fact] public async Task Branches_support_prefix_filter() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~SyncTests|FullyQualifiedName~BranchQueryTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `MirrorSyncWorker : BackgroundService`：每 `Sync:PollSeconds`（默认 60）扫描 `last_fetched_at + sync_interval < now` 且到期的仓库，串行 fetch；失败写 `last_error` 与 `next_attempt_at`（1/2/4/8/15 分钟退避）。
- `POST /api/repos/{id}/sync`：管理员或仓库成员可触发，但同一仓库 10 秒内只执行一次（进程内 `SemaphoreSlim` + 时间戳）。
- `unreviewed`：对 `default_base_branch` 做 `merge-base --is-ancestor`，取领先且 `commits ahead > 0` 的分支，排除默认分支本身与已有活跃评审单的分支；返回 `{ name, headSha, aheadBy }`。
- 分支列表来自镜像，不额外访问远端。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~SyncTests|FullyQualifiedName~BranchQueryTests"
git add CodeSentinel && git commit -m "feat: add mirror sync worker and branch queries"
```

---

## 阶段二：评审核心（Task 6–15）

### Task 6: 创建评审单、唯一性与通知落库

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/ReviewsEndpoints.cs`、`ReviewService.cs`、`ReviewDtos.cs`
- Create: `CodeSentinel/CodeSentinel/Notifications/NotificationService.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/ReviewCreationTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`、`MirrorService`、`AuditService`、`NotificationService`。
- Produces: `ReviewService.CreateAsync(Guid submitterId, CreateReviewRequest req, CancellationToken ct) -> Task<Review>`；`CreateReviewRequest(Guid RepoId, string HeadBranch, string BaseBranch, string Title, string? Description, IReadOnlyList<Guid> ReviewerIds)`；`ReviewDetailDto`（字段：`id, repo{id,name}, baseBranch, headBranch, submitter{id,displayName}, title, description, status, baseSha, headSha, currentRevision, revisions[{id,seq,headSha,createdAt}], reviewers[{userId,displayName,state,note}], permissions{canDecide,canManageReviewers,canClose,canMarkMerged}, mergeHint, mergedAt, mergeCommitSha, closeReason, createdAt, updatedAt`）；`NotificationService.CreateAsync(Guid userId, NotificationType type, Guid reviewId, object payload, CancellationToken ct) -> Task`；`NotificationService.NotifyReviewersAsync(Guid reviewId, NotificationType type, CancellationToken ct) -> Task`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Creates_review_with_revision_one_and_open_status() { }
[Fact] public async Task Rejects_reviewer_who_is_not_repo_member() { /* 400 INVALID_REVIEWER */ }
[Fact] public async Task Rejects_submitter_as_reviewer() { }
[Fact] public async Task Rejects_empty_reviewer_list() { }
[Fact] public async Task Rejects_head_not_ahead_of_base() { /* 400 NO_CHANGES */ }
[Fact] public async Task Duplicate_active_review_returns_409_with_existing_id() { }
[Fact] public async Task Notifies_each_reviewer_once() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~ReviewCreationTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `POST /api/reviews`：校验仓库成员身份（提交人须是成员）、分支存在、`head` 领先 `base`（`merge-base --is-ancestor`）、审核人 ⊆ 成员且不含提交人且非空、`title` 1–200 字。
- 事务内创建 `reviews`、`review_revisions(seq=1)`、`review_reviewers(PENDING)`；唯一索引冲突翻译成 `409 ACTIVE_REVIEW_EXISTS`，响应带已有评审单 `id`。
- 通知：每个审核人一条 `REVIEW_SUBMITTED`；`payload_json` 至少含 `reviewId`、`title`、`repoName`、`submitterName`、`headBranch`、`baseBranch`。
- 写审计 `REVIEW_CREATED`。
- `GET /api/reviews` 先支持 `scope=mine|to_review|repo` 与 `status`，分页返回。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~ReviewCreationTests"
git add CodeSentinel && git commit -m "feat: create reviews with revisions and reviewer notifications"
```

---

### Task 7: 结论与状态机

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/ReviewStateMachine.cs`
- Modify: `CodeSentinel/CodeSentinel/Reviews/ReviewService.cs`、`ReviewsEndpoints.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/ReviewStateMachineTests.cs`

**Interfaces:**
- Consumes: `Review`、`ReviewReviewer`、`ReviewDecision`、`NotificationService`、`AuditService`。
- Produces: `ReviewService.DecideAsync(Guid reviewId, Guid userId, ReviewerState state, string? note, CancellationToken ct) -> Task`；`ReviewService.CloseAsync(Guid reviewId, Guid userId, string? reason, CancellationToken ct) -> Task`；`ReviewService.AddReviewersAsync(Guid reviewId, Guid actorId, IReadOnlyList<Guid> userIds, CancellationToken ct) -> Task`；`ReviewService.RemoveReviewerAsync(Guid reviewId, Guid actorId, Guid userId, CancellationToken ct) -> Task`；`ReviewStateMachine.Recompute(IEnumerable<ReviewerState> states) -> ReviewStatus`（忽略 `Removed`）。

- [ ] **Step 1: 写失败测试**

```csharp
public static TheoryData<ReviewerState[], ReviewStatus> Cases => new()
{
    { new[] { ReviewerState.Approved, ReviewerState.Pending }, ReviewStatus.Open },
    { new[] { ReviewerState.Approved, ReviewerState.Approved }, ReviewStatus.Approved },
    { new[] { ReviewerState.Approved, ReviewerState.ChangesRequested }, ReviewStatus.ChangesRequested },
};

[Theory, MemberData(nameof(Cases))]
public void Recompute_maps_states(ReviewerState[] states, ReviewStatus expected) { }

[Fact] public async Task Non_assigned_reviewer_cannot_decide() { /* 403 */ }
[Fact] public async Task Submitter_cannot_decide() { /* 400 */ }
[Fact] public async Task Withdraw_from_approved_returns_to_open() { /* 再次 POST PENDING */ }
[Fact] public async Task Approving_all_notifies_mergers_once() { }
[Fact] public async Task Cannot_decide_on_merged_or_closed_review() { /* 409 */ }
[Fact] public async Task Submitter_can_close_own_review_admin_can_close_any() { }
[Fact] public async Task Added_reviewer_must_approve_before_approved_status() { }
[Fact] public async Task Removing_a_pending_reviewer_does_not_auto_approve() { }
[Fact] public async Task Removing_the_last_active_reviewer_is_rejected() { /* 400 LAST_REVIEWER */ }
[Fact] public async Task Admin_can_remove_approved_reviewer_and_status_is_recomputed() { }
[Fact] public async Task Removed_reviewer_is_ignored_by_recompute() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~ReviewStateMachineTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `POST /api/reviews/{id}/decisions`：state ∈ `APPROVED` / `CHANGES_REQUESTED` / `PENDING`；仅被分配的审核人；评审须处于活跃状态；写 `review_reviewers.state` 与 `review_decisions`（追加，含 PENDING）；重算状态；状态变化时发通知（`CHANGES_REQUESTED` → 提交人；`APPROVED` → 所有合并人 + 提交人；PENDING 撤回 → 提交人）。
- `ReviewStateMachine.Recompute`：任一 `ChangesRequested` → `ChangesRequested`；全部 `Approved` → `Approved`；否则 `Open`（空集合在入口就已被拒绝）。
- `POST /api/reviews/{id}/close`：提交人、合并人或管理员；写 `close_reason`、`closed_at`；通知相关人。
- 审核人管理：`POST /api/reviews/{id}/reviewers`（提交人或管理员；被加者须为仓库成员、非提交人、非已停用；已存在的人忽略）；`DELETE /api/reviews/{id}/reviewers/{userId}`（提交人只能删 `Pending`，管理员可删任何人；删除置 `state=Removed` 而不是物理删除；若删除后没有非 `Removed` 的审核人，返回 `400 LAST_REVIEWER`）。
- `Recompute` 忽略 `Removed`；没有活跃审核人时状态保持 `Open`，详情页提示“需要补充审核人”；`APPROVED` 要求至少一名非 `Removed` 审核人且全部通过。
- 并发：更新评审状态时用 `UPDATE ... WHERE status = @expected` 的乐观检查，冲突时重算后重试一次。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~ReviewStateMachineTests"
git add CodeSentinel && git commit -m "feat: add reviewer decisions and review state machine"
```

---

### Task 8: 新提交 → 新版本 → 结论作废

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/RevisionService.cs`
- Modify: `CodeSentinel/CodeSentinel/Sync/MirrorSyncWorker.cs`、`Reviews/ReviewsEndpoints.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/RevisionTests.cs`

**Interfaces:**
- Consumes: `MirrorService`、`AppDbContext`、`NotificationService`、`AnchorService`（Task 11 前以空实现注入）。
- Produces: `RevisionService.AddRevisionAsync(Guid reviewId, string newHeadSha, CancellationToken ct) -> Task<bool>`；`RevisionService.SyncActiveReviewsAsync(Guid repoId, CancellationToken ct) -> Task`；`POST /api/reviews/{id}/refresh`；`GET /api/reviews/{id}/revisions`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task New_head_creates_revision_two_and_resets_all_verdicts() { }
[Fact] public async Task Approved_review_returns_to_open_after_new_commit() { }
[Fact] public async Task Base_sha_is_not_changed_by_new_head() { }
[Fact] public async Task Same_head_is_idempotent() { /* 不产生第二个版本，不发通知 */ }
[Fact] public async Task Notifies_reviewers_with_new_commits() { }
[Fact] public async Task Refresh_endpoint_fetches_then_updates_revision() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~RevisionTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `AddRevisionAsync`：读当前 head 与评审单，若相同返回 false；否则插入 `review_revisions(seq+1, base_sha=当前版本 base_sha, head_sha=新 head)`，更新 `reviews.current_revision_id`、`head_sha`、`status=Open`，把所有非 `Removed` 的 `review_reviewers` 置为 `PENDING`、`state_revision_id=新版本`；发 `NEW_COMMITS`；调用 `AnchorService.ReanchorAsync`（Task 11 前为空实现）。
- `SyncActiveReviewsAsync`：仓库 fetch 后，对每个活跃评审单比较镜像里的 `headBranch` head，变化就调用 `AddRevisionAsync`。
- `POST /api/reviews/{id}/refresh`：触发该仓库一次同步（10 秒节流），随后返回最新评审单与版本。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~RevisionTests"
git add CodeSentinel && git commit -m "feat: track new commits as revisions and reset verdicts"
```

---

### Task 9: diff 引擎（文件列表、单文件 diff、全文、缓存与限额）

**Files:**
- Create: `CodeSentinel/CodeSentinel/Git/DiffModels.cs`、`UnifiedDiffParser.cs`、`DiffService.cs`、`DiffCache.cs`
- Create: `CodeSentinel/CodeSentinel/Reviews/DiffEndpoints.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/DiffServiceTests.cs`、`UnifiedDiffParserTests.cs`

**Interfaces:**
- Consumes: `GitCli`、`MirrorService`。
- Produces: `DiffService.GetFilesAsync(Guid repoId, string baseSha, string headSha, CancellationToken ct) -> Task<IReadOnlyList<DiffFileSummary>>`；`GetFileDiffAsync(Guid repoId, string baseSha, string headSha, string path, CancellationToken ct) -> Task<DiffFile>`；`GetBlobAsync(Guid repoId, string commitSha, string path, CancellationToken ct) -> Task<BlobContent>`；`UnifiedDiffParser.Parse(string patch) -> IReadOnlyList<DiffFile>`。

模型固定为：

```csharp
public sealed record DiffFileSummary(string Path, string? OldPath, FileChangeStatus Status,
    int Additions, int Deletions, bool IsBinary, bool IsTooLarge);
public sealed record DiffFile(string Path, string? OldPath, FileChangeStatus Status,
    int Additions, int Deletions, bool IsBinary, bool IsTooLarge, IReadOnlyList<DiffHunk> Hunks);
public sealed record DiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<DiffLine> Lines);
public sealed record DiffLine(DiffLineType Type, int? OldLine, int? NewLine, string Text);
public enum DiffLineType { Context, Add, Delete }
public sealed record BlobContent(string Path, string Content, bool IsBinary, bool IsTooLarge);
```

- [ ] **Step 1: 写失败解析器测试**

```csharp
[Fact] public void Parses_added_modified_deleted_and_renamed_files() { }
[Fact] public void Parses_chinese_paths_without_quoting() { /* 期望 Path == "文档/说明.md" */ }
[Fact] public void Marks_binary_when_patch_says_Binary_files_differ() { }
[Fact] public void Rejects_malformed_hunk_header() { /* 抛 FormatException，由服务层转 500 带 traceId */ }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~UnifiedDiffParserTests|FullyQualifiedName~DiffServiceTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- 文件列表：`git diff --name-status -z --find-renames=50% <base>...<head>` 取状态（`M`/`A`/`D`/`R100` 形式；重命名后跟 `\0oldPath\0newPath\0`），再用 `git diff --numstat -z` 取增删行数并按路径合并；中文路径原样输出（依赖 `core.quotePath=false`）；`-`/`-` 视为二进制。
- 单文件：`git diff --no-color -U3 --find-renames=50% <base>...<head> -- <path>`；解析成 hunk/行；单文件超 2 MB 或二进制 → `IsTooLarge`/`IsBinary` 且 `Hunks=[]`。
- 全文：`git show <commit>:<path>`，超 2 MB 或含 NUL 字节 → 标记并不返回内容。
- 缓存：`{Storage:Root}/cache/{repoId}/{baseSha}..{headSha}/index.json` 与每个文件一个 JSON（文件名用路径的 SHA-256）；读缓存前校验文件存在。
- 端点：`GET /api/reviews/{id}/files`、`GET /api/reviews/{id}/diff?path=`、`GET /api/reviews/{id}/blob?path=&side=`；文件路径不在该版本 diff 中 → 404 `FILE_NOT_IN_DIFF`。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~DiffServiceTests|FullyQualifiedName~UnifiedDiffParserTests"
git add CodeSentinel && git commit -m "feat: add structured diff engine with cache and limits"
```

---

### Task 10: 行级评论（创建、回复、编辑、删除、解决）

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/CommentsEndpoints.cs`、`CommentService.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/CommentsTests.cs`

**Interfaces:**
- Consumes: `DiffService`、`AppDbContext`、`AuditService`。
- Produces: `CommentService.CreateAsync(Guid reviewId, Guid authorId, CreateCommentRequest req, CancellationToken ct) -> Task<Comment>`；`CreateCommentRequest(string Path, string Side, int Line, string Body, Guid? ParentId)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Can_comment_on_add_and_delete_lines() { }
[Fact] public async Task Rejects_line_not_present_in_diff() { /* 400 LINE_NOT_IN_DIFF */ }
[Fact] public async Task Reply_requires_same_review_and_sets_parent() { }
[Fact] public async Task Only_author_can_edit_and_delete_comment() { }
[Fact] public async Task Deleted_comment_is_soft_deleted_and_audited() { }
[Fact] public async Task Author_or_merger_can_resolve_and_unresolve() { }
[Fact] public async Task Comment_on_too_large_file_returns_400_FILE_NOT_COMMENTABLE() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~CommentsTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `POST /api/reviews/{id}/comments`：仓库成员；校验 `(path, side, line)` 存在于当前版本 diff（上下文行允许，用 NEW 侧行号）；`body` 1–5000 字；写入 `anchor_text`（该侧原文）与 `blob_sha`（`git rev-parse <head>:<path>` 或旧侧 blob）。
- `PATCH /api/comments/{id}`：仅作者，只改 body；`DELETE`：作者或管理员，软删（`status=Deleted` 新增枚举值 `Deleted`，列表过滤）。
- `POST /api/comments/{id}/resolve|unresolve`：作者或仓库合并人。
- 通知：回复时通知被回复人 `COMMENT_REPLY`；写审计。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~CommentsTests"
git add CodeSentinel && git commit -m "feat: add line comments with replies and resolution"
```

---

### Task 11: 评论重新定位

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/LineMapper.cs`、`AnchorService.cs`
- Modify: `CodeSentinel/CodeSentinel/Reviews/RevisionService.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/AnchorTests.cs`

**Interfaces:**
- Consumes: `DiffService`、`AppDbContext`。
- Produces: `LineMapper.Map(DiffFile change, string side, int oldLine) -> int?`；`AnchorService.ReanchorAsync(Guid reviewId, Guid fromRevisionId, Guid toRevisionId, CancellationToken ct) -> Task<ReanchorReport>`；`ReanchorReport(int Kept, int Moved, int Outdated)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void Maps_unchanged_line_through_added_lines_above() { /* 旧 10 → 新 13 */ }
[Fact] public void Returns_null_for_deleted_line() { }
[Fact] public async Task Unchanged_file_keeps_comment_position() { }
[Fact] public async Task Shifted_line_is_remapped_and_kept_open() { }
[Fact] public async Task Changed_context_marks_comment_outdated() { }
[Fact] public async Task Renamed_file_updates_path_and_keeps_comment() { }
[Fact] public async Task Deleted_file_marks_comment_outdated() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~AnchorTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `LineMapper`：输入 `git diff <prevHead> <newHead>` 的解析结果（同一解析器，`-U0` 即可），建立“旧行 → 新行”映射；新增/删除行分别处理；重命名用 `--find-renames`。
- `AnchorService`：对每个 `status=Open` 且属于旧版本的评论：文件未变 → 保留；能映射且 `anchor_text` 匹配 → 更新 `current_path/current_line`；不匹配 → 在 ±5 行内找相同 `anchor_text`；仍找不到 → `status=Outdated`。评论的上限扫描行数 2000，避免大文件卡住。
- `RevisionService` 创建新版本后调用，结果写日志。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~AnchorTests"
git add CodeSentinel && git commit -m "feat: re-anchor comments across revisions"
```

---

### Task 12: 逐文件“已看过”

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/ViewedEndpoints.cs`
- Modify: `CodeSentinel/CodeSentinel/Reviews/DiffEndpoints.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/ViewedStateTests.cs`

**Interfaces:**
- Consumes: `DiffService`、`AppDbContext`。
- Produces: `PUT /api/reviews/{id}/files/viewed`（body `{ path, viewed }`）；`GET /api/reviews/{id}/files` 每项增加 `viewed: bool` 与顶层 `viewedCount` / `fileCount`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Toggle_viewed_and_read_back() { }
[Fact] public async Task Viewed_is_per_user() { }
[Fact] public async Task Unchanged_blob_survives_new_revision() { }
[Fact] public async Task Changed_blob_resets_viewed() { }
[Fact] public async Task Progress_counts_only_current_revision_files() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~ViewedStateTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- 写入时取当前版本该文件新侧 blob sha 存 `review_file_views.blob_sha`；读取时 `blob_sha` 与当前版本一致才算 `viewed=true`。
- `viewed=false` 删除该行。
- 只允许仓库成员；文件必须在当前版本 diff 中，否则 404。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~ViewedStateTests"
git add CodeSentinel && git commit -m "feat: add per-file viewed state based on blob sha"
```

---

### Task 13: 合并检测与手工标记

**Files:**
- Create: `CodeSentinel/CodeSentinel/Reviews/MergeDetectionService.cs`
- Modify: `CodeSentinel/CodeSentinel/Reviews/ReviewsEndpoints.cs`、`Sync/MirrorSyncWorker.cs`
- Modify: `CodeSentinel/CodeSentinel/Data/Entities/Review.cs`（新增 `merge_hint`、`merge_hint_count`）
- Test: `CodeSentinel/CodeSentinel.Tests/MergeDetectionTests.cs`

**Interfaces:**
- Consumes: `MirrorService`、`AppDbContext`、`NotificationService`。
- Produces: `MergeDetectionService.CheckAsync(Guid reviewId, CancellationToken ct) -> Task<MergeCheckResult>`；`MergeCheckResult(bool Merged, string? MergeCommitSha, bool MergeHint)`；`POST /api/reviews/{id}/mark-merged`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Ancestor_head_marks_merged_and_notifies() { }
[Fact] public async Task Non_ancestor_keeps_approved_and_sets_hint_after_three_syncs() { }
[Fact] public async Task Manual_mark_requires_merger_or_admin() { /* 403 给普通成员 */ }
[Fact] public async Task Manual_mark_accepts_optional_sha() { }
[Fact] public async Task Cannot_mark_closed_review_as_merged() { }
[Fact] public async Task Deleted_branch_after_merge_keeps_merged_status() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~MergeDetectionTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- 每次仓库同步后，对所有 `APPROVED` 评审单：`git merge-base --is-ancestor <head_sha> <A 当前 head>` → 真则 `MERGED`、记录 `merge_commit_sha`、发 `MERGED` 通知；假则 `merge_hint_count++`，达到 3 次置 `merge_hint=true`。
- `POST /api/reviews/{id}/mark-merged`：合并人或管理员；评审须为 `APPROVED`；写 `merged_at`、可选 `merge_commit_sha`；发通知；写审计。
- B 分支不存在且评审已 `MERGED` → 保持；未合并 → `CLOSED`，原因 `BRANCH_DELETED`。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~MergeDetectionTests"
git add CodeSentinel && git commit -m "feat: detect merges and support manual merge marking"
```

---

### Task 14: 通知查询与去重

**Files:**
- Create: `CodeSentinel/CodeSentinel/Notifications/NotificationsEndpoints.cs`
- Modify: `CodeSentinel/CodeSentinel/Notifications/NotificationService.cs`
- Test: `CodeSentinel/CodeSentinel.Tests/NotificationsTests.cs`

**Interfaces:**
- Consumes: `Notification`。
- Produces: `GET /api/notifications?unreadOnly=&page=`、`POST /api/notifications/read`（body `{ ids: [] }` 或 `{ all: true }`）、`GET /api/notifications/unread-count`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Lists_only_my_notifications() { }
[Fact] public async Task Unread_count_matches_unread_rows() { }
[Fact] public async Task Mark_selected_and_all_as_read_are_idempotent() { }
[Fact] public async Task Same_event_within_five_minutes_updates_instead_of_inserting() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~NotificationsTests"`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `NotificationService.CreateAsync` 先查同 `(user_id, type, review_id)` 且 5 分钟内的未读记录：存在则更新 `payload_json` 与 `created_at`，否则插入。
- 列表分页、按 `created_at desc`；`unread-count` 返回 `{ count }`。
- 标记已读只能操作自己的记录。

- [ ] **Step 4: 运行测试至通过并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj --filter "FullyQualifiedName~NotificationsTests"
git add CodeSentinel && git commit -m "feat: add notification queries and five-minute dedupe"
```

---

### Task 15: 权限矩阵与审计补全（横切验收）

**Files:**
- Modify: 所有端点文件
- Test: `CodeSentinel/CodeSentinel.Tests/PermissionsTests.cs`、`AuditCoverageTests.cs`

**Interfaces:**
- Consumes: 前面所有端点。
- Produces: 无新公开接口；补齐越权与审计。

- [ ] **Step 1: 写失败测试（表驱动）**

```csharp
[Theory]
[InlineData("GET", "/api/repos/{repoB}", 404)]
[InlineData("GET", "/api/reviews/{reviewB}", 404)]
[InlineData("GET", "/api/reviews/{reviewB}/files", 404)]
[InlineData("POST", "/api/reviews/{reviewB}/comments", 404)]
[InlineData("POST", "/api/reviews/{reviewB}/decisions", 404)]
public async Task Repo_member_of_another_repo_gets_404(...) { }

[Fact] public async Task Anonymous_gets_401_on_every_route_except_health_login_csrf_setup() { }
[Fact] public async Task Audit_covers_login_repo_member_review_decision_comment_delete_mark_merged() { }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~PermissionsTests|FullyQualifiedName~AuditCoverageTests"`
Expected: 至少一条 FAIL。

- [ ] **Step 3: 修复**

补齐 `EnsureRepoAccess` 的调用，确保返回 404；给缺失的动作加审计；确认 `must_change_password` 中间件不会阻断 `GET /api/auth/csrf`。

- [ ] **Step 4: 全量后端测试并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj
git add CodeSentinel && git commit -m "test: cover permission matrix and audit coverage"
```

---

## 阶段三：前端（Task 16–21）

### Task 16: 前端脚手架、登录、布局与通知角标

**Files:**
- Create: `code-sentinel-web/`（Vite 模板）
- Create: `code-sentinel-web/src/api/client.ts`、`src/api/types.ts`、`src/api/auth.ts`
- Create: `code-sentinel-web/src/stores/auth.ts`、`src/router.ts`、`src/layouts/AppLayout.vue`
- Create: `code-sentinel-web/src/views/LoginView.vue`
- Create: `code-sentinel-web/src/components/NotificationBell.vue`
- Test: `code-sentinel-web/src/api/client.spec.ts`、`src/router.spec.ts`

**Interfaces:**
- Consumes: 后端 `/api/auth/*`、`/api/notifications/unread-count`。
- Produces: axios 实例 `api`（自动带 Cookie、自动加 CSRF、401 跳登录）；`useAuthStore()`；路由守卫；布局组件。

- [ ] **Step 1: 初始化并写失败测试**

```bash
npm create vite@latest code-sentinel-web -- --template vue-ts
cd code-sentinel-web && npm i && npm i vue-router pinia element-plus axios marked dompurify shiki && npm i -D vitest @vue/test-utils jsdom
```

```ts
// client.spec.ts
it('adds X-CSRF-TOKEN to mutating requests', async () => { /* mock /auth/csrf, POST 后断言 header */ })
it('redirects to /login on 401', async () => { /* 断言 router.currentRoute == '/login' */ })
```

- [ ] **Step 2: 运行确认失败**

Run: `npm --prefix code-sentinel-web run test:unit -- --run`
Expected: FAIL（测试文件或实现缺失）。

- [ ] **Step 3: 实现**

- `vite.config.ts` 加 `server.proxy['/api'] = 'http://localhost:5214'`。
- `client.ts`：`withCredentials: true`；非 GET 前请求 `/api/auth/csrf` 并缓存 token（`/api/auth/login`、`/api/setup/admin` 除外）；拦截 401 清 store 并跳 `/login`。
- 登录页：用户名/密码/首次登录强制改密弹窗。
- 布局：顶部导航（评审 / 仓库 / 用户，管理员才显示后两项）+ 通知铃铛（30 秒轮询未读数）+ 当前用户菜单（退出）。
- 路由守卫：未登录访问受保护页 → `/login?redirect=`。

- [ ] **Step 4: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
npm --prefix code-sentinel-web run build
git add code-sentinel-web && git commit -m "feat(web): scaffold app with auth, layout and notification bell"
```

---

### Task 17: 评审列表与新建评审

**Files:**
- Create: `code-sentinel-web/src/views/ReviewListView.vue`、`ReviewCreateView.vue`
- Create: `code-sentinel-web/src/api/repos.ts`、`reviews.ts`
- Create: `code-sentinel-web/src/components/ReviewStatusTag.vue`
- Test: `code-sentinel-web/src/views/ReviewCreateView.spec.ts`、`ReviewListView.spec.ts`

**Interfaces:**
- Consumes: `GET /api/reviews`、`POST /api/reviews`、`GET /api/repos`、`GET /api/repos/{id}/branches`、`.../unreviewed`、`GET /api/repos/{id}/members`。
- Produces: 列表与创建表单；成功后跳转 `/reviews/{id}`。

- [ ] **Step 1: 写失败测试**

```ts
it('submits repoId, baseBranch, headBranch, reviewerIds and title', async () => { })
it('excludes the current user from reviewer options', async () => { })
it('disables submit until repo, head branch and at least one reviewer are chosen', async () => { })
it('renders three tabs and filters by status', async () => { })
```

- [ ] **Step 2: 运行确认失败并实现**

- 列表：三个标签（待我评审 / 我提交的 / 全部），仓库 + 状态筛选，分页，行点击进详情；每行显示状态标签、仓库、分支对、审核进度 `2/3`。
- 创建：选仓库 → 目标分支（默认分支预选）→ 我的分支（未开单分支置顶并标注领先提交数）→ 审核人多选（来自仓库成员，排除自己）→ 标题/说明 → 提交。后端 409 时提示“该分支已有评审单”并提供跳转。

- [ ] **Step 3: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
git add code-sentinel-web && git commit -m "feat(web): add review list and create flow"
```

---

### Task 18: 评审详情概览与操作

**Files:**
- Create: `code-sentinel-web/src/views/ReviewDetailView.vue`
- Create: `code-sentinel-web/src/components/ReviewHeader.vue`、`ReviewerList.vue`、`MergeGuide.vue`
- Create: `code-sentinel-web/src/api/comments.ts`
- Test: `code-sentinel-web/src/views/ReviewDetailView.spec.ts`

**Interfaces:**
- Consumes: `GET /api/reviews/{id}`、`/revisions`、`POST /decisions`、`/close`、`/mark-merged`、`/refresh`、`/reviewers`。
- Produces: 详情页头部与操作区。

- [ ] **Step 1: 写失败测试**

```ts
it('shows approve and request-changes only for assigned reviewers on an active review', async () => { })
it('shows withdraw button after the reviewer has decided', async () => { })
it('shows merge guide and mark-merged only when approved', async () => { })
it('shows state tag for OPEN / CHANGES_REQUESTED / APPROVED / MERGED / CLOSED', async () => { })
```

- [ ] **Step 2: 实现**

- 头部：标题、仓库、`head → base`、提交人、状态、版本切换（revision 1..n）、刷新按钮、关闭按钮（有权限时）。
- 审核人列表：每人状态（待评审/通过/要求修改/已移除），附言 tooltip；提交人可追加审核人、移除未给结论的人。
- 操作：通过 / 要求修改（附言必填） / 撤回；通过后显示合并指引（分支对、head sha、Gitblit 操作提示）和“标记为已合并”（合并人/管理员）。
- 任何操作成功后刷新详情。

- [ ] **Step 3: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
git add code-sentinel-web && git commit -m "feat(web): add review detail header and actions"
```

---

### Task 19: diff 文件列表、行渲染、已看过与过期评论

**Files:**
- Create: `code-sentinel-web/src/components/diff/DiffFile.vue`、`DiffHunk.vue`、`DiffLine.vue`、`ViewedCheckbox.vue`
- Create: `code-sentinel-web/src/composables/useHighlight.ts`
- Create: `code-sentinel-web/src/api/diff.ts`
- Test: `code-sentinel-web/src/components/diff/DiffFile.spec.ts`

**Interfaces:**
- Consumes: `GET /api/reviews/{id}/files`、`/diff?path=`、`/blob`、`PUT /files/viewed`。
- Produces: `<DiffFile :file :comments :viewed @toggle-viewed>`；行数据来自后端结构化 hunk。

- [ ] **Step 1: 写失败测试**

```ts
it('renders old and new line numbers and +/- markers', async () => { })
it('collapses a file and lazily fetches its diff on expand', async () => { })
it('shows 3/5 progress from viewed flags', async () => { })
it('renders outdated comments collapsed under an "已过期" section', async () => { })
it('falls back to plain text when highlighting fails or file is large', async () => { })
```

- [ ] **Step 2: 实现**

- 文件列表默认只渲染摘要；展开时拉 diff；单个文件可折叠。
- 行渲染：`context` 灰、`add` 绿、`del` 红；行号两列；长行横向滚动。
- 语法高亮：用 `shiki` 的 `codeToTokens` 对当前文件的高亮结果按行取用；文件 > 200 KB 或语言未知时纯文本；高亮失败静默降级。
- 已看过：勾选调用后端，进度实时更新；文件变化后后端返回 `viewed=false`。
- 过期评论：默认折叠，显示“已过期（原位置已变更）”。

- [ ] **Step 3: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
git add code-sentinel-web && git commit -m "feat(web): render diff with viewed state and stale comments"
```

---

### Task 20: 行级评论交互

**Files:**
- Create: `code-sentinel-web/src/components/diff/CommentThread.vue`、`CommentComposer.vue`
- Modify: `code-sentinel-web/src/components/diff/DiffLine.vue`
- Test: `code-sentinel-web/src/components/diff/CommentThread.spec.ts`

**Interfaces:**
- Consumes: `POST /api/reviews/{id}/comments`、`PATCH/DELETE /api/comments/{id}`、`resolve/unresolve`。
- Produces: 行内评论、回复、编辑、删除、解决。

- [ ] **Step 1: 写失败测试**

```ts
it('posts path, side and line when adding a comment on an added line', async () => { })
it('posts parentId when replying', async () => { })
it('only shows edit/delete for my own comment', async () => { })
it('toggles resolve state', async () => { })
it('renders markdown but strips script tags', async () => { })
```

- [ ] **Step 2: 实现**

- 行 hover 显示“+”按钮，点击展开输入框；提交后乐观插入并滚动到该评论。
- 评论线程：头像/姓名/时间/正文（`marked` + `dompurify`）；回复、编辑、删除（自己的）、解决/取消解决（作者或合并人）。
- 失败回滚并提示。

- [ ] **Step 3: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
git add code-sentinel-web && git commit -m "feat(web): add line comment composer and threads"
```

---

### Task 21: 仓库管理、用户管理与通知中心

**Files:**
- Create: `code-sentinel-web/src/views/ReposAdminView.vue`、`UsersAdminView.vue`、`NotificationsView.vue`
- Create: `code-sentinel-web/src/api/users.ts`、`notifications.ts`
- Test: `code-sentinel-web/src/views/ReposAdminView.spec.ts`、`UsersAdminView.spec.ts`

**Interfaces:**
- Consumes: `/api/repos*`、`/api/users*`、`/api/notifications*`。
- Produces: 三个管理页面；路由 `requiresAdmin`。

- [ ] **Step 1: 写失败测试**

```ts
it('creates a repo with name, url, base branch and credential', async () => { })
it('shows connection test result and blocks save on failure', async () => { })
it('prevents saving members without a merger', async () => { })
it('deactivates a user and hides them from reviewer options', async () => { })
it('marks all notifications read', async () => { })
```

- [ ] **Step 2: 实现**

- 仓库页：列表 + 新建/编辑抽屉 + 连接测试按钮 + 同步状态（最近同步时间、错误）+ 成员表（勾选合并人）。
- 用户页：列表、新建、停用/启用、重置密码（展示一次性新密码）。
- 通知中心：未读/全部切换、单条与全部已读、点击跳转评审。

- [ ] **Step 3: 测试通过并提交**

```bash
npm --prefix code-sentinel-web run test:unit -- --run
git add code-sentinel-web && git commit -m "feat(web): add repo, user and notification admin pages"
```

---

## 阶段四：验收与交付（Task 22）

### Task 22: Playwright 冒烟、部署文档与验收清单

**Files:**
- Create: `code-sentinel-web/playwright.config.ts`、`e2e/review-flow.spec.ts`
- Create: `README.md`、`docs/deployment.md`
- Modify: `code-sentinel-web/package.json`

**Interfaces:**
- Consumes: 全部。
- Produces: 一条 UI 冒烟测试、部署与备份说明、spec 第 24 节的验收清单。

- [ ] **Step 1: 写冒烟测试（API 用 `page.route` 打桩）**

```ts
test('submit → comment → request changes → new revision → approve → merged', async ({ page }) => { })
```

- [ ] **Step 2: 运行**

Run: `npm --prefix code-sentinel-web run test:e2e`
Expected: PASS。

- [ ] **Step 3: 写部署文档**

`docs/deployment.md` 覆盖：git / .NET 10 运行时装机；`Storage:Root`、数据库、Data Protection 密钥三处备份；首次启动初始化口令；IIS 或 Nginx 反代到 `http://127.0.0.1:5214`；只读 Gitblit 账号的权限建议；spec 第 24 节 15 条验收场景的手工步骤。

- [ ] **Step 4: 全量验证并提交**

```bash
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj
npm --prefix code-sentinel-web run test:unit -- --run
npm --prefix code-sentinel-web run test:e2e
npm --prefix code-sentinel-web run build
git add . && git commit -m "test: add e2e smoke and deployment documentation"
```

---

## 任务依赖

```
1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11 → 12 → 13 → 14 → 15
                                                      └→ 16 → 17 → 18 → 19 → 20 → 21 → 22
```

- Task 7 不依赖 Task 8；Task 9 可在 Task 8 之后并行，但 Task 11 依赖 Task 9 的解析器。
- 前端 16 依赖 Task 15 的接口稳定；17–21 按顺序做，页面之间共享路由与 store。
- 每个任务结束都必须 `dotnet test` 或前端单测通过再提交，不合并“先写后补测试”的提交。

## 自检记录

- **Spec 覆盖**：数据模型（Task 1）、状态机（7/8）、镜像与同步（4/5）、diff 与锚点（9/11）、合并闭环（13）、权限（2/15）、接口（各任务）、通知（6/14/21）、页面（16–21）、部署（22）、已看过（12/19）均有对应任务。
- **类型一致性**：枚举与 DTO 名称在 Task 1 固定，后续任务只引用；diff 模型在 Task 9 固定，Task 19 直接消费。
- **Review Focus**：五条分别落在 Task 4/9（路径与编码）、Task 9/19（大文件）、Task 8/11/13（rebase/删除/A 前进）、Task 6/7（并发）、Task 3/4/15（凭据）。
