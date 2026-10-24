using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;

namespace CodeSentinel.Audit;

/// <summary>
/// 审计日志写入服务。
/// </summary>
/// <param name="db">数据库上下文</param>
public sealed class AuditService(AppDbContext db)
{
    private static readonly JsonSerializerOptions detailJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 写入一条审计日志。
    /// </summary>
    /// <param name="actorId">操作人，系统操作时可为空</param>
    /// <param name="action">动作标识</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="targetId">目标主键</param>
    /// <param name="detail">动作明细，可为空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task RecordAsync(
        Guid? actorId,
        string action,
        string targetType,
        Guid? targetId,
        object? detail,
        CancellationToken ct)
    {
        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail, detailJsonOptions)
        };

        db.AuditLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }
}
