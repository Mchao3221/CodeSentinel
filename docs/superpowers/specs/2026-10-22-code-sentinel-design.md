# CodeSentinel 设计文档

日期：2026-10-22
状态：已批准

## 1. 意图与背景

公司代码托管在 Gitblit 上，没有 PR（Pull Request）流程。现在的评审靠口头、聊天工具和提交说明，结论不留痕，谁看过哪个版本、意见有没有改掉，都说不清。

目标是在 Gitblit 之外补一层独立的代码评审系统：开发者把自己的分支提交评审，审核人按行看 diff、评论、通过或打回，全部通过后通知合并人在 Gitblit 里手工合并。系统**只读**仓库，不代理 git 托管，也不代替合并。

为什么现在做：现有的 Gitblit 1.8.0 没有 REST API，自带的 Tickets 只有整单评论、没有行级评论和审批结论；升级到 1.10 也不会补上接口能力。与其改造 Gitblit，不如做一个只依赖 git 协议的外挂层，顺带让这套系统以后能接任何 git 服务。

## 2. 目标

- 一个仓库可以被登记进系统，系统能读取它的分支、提交和文件内容（只读）。
- 提交人能显式发起一次评审：选仓库、目标分支 A、自己的分支 B、审核人。
- 审核人能看到 A 与 B 之间的完整 diff，按文件、按行评论，给出“通过”或“要求修改”。
- 提交人继续往 B 推代码时，评审自动跟到新版本，旧结论自动作废，不产生新的评审单。
- 被挑中的审核人全部通过后，系统通知合并人。
- 合并在 Gitblit 手工完成后，系统能自动发现并关闭评审单。
- 全过程有通知和审计记录。

## 3. 非目标

- 不做代码托管，不提供 clone/push 地址。
- 不做自动合并、不做冲突解决。
- 不做 CI/CD、不做代码扫描、不做质量门禁。
- 不做邮件、短信、企业微信通知（第一版只有站内）。
- 不做 SSO / LDAP / 域账号集成（第一版自建账号）。
- 不做多租户、不做移动端。
- 不做全文搜索、代码搜索、仓库文件树浏览（只展示变更文件，可展开全文）。

## 4. 反目标

以下情况即使功能“能用”也算失败：

- 系统对 Gitblit 仓库产生了任何写操作。
- 评审单状态与实际代码不一致（比如显示“已通过”，但 B 上已经有了没人看过的提交）。
- 审核人需要手工记住自己看过哪个 commit。
- 为了用这套系统，开发者必须改变推送代码的习惯。

## 5. 约束

- 公司 Gitblit 为 1.8.0，只有 JSON-RPC（管理用途）、Atom feed、raw 文件读取和 git 协议，**没有 diff/commit 的 HTTP 接口**。
- 只能拿到只读的仓库凭据；第一版不申请服务账号，不安装任何 Gitblit hook。
- 服务器上必须有 git 可执行文件。
- 前端遵循团队规范：Vue 3。
- 后端沿用仓库里已有的 .NET 10 ASP.NET Core 骨架。

## 6. 已定的决策（由需求方拍板）

| 决策 | 结论 |
|---|---|
| 与 Gitblit 的关系 | 只依赖 git 协议，不调用 Gitblit 专有接口 |
| 仓库访问 | 只读 |
| 评审发起 | 显式“提交评审”，推送本身不生成评审单 |
| 审核人 | 提交人自己挑，挑到的人全部通过才算通过 |
| 分支更新 | 评审对象自动跟到 B 的最新提交，旧结论作废 |
| 合并 | 合并人在 Gitblit 手工完成；系统只检测结果 |
| 账号与权限 | 系统自建；管理员建仓库、拉人进仓库 |
| 通知 | 仅站内 |
| 门禁 | 第一版不做，后续作为可插模块扩展 |

## 7. 方案选择

考虑过三条路：

1. **自研单体（.NET 后端 + Vue 3 前端 + 每仓库裸镜像）**——选中。
2. 全 C#（Blazor Server）——diff 查看器的交互组件生态弱，团队规范是 Vue，放弃。
3. 现成开源（Gerrit / Review Board / Gitea）——Gerrit 要求接管仓库和 refs；Review Board 缺少“挑审核人→全部通过→通知合并人”的流程，定制成本高；Gitea 会复制代码和账号，合并还要反推回 Gitblit。放弃。

