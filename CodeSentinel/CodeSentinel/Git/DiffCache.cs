using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace CodeSentinel.Git;

/// <summary>
/// diff 结果缓存：按 {repoId}/{baseSha}..{headSha} 目录存放文件列表索引与每个文件的 diff。
/// </summary>
public sealed class DiffCache
{
    private const string CacheDirectoryName = "cache";
    private const string IndexFileName = "index.json";
    private const string DefaultStorageRoot = "data";

    private readonly string _root;
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// 按应用配置创建缓存，Storage:Root 相对内容根目录解析。
    /// </summary>
    /// <param name="configuration">应用配置</param>
    /// <param name="environment">宿主环境</param>
    public DiffCache(IConfiguration configuration, IHostEnvironment environment)
        : this(ResolveStorageRoot(configuration, environment.ContentRootPath))
    {
    }

    /// <summary>
    /// 使用已解析的存储根目录创建缓存。
    /// </summary>
    /// <param name="storageRoot">存储根目录</param>
    public DiffCache(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _root = Path.Combine(Path.GetFullPath(storageRoot), CacheDirectoryName);
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// 读取文件列表缓存，未命中返回 null。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件摘要列表或 null</returns>
    public async Task<IReadOnlyList<DiffFileSummary>?> TryGetIndexAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        CancellationToken ct)
    {
        return await TryReadAsync<IReadOnlyList<DiffFileSummary>>(
            IndexPath(repoId, baseSha, headSha), ct);
    }

    /// <summary>
    /// 写入文件列表缓存。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="files">文件摘要列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task WriteIndexAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        IReadOnlyList<DiffFileSummary> files,
        CancellationToken ct)
    {
        await WriteAsync(IndexPath(repoId, baseSha, headSha), files, ct);
    }

    /// <summary>
    /// 读取单文件 diff 缓存，未命中返回 null。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件 diff 或 null</returns>
    public async Task<DiffFile?> TryGetFileAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        string path,
        CancellationToken ct)
    {
        return await TryReadAsync<DiffFile>(FilePath(repoId, baseSha, headSha, path), ct);
    }

    /// <summary>
    /// 写入单文件 diff 缓存。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <param name="file">文件 diff</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task WriteFileAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        string path,
        DiffFile file,
        CancellationToken ct)
    {
        await WriteAsync(FilePath(repoId, baseSha, headSha, path), file, ct);
    }

    /// <summary>
    /// 计算某个版本对应的缓存目录。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <returns>目录绝对路径</returns>
    private string VersionDirectory(Guid repoId, string baseSha, string headSha)
    {
        return Path.Combine(_root, repoId.ToString(), $"{baseSha}..{headSha}");
    }

    /// <summary>
    /// 文件列表索引文件路径。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <returns>文件路径</returns>
    private string IndexPath(Guid repoId, string baseSha, string headSha)
    {
        return Path.Combine(VersionDirectory(repoId, baseSha, headSha), IndexFileName);
    }

    /// <summary>
    /// 单个文件 diff 的缓存文件路径，文件名用路径的 SHA-256 十六进制。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <returns>文件路径</returns>
    private string FilePath(Guid repoId, string baseSha, string headSha, string path)
    {
        return Path.Combine(
            VersionDirectory(repoId, baseSha, headSha),
            $"{Sha256Hex(path)}.json");
    }

    /// <summary>
    /// 计算路径的 SHA-256 十六进制（小写）。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>十六进制摘要</returns>
    private static string Sha256Hex(string path)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// 读取并反序列化缓存文件，不存在或反序列化失败时返回默认值。
    /// </summary>
    /// <typeparam name="T">缓存对象类型</typeparam>
    /// <param name="path">缓存文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>缓存对象或默认值</returns>
    private async Task<T?> TryReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// 先写临时文件再原子移动，保证缓存写入要么完整要么不存在。
    /// </summary>
    /// <typeparam name="T">缓存对象类型</typeparam>
    /// <param name="path">目标文件路径</param>
    /// <param name="value">待写入对象</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, _options, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// 解析存储根目录，相对路径以内容根目录为基准。
    /// </summary>
    /// <param name="configuration">应用配置</param>
    /// <param name="contentRootPath">内容根目录</param>
    /// <returns>绝对路径</returns>
    private static string ResolveStorageRoot(IConfiguration configuration, string contentRootPath)
    {
        var root = configuration.GetValue("Storage:Root", DefaultStorageRoot) ?? DefaultStorageRoot;
        return Path.GetFullPath(root, contentRootPath);
    }
}
