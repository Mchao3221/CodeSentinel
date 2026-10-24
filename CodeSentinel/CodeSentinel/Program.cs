using CodeSentinel.Audit;
using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using CodeSentinel.Repos;
using CodeSentinel.Reviews;
using CodeSentinel.Sync;
using CodeSentinel.Users;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

const string DefaultConnectionString = "Data Source=data/codesentinel.db";
const int DefaultSessionHours = 8;
const string DefaultStorageRoot = "data";
const string DataProtectionApplicationName = "CodeSentinel";

var builder = WebApplication.CreateBuilder(args);

// 启动时确保 SQLite 数据库所在目录存在
EnsureDatabaseDirectory(builder);

var sessionHours = builder.Configuration.GetValue("Auth:SessionHours", DefaultSessionHours);
if (sessionHours <= 0)
{
    sessionHours = DefaultSessionHours;
}

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? DefaultConnectionString));

// Data Protection：密钥持久化到 Storage:Root/keys，保证重启后仍能解密仓库凭据
ConfigureDataProtection(builder);

builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<GitCli>();
builder.Services.AddSingleton<MirrorService>();
builder.Services.AddSingleton<DiffCache>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<SyncThrottle>();
builder.Services.AddSingleton<MirrorSyncWorker>();

// 镜像同步配置：Sync:PollSeconds 默认 60 秒
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));

// 测试环境不启动后台轮询，避免与集成测试争用数据库与镜像目录
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService(
        services => services.GetRequiredService<MirrorSyncWorker>());
}

// 全局枚举序列化为 SNAKE_CASE_UPPER
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));

// CSRF：前端从 GET /api/auth/csrf 取令牌，放在 X-CSRF-TOKEN 请求头
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "CodeSentinel.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CodeSentinel.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(sessionHours);
        options.SlidingExpiration = false;

        // 接口不跳转登录页，统一返回错误形状
        options.Events.OnRedirectToLogin = context => WriteAuthErrorAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "UNAUTHENTICATED",
            "未登录");
        options.Events.OnRedirectToAccessDenied = context => WriteAuthErrorAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "FORBIDDEN",
            "无权访问");
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(CurrentUser.AdminRole, policy => policy.RequireRole(CurrentUser.AdminRole));
});

builder.Services.AddScoped<PasswordAuthService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<ReviewReviewerService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<AnchorService>();
builder.Services.AddScoped<RevisionService>();
builder.Services.AddScoped<MergeDetectionService>();
builder.Services.AddScoped<CsrfEndpointFilter>();

var app = builder.Build();

// 启动自检：git 不可用时非测试环境直接失败，测试环境只记录警告
await EnsureGitAvailableAsync(app);

// 非测试环境：执行迁移并在无管理员时生成一次性初始化口令
if (!app.Environment.IsEnvironment("Testing"))
{
    await AdminBootstrap.EnsureAsync(app.Services, app.Environment);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 异常与错误响应统一形状
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    var (code, message) = context.Response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => ("BAD_REQUEST", "请求参数有误"),
        StatusCodes.Status401Unauthorized => ("UNAUTHENTICATED", "未登录"),
        StatusCodes.Status403Forbidden => ("FORBIDDEN", "无权访问"),
        StatusCodes.Status404NotFound => ("NOT_FOUND", "资源不存在"),
        StatusCodes.Status405MethodNotAllowed => ("METHOD_NOT_ALLOWED", "请求方法不允许"),
        _ => ("HTTP_ERROR", "请求失败")
    };

    await context.Response.WriteAsJsonAsync(new { code, message, traceId = context.TraceIdentifier });
});

app.UseAuthentication();

// 待改密用户只能访问改密相关接口
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

app.UseAuthorization();

// 全部业务端点挂在 /api 下，统一应用 CSRF 端点过滤器
var api = app.MapGroup("/api");
api.AddEndpointFilter<CsrfEndpointFilter>();
api.MapGet("/health", () => new { status = "ok" });
api.MapAuthEndpoints(sessionHours);
api.MapAdminSetupEndpoint();
api.MapUsersEndpoints();
api.MapReposEndpoints();
api.MapBranchEndpoints();
api.MapReviewsEndpoints();
api.MapCommentsEndpoints();
api.MapDiffEndpoints();
api.MapViewedEndpoints();
api.MapSyncEndpoints();
api.MapNotificationsEndpoints();

app.Run();

// 确保 SQLite 数据库文件所在目录存在
static void EnsureDatabaseDirectory(WebApplicationBuilder builder)
{
    var connectionString = builder.Configuration.GetConnectionString("Default") ?? DefaultConnectionString;
    var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;

    // 内存数据库没有文件目录
    if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var fullPath = Path.GetFullPath(dataSource, builder.Environment.ContentRootPath);
    var directory = Path.GetDirectoryName(fullPath);

    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

// 用统一错误形状写出认证/授权失败响应
static async Task WriteAuthErrorAsync(HttpContext context, int status, string code, string message)
{
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { code, message, traceId = context.TraceIdentifier });
}

// 配置 Data Protection：固定应用名与密钥目录，确保凭据密文跨重启可解
static void ConfigureDataProtection(WebApplicationBuilder builder)
{
    builder.Services
        .AddDataProtection()
        .SetApplicationName(DataProtectionApplicationName);

    // 延迟到宿主构建完成后解析 Storage:Root，测试可覆盖到临时目录
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
        {
            var storageRoot = configuration.GetValue("Storage:Root", DefaultStorageRoot) ?? DefaultStorageRoot;
            var storagePath = Path.GetFullPath(storageRoot, environment.ContentRootPath);
            var keyPath = Path.Combine(storagePath, "keys");
            Directory.CreateDirectory(keyPath);
            options.XmlRepository = new FileSystemXmlRepository(
                new DirectoryInfo(keyPath),
                NullLoggerFactory.Instance);
        });
}

// 校验 git 命令可用：非测试环境失败即退出，测试环境只记录警告
static async Task EnsureGitAvailableAsync(WebApplication app)
{
    var git = app.Services.GetRequiredService<GitCli>();
    if (await git.VerifyAvailableAsync(CancellationToken.None))
    {
        return;
    }

    const string message = "未找到可用的 git 命令，请检查 Git:ExecutablePath 配置后重启。";

    if (app.Environment.IsEnvironment("Testing"))
    {
        app.Logger.LogWarning(message);
        return;
    }

    app.Logger.LogCritical(message);
    throw new InvalidOperationException(message);
}

/// <summary>
/// 供集成测试引用的程序入口类型。
/// </summary>
public partial class Program;