diff 的获取方式同样只有一种靠谱做法：服务端保留裸镜像，用 git 命令算 merge-base 和 diff。每次请求现 clone 太慢太脆；抓 Gitblit 的 HTML 页面不通用也不稳。

## 8. 系统结构

```
┌──────────────────────┐        ┌───────────────────────┐
│  Vue 3 SPA           │  HTTPS │  ASP.NET Core 10      │
│  (code-sentinel-web) │◄──────►│  REST API + 静态资源   │
└──────────────────────┘        │                       │
                                │  ┌─────────────────┐  │
                                │  │ BackgroundService│ │
                                │  │ 镜像同步/合并检测 │ │
                                │  └────────┬────────┘  │
                                │           │ git CLI    │
                                │  ┌────────▼────────┐  │
                                │  │ 裸镜像目录       │  │
                                │  └────────┬────────┘  │
                                │  ┌────────▼────────┐  │
                                │  │ SQLite / EF Core │  │
                                │  └─────────────────┘  │
                                └───────────┬───────────┘
                                            │ 只读 fetch / ls-remote
                                    ┌───────▼────────┐
                                    │  Gitblit (git) │
                                    └────────────────┘
```

单进程部署：API、后台任务、静态资源在同一个进程里。规模上来后可以把后台任务拆成独立进程，接口不变。

## 9. 技术栈

- 后端：.NET 10、ASP.NET Core、EF Core、ASP.NET Core Identity（账号与密码哈希）、Serilog（结构化日志）。
- 数据库：默认 SQLite；EF Core 提供程序可切换 PostgreSQL / SQL Server，第一版只验证 SQLite。
- 前端：Vue 3 + Vite + TypeScript + Element Plus + Pinia。diff 由后端解析成结构化的“文件 → hunk → 行”，前端按行渲染，用 Shiki 做语法高亮；不用 CodeMirror，行级评论的定位和测试都更稳。
- git：调用命令行 `git`（不引入 LibGit2Sharp）。服务启动时检测 `git --version`。
- 测试：后端 xUnit + 可丢弃的临时裸仓库做集成测试；前端 Vitest（组件）+ Playwright（关键流程）。

## 10. 数据模型

所有表带 `created_at` / `updated_at`；主键用自增整型或 GUID（实现时统一，建议 GUID 便于日志追踪）。

### users
| 字段 | 说明 |
|---|---|
| id | 主键 |
| username | 唯一，登录名 |
| display_name | 显示名 |
| email | 可选，仅展示 |
| password_hash | Identity 负责 |
| is_admin | 系统管理员 |
| is_active | 停用后不能登录 |
| must_change_password | 首次登录强制改密 |

不开放自助注册，账号由管理员创建。

### repos
| 字段 | 说明 |
|---|---|
| id | 主键 |
| name | 系统内显示名，唯一 |
| git_url | 仓库地址（http 或 https） |
| default_base_branch | 新建评审时的默认目标分支 |
| credential_cipher | 只读凭据密文（ASP.NET Data Protection 加密） |
| credential_kind | password / token |
| sync_interval_seconds | 默认 300 |
| last_fetched_at / last_error | 同步状态 |
| is_active | 停用后不再同步，但历史可看 |

### repo_members
| 字段 | 说明 |
|---|---|
| repo_id, user_id | 联合唯一 |
| is_merger | 是否合并人（可多个） |

仓库成员才能看该仓库的评审、被挑为审核人、发评论。

### reviews
| 字段 | 说明 |
|---|---|
| id | 主键 |
| repo_id | 所属仓库 |
| base_branch | 目标分支 A |
| head_branch | 提交人的分支 B |
| submitter_id | 提交人 |
| title / description | 标题与说明 |
| status | OPEN / CHANGES_REQUESTED / APPROVED / MERGED / CLOSED |
| current_revision_id | 当前评审版本 |
| base_sha | 本版本的基线，创建版本时锁定为 A 当时的 head，**不随 A 前进变化** |
| head_sha | 本版本评审的 B 的提交 |
| approved_at / merged_at / closed_at | 时间戳 |
| merge_commit_sha | 检测到的合并结果（A 上的提交） |
| merge_hint / merge_hint_count | 连续未检测到合并的同步次数，用于 squash 提示 |
| close_reason | 关闭原因 |

