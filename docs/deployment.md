# CodeSentinel 部署与运维手册

本文覆盖生产部署所需的运行前提、目录规划、首次初始化、反向代理、凭据管理、备份恢复、升级以及 15 条手工验收场景。设计背景见 `docs/superpowers/specs/2026-10-22-code-sentinel-design.md`，日常开发命令见根目录 `README.md`，本文不重复开发说明。

## 1. 部署拓扑

```
浏览器 ──HTTPS──► 反向代理（IIS / Nginx）
                    ├── 静态文件：code-sentinel-web 构建产物（SPA）
                    └── /api/* ──HTTP──► 127.0.0.1:5214（CodeSentinel 后端）
                                              └── git over HTTP(S) ──► Gitblit（只读账号）
```

- 后端只提供 `/api`，不托管前端页面；前端构建产物由反向代理或 IIS 站点直接提供。
- 后端建议只监听回环地址 `127.0.0.1:5214`，不要直接暴露到网络。
- 前端使用 HTML5 history 路由，非静态文件路径需要回退到 `index.html`。

## 2. 运行前提

| 依赖 | 要求 | 说明 |
|---|---|---|
| git | ≥ 2.30 | 服务器上必须可执行；非 PATH 安装时配置 `Git:ExecutablePath` |
| .NET | .NET 10 运行时（ASP.NET Core Runtime） | 只需运行时；构建机需 SDK |
| 操作系统 | Windows Server 或 Linux | 两者均可 |
| 数据库 | 无独立服务 | SQLite 文件随应用部署 |

启动时后端会自检 git；不可用时生产环境直接启动失败，并提示检查 `Git:ExecutablePath`。

## 3. 目录布局

推荐的单机布局（Windows 示例，Linux 用对应路径）：

```
C:\CodeSentinel\
├── app\                       后端发布物（CodeSentinel.dll、appsettings*.json、依赖 DLL）
├── web\                       前端构建产物（index.html、assets/）
└── data\
    ├── codesentinel.db        SQLite 数据库
    ├── codesentinel.db-wal    WAL 文件（运行中可能存在）
    ├── codesentinel.db-shm    WAL 共享内存文件（运行中可能存在）
    ├── mirrors\               每仓库裸镜像 {repoId}.git（可重建）
    ├── keys\                  Data Protection 密钥（不可重建）
    ├── cache\                 diff 结果缓存（可随时删除）
    └── init-admin-token.txt   首次启动的一次性初始化口令（初始化成功后自动删除）
```

说明：

- `data/mirrors/`、`data/keys/`、`data/cache/` 由 `Storage:Root` 决定，默认是内容根目录下的 `data`。
- 数据库路径由 `ConnectionStrings:Default` 决定，默认 `Data Source=data/codesentinel.db`；它**不跟随** `Storage:Root`，迁移存储目录时要一起改。
- 初始化口令固定写在**内容根目录**下的 `data/init-admin-token.txt`。生产环境请把服务的工作目录/内容根设置为 `C:\CodeSentinel`，或在启动日志中确认口令文件的实际位置。

## 4. 构建与安装

```bash
# 后端发布
dotnet publish CodeSentinel/CodeSentinel -c Release -o /srv/codesentinel/app

# 前端构建
npm --prefix code-sentinel-web ci
npm --prefix code-sentinel-web run build
# 将 code-sentinel-web/dist/ 的内容复制到 /srv/codesentinel/web/
```

启动后端（Linux 示例，Windows 用服务或计划任务）：

```bash
cd /srv/codesentinel
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS=http://127.0.0.1:5214 \
dotnet app/CodeSentinel.dll
```

Windows 可用 `sc.exe` / NSSM 注册为服务，务必把工作目录设置为 `C:\CodeSentinel`，否则相对路径会落到系统目录。也可以直接在配置中使用绝对路径：

```json
{
  "ConnectionStrings": { "Default": "Data Source=D:\\CodeSentinel\\data\\codesentinel.db" },
  "Storage": { "Root": "D:\\CodeSentinel\\data" },
  "Sync": { "PollSeconds": 60 },
  "Auth": { "SessionHours": 8 },
  "Git": { "ExecutablePath": "C:\\Program Files\\Git\\cmd\\git.exe" }
}
```

