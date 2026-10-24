# CodeSentinel

CodeSentinel 是面向公司 Gitblit 的内部代码评审系统：提交人显式发起评审、挑选审核人，审核人在网页上查看完整 diff、按行评论并给出结论；合并仍在 Gitblit 手工完成，系统通过镜像同步检测合并结果。系统不接管仓库、不安装 hook，只使用只读凭据。

## 技术栈

- 后端：.NET 10 / ASP.NET Core Minimal API、EF Core、SQLite、Cookie 认证 + Antiforgery。
- 前端：Vue 3 + TypeScript + Vite、Element Plus、Pinia、Vue Router。
- 测试：xUnit（后端）、Vitest（前端单元）、Playwright（UI 冒烟）。

## 仓库结构

```
CodeSentinel/                 后端解决方案与源码
  CodeSentinel/               ASP.NET Core 应用
  CodeSentinel.Tests/         xUnit 测试
code-sentinel-web/            前端工程
  src/                        页面、组件、接口与状态
  e2e/                        Playwright 冒烟测试（接口打桩）
docs/
  deployment.md               部署、备份与验收清单
  superpowers/specs/          设计文档
  superpowers/plans/          实施计划
```

## 本地开发

前置：.NET 10 SDK、Node.js 24、git ≥ 2.30。

```bash
# 后端：默认监听 http://localhost:5214
dotnet run --project CodeSentinel/CodeSentinel

# 前端：Vite 开发服务器，/api 代理到 5214
npm --prefix code-sentinel-web run dev
```

首次启动时后端会执行数据库迁移；如果系统里还没有管理员，会在控制台输出一次性初始化口令，并写入 `CodeSentinel/CodeSentinel/data/init-admin-token.txt`。使用该口令创建第一个管理员：

```bash
curl -X POST http://localhost:5214/api/setup/admin \
  -H "Content-Type: application/json" \
  -d '{"token":"<控制台输出的口令>","username":"admin","displayName":"管理员","password":"至少8位密码"}'
```

初始化成功后口令文件自动删除。系统**不内置任何默认管理员账号**，开发、测试与生产环境都走同一套初始化流程；开发环境中管理员创建用户、仓库后即可开始评审。

## 配置项

后端配置位于 `CodeSentinel/CodeSentinel/appsettings*.json`，也可用环境变量覆盖（层级用 `__` 分隔）。

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=data/codesentinel.db` | SQLite 数据库，相对路径以内容根目录为基准 |
| `Storage:Root` | `data` | 镜像、缓存与 Data Protection 密钥的根目录 |
| `Sync:PollSeconds` | `60` | 后台同步任务的轮询间隔（秒） |
| `Auth:SessionHours` | `8` | 登录会话有效期（小时） |
| `Git:ExecutablePath` | `git` | git 可执行文件路径，非 PATH 安装时必填 |

数据库路径与 `Storage:Root` 是两套配置：把 `Storage:Root` 迁到其他磁盘时，记得同步调整连接字符串。

## 测试

```bash
# 后端
dotnet test CodeSentinel/CodeSentinel.Tests/CodeSentinel.Tests.csproj

# 前端单元测试
npm --prefix code-sentinel-web run test:unit -- --run

# UI 冒烟（首次需下载 Chromium；接口全部打桩，不需要后端、数据库或 git）
npm --prefix code-sentinel-web run e2e:install
npm --prefix code-sentinel-web run test:e2e

# 前端生产构建
npm --prefix code-sentinel-web run build
```

## 相关文档

- 部署、备份、反向代理与 15 条手工验收场景：[docs/deployment.md](docs/deployment.md)
- 设计文档：[docs/superpowers/specs/2026-10-22-code-sentinel-design.md](docs/superpowers/specs/2026-10-22-code-sentinel-design.md)
- 实施计划：[docs/superpowers/plans/2026-10-23-code-sentinel-implementation.md](docs/superpowers/plans/2026-10-23-code-sentinel-implementation.md)