同一 `(repo_id, head_branch)` 同时只允许一个活跃评审单（status 不是 MERGED/CLOSED）。重复提交返回已有单号。

### review_revisions
每发现 B 有新提交就追加一条，评审单永远指向最新一条。

| 字段 | 说明 |
|---|---|
| id | 主键 |
| review_id | 所属评审 |
| seq | 从 1 递增 |
| base_sha / head_sha | 本版本计算 diff 的两端（提交时锁定） |
| created_at | |

版本只增不改。B 的 head 一旦变化（新提交、amend、rebase、把 A 合进 B）就追加新版本，不做“内容相同就沿用”的判断——评审对象是提交，不是文件内容的哈希。

### review_reviewers
| 字段 | 说明 |
|---|---|
| review_id, user_id | 联合唯一 |
| state | PENDING / APPROVED / CHANGES_REQUESTED / REMOVED（新版本出现后除 REMOVED 外全部回到 PENDING） |
| state_revision_id | 结论对应的版本，正常情况下等于 current_revision_id |
| note | 结论附言 |

### review_decisions（只追加）
| 字段 | 说明 |
|---|---|
| review_id / revision_id / user_id | |
| state | APPROVED / CHANGES_REQUESTED |
| note | |
| created_at | |

历史结论可追溯；当前结论由 review_reviewers 缓存，两者同时写。

### comments
| 字段 | 说明 |
|---|---|
| id | |
| review_id / revision_id | 评论创建时所属版本 |
| author_id | |
| file_path | 文件路径 |
| side | OLD / NEW |
| line_number | 行号（在该侧文件中的行号） |
| anchor_text | 该行原文，用于重新定位 |
| blob_sha | 该侧文件内容的 blob id |
| body | 内容（Markdown 子集） |
| parent_id | 回复某条评论 |
| status | OPEN / RESOLVED / OUTDATED / DELETED |
| resolved_by / resolved_at | |

### review_file_views
逐文件的“已看过”标记，只属于个人，不参与通过判定。

| 字段 | 说明 |
|---|---|
| review_id / user_id / file_path | 联合唯一 |
| blob_sha | 勾选时该文件新侧的 blob |
| viewed_at | |

读取时比较 `blob_sha` 与当前版本该文件的 blob：一致才算“已看过”，文件变了自动视为未看过。旧记录保留，内容改回原样也不会恢复勾选（以 blob 比对为准）。

### notifications
| 字段 | 说明 |
|---|---|
| id / user_id | |
| type | REVIEW_SUBMITTED / CHANGES_REQUESTED / NEW_COMMITS / APPROVED / MERGED / CLOSED / COMMENT_REPLY |
| review_id | 可空 |
| payload_json | 渲染所需的最小字段 |
| read_at | 空表示未读 |

### audit_log
actor_id、action、target_type、target_id、detail_json、created_at。记录登录、仓库与成员变更、提交评审、结论、删除评论、标记合并、关闭。

## 11. 评审单状态机

```
            提交评审
               │
               ▼
            OPEN ──────────────┐
           │    ▲              │ 任一审核人要求修改
           │    │              ▼
           │    │      CHANGES_REQUESTED
           │    │              │
           │    └── B 有新提交 ─┘（回到 OPEN，结论全部作废）
           │
           │ 全部审核人通过
           ▼
        APPROVED ──── 合并人手工合并、系统检测到 ──► MERGED
           │
           └── 提交人/合并人关闭 ──► CLOSED

任意状态：管理员可关闭；B 分支被删除时提示关闭。
```

规则：

