using CodeSentinel.Common;
using CodeSentinel.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

const string DefaultConnectionString = "Data Source=data/codesentinel.db";

var builder = WebApplication.CreateBuilder(args);

// 启动时确保 SQLite 数据库所在目录存在
EnsureDatabaseDirectory(builder);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? DefaultConnectionString));

// 全局枚举序列化为 SNAKE_CASE_UPPER
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));

var app = builder.Build();

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

app.MapGet("/api/health", () => new { status = "ok" });

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

/// <summary>
/// 供集成测试引用的程序入口类型。
/// </summary>
public partial class Program;