`appsettings.Production.json` 与 `appsettings.json` 合并，后者提供默认值。环境变量同样生效，例如 `Storage__Root`、`ConnectionStrings__Default`。

## 5. 首次启动与初始化

1. 启动后端。若系统中还没有管理员，控制台会输出一次性初始化口令，并写入 `data/init-admin-token.txt`。
2. 在**系统对网络开放之前**用该口令创建第一个管理员：

```bash
curl -X POST http://127.0.0.1:5214/api/setup/admin \
  -H "Content-Type: application/json" \
  -d '{
    "token": "<控制台输出的口令>",
    "username": "admin",
    "displayName": "系统管理员",
    "password": "<至少 8 位>"
  }'
```

3. 成功后口令文件自动删除，接口再次调用会返回“系统已初始化”。
4. 用管理员登录，创建用户与仓库，再把用户加入仓库并指定合并人。

注意：

- 系统**不内置默认账号**，开发、测试、生产都走同一流程。
- 初始化口令只出现一次。若在创建管理员前丢失，重启服务且仍无管理员时会重新生成。
- 管理员创建后没有自助找回入口；密码遗失请按单位数据库运维流程处理，本期不提供 CLI 重置。

## 6. 反向代理

### 6.1 IIS

前置：安装 **URL Rewrite** 与 **Application Request Routing (ARR)**，并在 ARR 中启用代理。

- 站点物理路径：`C:\CodeSentinel\web`。
- 应用程序池：.NET CLR 版本选择“无托管代码”。
- 在站点根目录放置 `web.config`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <proxy enabled="true" preserveHostHeader="true" timeout="00:02:00" />
    <rewrite>
      <rules>
        <rule name="ApiProxy" stopProcessing="true">
          <match url="^api/(.*)" />
          <action type="Rewrite" url="http://127.0.0.1:5214/api/{R:1}" />
        </rule>
        <rule name="SpaFallback" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
            <add input="{REQUEST_URI}" pattern="^/api/" negate="true" />
          </conditions>
          <action type="Rewrite" url="/index.html" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