- **提交评审**是唯一创建评审单的入口，推送不创建。
- 创建时锁定 `base_sha`（A 当时的 head）和 `head_sha`（B 当时的 head），生成 `revision 1`。
- **新版本的唯一触发是 B 的 head SHA 变化**：新提交、amend、rebase、把 A 合进 B，一律生成新版本，并把所有未移除的审核人重置为 `PENDING`。不比较 diff 是否相同，旧结论留在 `review_decisions`。
- **A 前进不生成新版本、不重置结论**：只是重新计算合并可行性，冲突风险在界面上提示。
- 任一审核人要求修改 → `CHANGES_REQUESTED`；该状态下新提交直接回到 `OPEN`。
- 只有被挑中的审核人能给出结论；提交人不能作为自己评审单的审核人。
- 审核人可随时改判或撤回自己的结论（撤回即回到 `PENDING`）。任何结论变化后按当前状态重算：有人要求修改 → `CHANGES_REQUESTED`；全部通过 → `APPROVED`；否则 `OPEN`。
- 全部审核人 APPROVED → `APPROVED`，通知合并人；此时审核人改判会让状态退出 `APPROVED`。
- `APPROVED` 不是终态，B 再有提交就回到 `OPEN`。
- 评审单必须始终至少有一个审核人：会导致审核人列表为空的移除操作一律拒绝。

## 12. 仓库接入与镜像同步

- 管理员新增仓库时填写名称、git 地址、只读凭据、默认目标分支。系统立即执行一次 `git ls-remote` 验证连通性与凭据，失败不允许保存。
- 每个仓库在 `{StorageRoot}/mirrors/{repoId}.git` 建立裸镜像：
  `git init --bare` + `git remote add origin <url>` + `git fetch --prune origin '+refs/heads/*:refs/remotes/origin/*'`。
- 后台任务每 60 秒扫描一次到期仓库（按各自的 `sync_interval_seconds`），串行 fetch；同一仓库同时只允许一个 fetch。
- 打开评审详情时可按需触发一次刷新，同一仓库 10 秒内不重复。
- 凭据不写进 URL、不写进日志。通过 `GIT_ASKPASS` 临时脚本或 `http.extraHeader` 传给 git 进程，进程结束即清理；日志中的 URL 一律脱敏。
- **传输方式**：生产与测试都是**纯 HTTP**，第一版只支持 http/https（https 走系统信任链，不额外提供 CA 或跳过校验的配置），**不做 SSH**，因此不需要管密钥文件、known_hosts 和 Windows 临时文件权限。纯 HTTP 意味着凭据在内网明文传输，缓解手段是：凭据只读、只授予受管仓库、只在服务器上保存、日志脱敏。
- fetch 失败：记录 `last_error`，界面显示“数据可能不是最新”，不清空已有数据，不阻塞浏览。
- 第一版不做 webhook。以后可以在 Gitblit 配 post-receive 通知我们，触发即时同步。

## 13. diff 与评论锚点

- 基线：`git merge-base <base_sha> <head_sha>`。展示 `base...head`（三点）的 diff，与 GitHub PR 一致。`base_sha` 取评审版本里锁定的值，A 前进后不重算 diff，保证审核人看到的就是他审过的内容。
- 文件列表：`git diff --numstat --find-renames <base>...<head>`。
- 单文件：`git diff --unified=3 --find-renames <base>...<head> -- <path>`。
- 限制：单次最多 300 个文件；单文件 diff 超过 2 MB 或二进制时只显示“文件过大/不支持预览”；超限不影响评论（可对文件整体评论，行级评论不可用）。
- 结果按 `(repoId, baseSha, headSha)` 缓存在磁盘，版本不变时直接复用。
- 评论锚点存四元组：路径、侧（旧/新）、行号、该行原文，外加 blob sha。新版本到来时：
  1. 用 diff 的行映射把旧行号映射到新行号；
  2. 校验该行原文是否一致；
  3. 不一致则在 ±5 行内找相同原文；
  4. 仍找不到 → `OUTDATED`，评论保留但默认折叠。
- 评论与版本绑定，但展示时始终按“当前版本能定位到哪”展示，历史版本可切换查看。

## 14. 合并闭环

