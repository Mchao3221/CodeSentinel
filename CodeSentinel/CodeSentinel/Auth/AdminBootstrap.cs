using System.Security.Cryptography;
using System.Text;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Auth;

/// <summary>
/// 首次启动的管理员初始化：生成一次性口令，并提供创建第一个管理员的匿名接口。
/// </summary>
public static class AdminBootstrap
{
    /// <summary>
    /// 一次性初始化口令文件名。
    /// </summary>
    public const string TokenFileName = "init-admin-token.txt";

    private const int TokenByteLength = 32;

    /// <summary>
    /// 启动时若无管理员则生成一次性初始化口令并写入文件。
    /// </summary>
    /// <param name="services">根服务提供者</param>
    /// <param name="environment">宿主环境</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public static async Task EnsureAsync(
        IServiceProvider services,
        IHostEnvironment environment,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        if (await db.Users.AnyAsync(m => m.IsAdmin, ct))
        {
            return;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteLength));
        var tokenPath = GetTokenPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        await File.WriteAllTextAsync(tokenPath, token, ct);
        RestrictFilePermissions(tokenPath);

        // 初始化口令必须让运维在控制台看到，但不能进入结构化日志文件
        Console.WriteLine("未检测到管理员账号，已生成一次性初始化口令：");
        Console.WriteLine(token);
        Console.WriteLine("请调用 POST /api/setup/admin 使用该口令创建第一个管理员，成功后口令文件会自动删除。");
    }

    /// <summary>
    /// 映射初始化管理员接口（匿名、免 CSRF）。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapAdminSetupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/setup/admin", CreateFirstAdminAsync);
    }

    /// <summary>
    /// 取一次性初始化口令文件路径。
    /// </summary>
    /// <param name="environment">宿主环境</param>
    /// <returns>文件路径</returns>
    private static string GetTokenPath(IHostEnvironment environment)
    {
        return Path.Combine(environment.ContentRootPath, "data", TokenFileName);
    }

    /// <summary>
    /// 使用一次性口令创建第一个管理员。
    /// </summary>
    /// <param name="request">初始化请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="authService">密码服务</param>
    /// <param name="environment">宿主环境</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的管理员</returns>
    private static async Task<IResult> CreateFirstAdminAsync(
        SetupAdminRequest request,
        AppDbContext db,
        PasswordAuthService authService,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (await db.Users.AnyAsync(m => m.IsAdmin, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "ALREADY_INITIALIZED", "系统已初始化");
        }

        await VerifyTokenAsync(environment, request.Token, ct);

        var username = request.Username?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;

        if (username.Length is 0 or > 64 || displayName.Length is 0 or > 100)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_USER",
                "用户名或显示名不能为空且长度不超限");
        }

        PasswordAuthService.EnsureValidPassword(request.Password);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            IsAdmin = true,
            IsActive = true,
            MustChangePassword = false
        };
        user.PasswordHash = authService.HashPassword(user, request.Password!);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        DeleteTokenFile(environment);

        return Results.Created($"/api/users/{user.Id}", new
        {
            id = user.Id,
            username = user.Username,
            displayName = user.DisplayName,
            isAdmin = user.IsAdmin,
            mustChangePassword = user.MustChangePassword
        });
    }

    /// <summary>
    /// 校验请求携带的一次性口令与文件内容是否一致。
    /// </summary>
    /// <param name="environment">宿主环境</param>
    /// <param name="token">请求口令</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private static async Task VerifyTokenAsync(
        IHostEnvironment environment,
        string? token,
        CancellationToken ct)
    {
        var tokenPath = GetTokenPath(environment);

        if (string.IsNullOrWhiteSpace(token) || !File.Exists(tokenPath))
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "INVALID_SETUP_TOKEN",
                "初始化口令无效");
        }

        var expected = Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(tokenPath, ct)).Trim());
        var actual = Encoding.UTF8.GetBytes(token.Trim());

        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "INVALID_SETUP_TOKEN",
                "初始化口令无效");
        }
    }

    /// <summary>
    /// 初始化成功后删除口令文件，失败时忽略（管理员已存在后该口令不再生效）。
    /// </summary>
    /// <param name="environment">宿主环境</param>
    private static void DeleteTokenFile(IHostEnvironment environment)
    {
        try
        {
            File.Delete(GetTokenPath(environment));
        }
        catch (IOException)
        {
            // 文件被占用时保留，管理员已创建后该口令不会再被接受
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足时保留，管理员已创建后该口令不会再被接受
        }
    }

    /// <summary>
    /// 在类 Unix 系统上把口令文件权限收紧为仅当前用户可读写。
    /// </summary>
    /// <param name="path">文件路径</param>
    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>
/// 初始化管理员请求。
/// </summary>
/// <param name="Token">一次性初始化口令</param>
/// <param name="Username">登录名</param>
/// <param name="Password">密码</param>
/// <param name="DisplayName">显示名</param>
public sealed record SetupAdminRequest(
    string? Token,
    string? Username,
    string? Password,
    string? DisplayName);