```

要点：

- `ApiProxy` 必须排在 SPA 回退之前，否则 `/api/*` 会被回退成 `index.html`。
- 大 diff 或首次同步可能较慢，ARR 超时建议不小于 120 秒。
- TLS 证书绑定在 IIS 站点上，后端只走回环 HTTP。

### 6.2 Nginx

```nginx
server {
    listen 443 ssl;
    server_name codesentinel.example.com;

    ssl_certificate     /etc/ssl/certs/codesentinel.crt;
    ssl_certificate_key /etc/ssl/private/codesentinel.key;

    root  /srv/codesentinel/web;
    index index.html;

    client_max_body_size 4m;

    location /api/ {
        proxy_pass http://127.0.0.1:5214;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 120s;
    }

    location / {
        try_files $uri $uri/ /index.html;
    }

    # 静态资源带内容哈希，可长缓存；index.html 必须每次回源
    location = /index.html {
        add_header Cache-Control "no-cache";
    }
}
```

健康检查可直接访问 `GET /api/health`（返回 `{"status":"ok"}`，无需登录）。

## 7. Gitblit 只读账号

- 新建专用账号（例如 `codesentinel-ro`），只授予**受管仓库**的读权限；不授予管理权限，不允许推送。
- 在“仓库管理 → 新增/编辑仓库”中填写 `用户名:密码或令牌`，凭据经 Data Protection 加密后存入数据库，不写日志、不拼进 URL。
- 不要复用个人账号或管理员账号；按仓库最小授权，避免一个账号读取全部仓库。
- 轮换密码后必须逐个仓库更新凭据并执行“测试连接”；旧密文不会自动更新。
- 网络层限制：只允许 CodeSentinel 服务器访问 Gitblit 的 HTTP(S) 端口。
- 删除或丢失 Data Protection 密钥后，已保存的凭据无法解密，只能重新录入（见第 9 节）。

## 8. 传输方式与 HTTPS 注意事项

- 第一版对 Gitblit 使用 **纯 HTTP** 是可接受的既有暴露面，但凭据会以 Base64 形式在内网传输。缓解措施：专用只读账号、按仓库授权、限制来源 IP、日志脱敏。
- 若 Gitblit 后续切换到 **HTTPS**，且证书来自 Windows 证书存储，应用服务器上的 git 需要显式使用 schannel 后端：

```bat
git config --system http.sslBackend schannel
```

  等价于每次 git 命令加 `-c http.sslBackend=schannel`。git 自带的 OpenSSL 默认不信任 Windows 证书存储，不设置会报证书校验失败。自签名证书应把 CA 导入受信任根，不要关闭校验。

- 浏览器到反向代理建议始终使用 HTTPS。后端 Cookie 为 HttpOnly + SameSite=Lax；若要求 Cookie 带 `Secure` 标记，需要让后端识别代理转发的 `X-Forwarded-Proto`（当前程序未启用 ForwardedHeaders，直接对后端做 TLS 或后续版本补充）。

## 9. 备份与恢复

三处数据必须**成组备份**，并记录同一时间点：

| 对象 | 路径 | 丢失后果 | 重建方式 |
|---|---|---|---|
| 数据库 | `data/codesentinel.db`（连同 `-wal`、`-shm`） | 用户、仓库、评审、评论、结论、审计全部丢失 | 不可重建，只能依赖备份 |
| 镜像 | `data/mirrors/` | 无法展示历史 diff，同步需重新拉取 | 可从 Gitblit 重新 fetch；需要凭据、网络和时间 |
| Data Protection 密钥 | `data/keys/` | 仓库凭据密文无法解密；现有登录态与防伪令牌全部失效 | 不可重建；只能重新录入所有仓库凭据 |

补充说明：

- 镜像丢失不会丢业务数据，但 Gitblit 不可用期间无法重新拉取，评审将无法查看 diff。
- 密钥丢失后，所有用户需重新登录，所有仓库凭据需重新填写；旧密文没有恢复手段。
- `data/cache/` 不需要备份，它只是 diff 结果缓存。

备份建议：

1. 低峰期停止服务，或使用 SQLite 在线备份（`sqlite3 data/codesentinel.db ".backup backup.db"`）保证一致性；直接复制运行中的数据库必须同时带上 `-wal` 与 `-shm`。
2. 将数据库、`mirrors/`、`keys/` 一起打包，落到离线介质或异地存储，并加密。
3. 定期做恢复演练：停止服务 → 恢复三处数据 → 启动 → 登录 → 打开一个评审 → 测试仓库连接。

恢复顺序：停服务 → 恢复 `app/`、`web/` 到目标版本 → 恢复 `data/` 三处 → 启动 → 检查 `/api/health`、登录、diff、同步。

## 10. 升级与缓存失效

1. 升级前完整备份数据库、镜像与密钥。
2. 停止服务，替换 `app/` 与 `web/`，保留 `data/`。
3. 启动时后端自动执行 EF Core 迁移；`data/mirrors/` 与 `data/keys/` 必须保留。
4. 若新版本调整了 diff 输出或缓存结构，停止服务后删除 `data/cache/` 即可；缓存会按需重建，不影响任何业务数据。
5. 验证：`/api/health`、登录、评审列表、详情 diff、评论、仓库同步。
6. 回滚：停止服务，恢复旧版 `app/`、`web/` 与升级前的数据库备份。数据库迁移可能不可逆，不能只回滚程序。

浏览器静态资源文件名带内容哈希，升级后无需手动清缓存；`index.html` 应配置为不缓存或短缓存。

## 11. 日常检查

- `GET /api/health` 是否正常。
- 仓库列表的“最近同步时间 / 最近同步错误”；持续失败检查 git、网络、凭据。
- 磁盘空间：镜像总量按场景上限约 50 GB，另需为缓存与备份留出空间。
- 服务日志中不应出现明文凭据；如发现，立即轮换 Gitblit 账号。
- 备份任务成功率与恢复演练记录。

## 12. 手工验收清单（对应设计文档第 24 节）

以下 15 条在测试 Gitblit 实例上执行，建议按顺序进行并记录操作人、时间与结果。

### 1. 管理员建仓库、拉成员、指定合并人

步骤：管理员登录 → 仓库 → 新增仓库（名称、git 地址、只读凭据、默认目标分支）→ 保存；编辑成员，加入两名用户并把其中一人标记为合并人 → 保存。

预期：保存时立即校验远端连通性与凭据，失败不允许保存；成员列表出现两人，合并人带标记。

### 2. 推送分支不产生评审单

步骤：开发者向测试仓库推送分支 B（三次提交）→ 等待一次同步或手动触发同步 → 打开评审列表“全部”和“我提交的”。

预期：不产生任何评审单；B 出现在新建评审的“未开单分支”中并显示领先提交数。

### 3. 提交评审并通知审核人

步骤：开发者新建评审，选择分支 B、目标分支 A、审核人甲 → 提交。

预期：进入详情页，状态“评审中”，版本 v1；审核人甲的未读数增加并收到“提交评审”通知。

### 4. 查看完整 diff、行评论、要求修改

步骤：审核人甲登录 → 打开评审 → 展开文件查看 A...B 完整 diff → 在某行添加评论 → 点击“要求修改”并填写说明。

预期：diff 行号、增删行正确；评论挂在对应行；状态变为“需修改”；提交人收到通知。

### 5. 新提交生成新版本

步骤：提交人继续向 B 推送两次提交 → 在详情页点击“刷新”或等待同步。

预期：出现 v2，状态回到“评审中”，审核人结论重置为“待评审”；旧评论能重新定位的保留，不能定位的标记为已过期并默认折叠。

### 6. 通过后合并人通知与新提交回退

步骤：审核人甲通过 → 再向 B 推送一次提交 → 刷新。

预期：通过后状态“已通过”，合并人收到通知；再次推送后生成新版本并回到“评审中”，必须重新通过。

### 7. Gitblit 合并后自动检测

步骤：合并人在 Gitblit 中把 B 合并到 A → 等待同步或刷新评审。

预期：评审自动变为“已合并”，记录合并提交；相关人收到通知。

### 8. squash 合并与手工标记

步骤：使用 squash 方式合并，使系统无法通过提交关系识别 → 刷新评审。

预期：不会自动变为已合并，出现“未检测到合并，可能使用了 squash”提示；合并人点击“标记为已合并”，确认后状态变为已合并（如需要再关闭）。

### 9. 非成员访问返回 404

步骤：用不属于该仓库的用户登录，直接访问评审详情链接。

预期：返回 404（不是 403），不暴露评审是否存在。

### 10. 审计日志可还原每一步

步骤：完成上述操作后，在数据库中查询：

```sql
SELECT created_at, actor_id, action, target_type, target_id, detail_json
FROM audit_log
ORDER BY created_at DESC
LIMIT 100;
```

预期：创建仓库、成员变更、提交评审、评论、结论、版本、合并等动作均有记录，操作人、时间、对象与明细可还原。

### 11. 撤回结论

步骤：审核人甲通过后点击“撤回结论”。

预期：状态回到“待评审”，提交人收到通知；再次通过可恢复“已通过”。

### 12. 移除审核人

步骤：评审单上有两名审核人，其中一人已通过；管理员移除该审核人；再尝试移除最后一名审核人。

预期：被移除者的结论不再计入（按剩余审核人重算状态）；移除到审核人为空的操作被拒绝。

### 13. 停用待评审审核人

步骤：管理员停用一名仍为“待评审”的审核人 → 刷新评审。

预期：该审核人自动从名单移除，提交人收到补人通知；评审仍要求至少一名审核人。

### 14. 没有合并人时保存被拒绝

步骤：仓库成员配置中取消所有合并人标记 → 保存。

预期：保存被拒绝并提示必须指定合并人，配置不变。

### 15. 已看过文件跨版本

步骤：审核人甲在当前版本勾选 3 个文件的“已看过” → 提交人推送新提交（其中 1 个文件被修改）→ 刷新。

预期：未变化的 2 个文件保持勾选；变化的 1 个文件自动取消；顶部“已看过”进度按当前版本重新计算。