- 每次同步后检查：`git merge-base --is-ancestor <head_sha> <A 当前 head>` 为真 → 评审单置为 `MERGED`，记录 `merge_commit_sha = A 当前 head`，通知提交人和审核人。
- **squash / rebase 合并**时 B 的 head 不会成为 A 的祖先，自动检测会失败。处理：
  - 已通过但连续 3 次同步（默认 15 分钟）仍未检测到时，在评审单上提示“未检测到合并，可能使用了 squash”；
  - 合并人可点“标记为已合并”，需填写合并后的提交（可选）；
  - 提交人可关闭评审单。
- 目标分支 A 前进：只要 B 没有新提交，不重置审核结论，diff 也不重算；用 `git merge-tree --write-tree <A 当前 head> <B head>` 判断合并可行性，有冲突则显示“目标分支已前进，合并可能有冲突”。该命令只算不写，不碰工作区。
- B 分支被删除：若已合并 → 正常关单；未合并 → 标记 `CLOSED`，原因“分支已删除”。

## 15. 权限

| 角色 | 权限 |
|---|---|
| 系统管理员 | 用户增删改停用；仓库增删改；任意仓库成员管理；查看所有评审；关闭任意评审；可移除评审单上的审核人 |
| 仓库成员 | 查看本仓库评审；发起评审；评论；被挑为审核人时给结论 |
| 合并人（仓库级标记） | 仓库成员的全部权限 + 接收合并通知 + 标记已合并 |
| 提交人 | 编辑自己评审的标题/说明；关闭自己提交的评审；追加审核人、移除尚未给结论的审核人；不能审核自己的评审 |

- 所有接口按“当前用户是否属于该仓库”鉴权，管理员除外。
- 审核人只能从该仓库成员中选择，且排除提交人。
- 停用用户不能登录，其历史评论与结论保留。

## 16. 接口清单（REST，同源，`/api` 前缀）

**认证**
- `POST /auth/login`、`POST /auth/logout`、`GET /auth/me`、`POST /auth/change-password`

**用户（管理员）**
- `GET /users`、`POST /users`、`PATCH /users/{id}`、`POST /users/{id}/reset-password`

**仓库**
- `GET /repos`、`POST /repos`、`PATCH /repos/{id}`、`POST /repos/{id}/test-connection`
- `GET /repos/{id}/members`、`PUT /repos/{id}/members`（整体覆盖：成员列表 + 合并人标记）
- `POST /repos/{id}/sync`
- `GET /repos/{id}/branches`（来自镜像，支持前缀过滤）
- `GET /repos/{id}/branches/unreviewed`（领先目标分支、尚未开单的分支）

**评审**
- `GET /reviews?scope=mine|to_review|repo&repoId=&status=&page=`
- `POST /reviews`
- `GET /reviews/{id}`
- `POST /reviews/{id}/refresh`（触发一次同步并刷新版本）
- `POST /reviews/{id}/decisions`（body: state, note；state 可为 APPROVED / CHANGES_REQUESTED / PENDING）
- `POST /reviews/{id}/reviewers`（提交人或管理员追加审核人）
- `DELETE /reviews/{id}/reviewers/{userId}`（提交人仅能移除尚未给结论的人；管理员可移除任何人）
- `POST /reviews/{id}/close`、`POST /reviews/{id}/mark-merged`
- `GET /reviews/{id}/revisions`

**diff 与评论**
- `GET /reviews/{id}/files`
- `GET /reviews/{id}/diff?path=`
- `GET /reviews/{id}/blob?path=&side=`（展开全文）
- `PUT /reviews/{id}/files/viewed`（body: path, viewed；勾选/取消“已看过”）
- `POST /reviews/{id}/comments`、`PATCH /comments/{id}`、`DELETE /comments/{id}`
- `POST /comments/{id}/resolve`、`POST /comments/{id}/unresolve`

**通知**
- `GET /notifications?unreadOnly=`、`POST /notifications/read`（批量）、`GET /notifications/unread-count`

约定：列表分页；错误返回 `{ code, message, traceId }`；时间统一 UTC ISO-8601。

## 17. 通知

- 触发点：提交评审 → 审核人；打回 → 提交人；B 有新提交 → 审核人；全部通过 → 合并人 + 提交人；检测到合并 → 提交人 + 审核人；被关闭 → 相关人；回复评论 → 被回复人。
- 站内信列表 + 未读数角标，前端每 30 秒轮询未读数，打开通知页时拉列表。
- 同一评审同一事件在 5 分钟内合并为一条，避免刷屏。
- 不做已读回执之外的推送。

## 18. 前端页面

1. **登录**（含首次登录强制改密）
2. **评审列表**：三个标签（待我评审 / 我提交的 / 全部），按仓库、状态、关键字过滤
3. **新建评审**：选仓库 → 选目标分支（默认分支预选）→ 选自己的分支（展示未开单分支优先）→ 勾选审核人 → 标题/说明 → 提交
4. **评审详情**：
   - 顶部：状态、提交人、审核人及各自结论、base/head、版本切换、刷新
   - 变更文件列表 + 行级 diff + 行评论（支持回复、解决、折叠）
   - 每个文件一个“已看过”勾选，顶部显示“已看过 3/5”；文件内容没变时勾选跨版本保留
   - 右侧操作：通过 / 要求修改（带附言）、关闭、标记已合并
   - 通过后显示“合并指引”：合并哪两个分支、当前 head sha、合并在 Gitblit 完成
5. **仓库管理**（管理员）：仓库增删改、连通性测试、同步状态、成员与合并人
6. **用户管理**（管理员）
7. **通知中心**

## 19. 我做的判断（需求方未明确，按此实现，可推翻）

1. **技术栈细节**：SQLite 默认、CodeMirror 6、Serilog、Identity 密码哈希；后续可换库。
2. **提交人不能审核自己的单**。
3. **A 前进不重置结论**；只有 B 的 head 变化（也就是任何新提交）才生成新版本并作废结论。合并冲突风险单独提示。
4. **合并人是仓库级标记**，可以有多个，从成员里指定。
5. **squash 合并**靠人工“标记为已合并”兜底，不做内容比对。
6. **通知轮询 30 秒**，不引入 WebSocket。
7. **评审单粒度是整条分支**（merge-base..B），不支持只评审某几个提交。
8. **活跃评审单按“仓库 + 分支 B”唯一**：同一分支只能有一个未结束的评审单，不同分支互不影响。
9. **审核人给出“要求修改”后**，若提交人认为不成立，只能由该审核人改判或管理员关闭，不支持提交人申诉流程。
10. **删除评论为软删除**，保留审计。
11. **首版不做代码高亮的主题定制**，用默认亮色主题。
12. **结论可撤回**：审核人在评审未结束前可把结论改回 `PENDING`，系统按当前结论集合重算状态并通知提交人。
13. **审核人名单可调整**：提交人可追加审核人（新增者必须通过）、可移除尚未给结论的人；管理员可移除任何人。被移除者的结论作废、不计入通过条件。任何会导致审核人列表为空的移除都拒绝。
14. **停用用户或被移出仓库的审核人**：自动从所有未结束评审的审核人名单中移除，界面标注“已移除”，不计入通过条件，并通知提交人补人。
15. **每个仓库至少一个合并人**：创建和保存仓库成员时校验，不允许移除最后一个合并人；通知兜底同时发给系统管理员。
16. **git 传输**：生产与测试均为纯 HTTP，只做 http/https，不做 SSH、不做自定义 CA 或跳过校验的开关。
17. **逐文件“已看过”是个人标记**：只影响自己的浏览进度，不影响通过判定；比对 blob 判断是否失效。第一版不做“只看我上次之后的变化”那种按人增量 diff。

## 20. 留给构建者

- 具体 UI 视觉与交互细节（布局、间距、快捷键）。
- 评论重新定位算法的参数（±5 行是否合适、是否需要按 diff hunk 对齐）。
- 缓存与分页的具体实现（磁盘缓存淘汰策略）。
- 日志、监控、健康检查的具体方案。
- 数据库迁移与种子管理员账号的方式。
- 反向代理与 HTTPS 的具体配置（IIS / Nginx）。
- 测试用例的具体划分。

## 21. 部署与运维

- 依赖：.NET 10 运行时、git ≥ 2.30；Windows Server 或 Linux 均可。
- 目录：`app/`、`data/codesentinel.db`、`data/mirrors/`、`data/keys/`（Data Protection 密钥）。
- 备份：数据库 + 镜像目录 + 密钥目录三者一起备份；任意一个丢失都会影响使用（镜像可重建，密钥丢失会导致凭据无法解密、登录态失效）。
- 配置项：`Git:ExecutablePath`、`Storage:Root`、`Sync:DefaultIntervalSeconds`、`Auth:SessionHours`。
- 首次启动：执行迁移，若无管理员则输出一次性初始化口令到控制台，用该口令创建第一个管理员。
- 只读凭据建议：在 Gitblit 建专用账号，只授予受管仓库的读权限。

## 22. 安全

- 密码：Identity 默认 PBKDF2，最少 8 位，管理员重置后强制改密。
- 会话：同源 HttpOnly Cookie；非 GET 请求校验 antiforgery token。
- 传输：系统自身建议 HTTPS（反向代理终止 TLS）；对 Gitblit 的 fetch 按已定方案走内网 HTTP，凭据只读、只在 fetch 时使用、日志脱敏。纯 HTTP 是现有 Gitblit 的既有暴露面，不因本系统扩大。
- 凭据：Data Protection 加密存储；进程内只在 fetch 时短暂使用；日志脱敏，禁止打印含凭据的 URL。
- 授权：每个请求校验仓库成员身份，管理员例外；越权返回 404 而不是 403，避免暴露仓库存在性。
- 审计：第 10 节列出的动作全部落库，审计日志只读、不可在界面删除。

## 23. 规模与性能假设

- 单实例：≤ 50 个仓库、≤ 100 个用户、每年 ≤ 1000 个评审单、镜像总大小 ≤ 50 GB。
- 单次 diff 计算（500 行以内）在 1 秒内完成；缓存命中时 200 ms 内返回。
- 同步任务单线程串行，fetch 失败退避（1、2、4、8 分钟，上限 15 分钟）。
- 超过上述规模需要评估拆分后台任务、换 PostgreSQL、加只读副本。

## 24. 验收场景

1. 管理员建仓库（测试 Gitblit 实例）→ 拉两个成员 → 指定合并人。
2. 开发者推送分支 B（三次提交）→ 系统不产生评审单。
3. 开发者提交评审，挑一名审核人 → 审核人收到站内通知。
4. 审核人看到 A...B 的完整 diff，按行评论，点“要求修改” → 提交人收到通知。
5. 提交人继续推两次提交 → 评审自动出现新版本，旧结论作废，旧评论要么重新定位要么标为过期。
6. 审核人通过 → 合并人收到通知；此时再推一次提交 → 状态回到待评审，需重新通过。
7. 合并人在 Gitblit 合并 → 下一次同步后评审单自动变为“已合并”，并通知相关人。
8. squash 合并场景：不会自动变为已合并，出现提示，合并人手工标记后关闭。
9. 非仓库成员访问评审链接 → 404。
10. 审计日志能还原上述每一步的操作人、时间和对象。
11. 审核人通过后撤回结论 → 状态回到待评审，提交人收到通知。
12. 管理员移除一名审核人 → 被移除者的结论不再计入；尝试移除到空被拒绝。
13. 停用一个待评审的审核人 → 自动从名单移除并通知提交人补人。
14. 仓库成员配置里没有合并人 → 保存被拒绝。
15. 审核人勾选 3 个文件“已看过”后，B 再提交：没变的文件保持勾选，变了的自动取消，顶部进度随当前版本重算。

## 25. 后续（不在本期）

- **门禁层**：申请 Gitblit 服务账号与 pre-receive hook，把“合并”收进系统，实现“未通过不能合”。届时增加：合并按钮、合并冲突检测、push 前校验、hook 脚本。
- 邮件 / 企业微信通知。
- Gitblit 的 webhook 接入，替代轮询。
- LDAP / SSO 登录、仓库成员自动同步。
- 多仓库批量评审、评审模板、CODEOWNERS 式自动审核人。
