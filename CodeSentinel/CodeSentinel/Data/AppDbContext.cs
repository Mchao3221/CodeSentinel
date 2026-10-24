using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodeSentinel.Data;

/// <summary>
/// CodeSentinel 数据库上下文。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>
    /// 用户。
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>
    /// 仓库。
    /// </summary>
    public DbSet<Repo> Repos => Set<Repo>();

    /// <summary>
    /// 仓库成员。
    /// </summary>
    public DbSet<RepoMember> RepoMembers => Set<RepoMember>();

    /// <summary>
    /// 评审单。
    /// </summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>
    /// 评审版本。
    /// </summary>
    public DbSet<ReviewRevision> ReviewRevisions => Set<ReviewRevision>();

    /// <summary>
    /// 评审审核人。
    /// </summary>
    public DbSet<ReviewReviewer> ReviewReviewers => Set<ReviewReviewer>();

    /// <summary>
    /// 审核结论历史。
    /// </summary>
    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();

    /// <summary>
    /// 行级评论。
    /// </summary>
    public DbSet<Comment> Comments => Set<Comment>();

    /// <summary>
    /// 逐文件“已看过”标记。
    /// </summary>
    public DbSet<ReviewFileView> ReviewFileViews => Set<ReviewFileView>();

    /// <summary>
    /// 站内通知。
    /// </summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// 审计日志。
    /// </summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// 保存更改，并自动维护创建/更新时间。
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">保存成功后是否接受所有更改</param>
    /// <returns>受影响的行数</returns>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// 异步保存更改，并自动维护创建/更新时间。
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">保存成功后是否接受所有更改</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>受影响的行数</returns>
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 按实体状态写入 UTC 创建/更新时间。
    /// </summary>
    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    /// <summary>
    /// 配置表名、列名、枚举转换与索引。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUser(modelBuilder);
        ConfigureRepo(modelBuilder);
        ConfigureRepoMember(modelBuilder);
        ConfigureReview(modelBuilder);
        ConfigureReviewRevision(modelBuilder);
        ConfigureReviewReviewer(modelBuilder);
        ConfigureReviewDecision(modelBuilder);
        ConfigureComment(modelBuilder);
        ConfigureReviewFileView(modelBuilder);
        ConfigureNotification(modelBuilder);
        ConfigureAuditLog(modelBuilder);
        ConfigureUtcDateTimes(modelBuilder);
    }

    /// <summary>
    /// 配置用户表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.Username).HasColumnName("username");
            entity.Property(m => m.DisplayName).HasColumnName("display_name");
            entity.Property(m => m.Email).HasColumnName("email");
            entity.Property(m => m.PasswordHash).HasColumnName("password_hash");
            entity.Property(m => m.IsAdmin).HasColumnName("is_admin");
            entity.Property(m => m.IsActive).HasColumnName("is_active");
            entity.Property(m => m.MustChangePassword).HasColumnName("must_change_password");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => m.Username).IsUnique();
        });
    }

    /// <summary>
    /// 配置仓库表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureRepo(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Repo>(entity =>
        {
            entity.ToTable("repos");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.Name).HasColumnName("name");
            entity.Property(m => m.GitUrl).HasColumnName("git_url");
            entity.Property(m => m.DefaultBaseBranch).HasColumnName("default_base_branch");
            entity.Property(m => m.CredentialCipher).HasColumnName("credential_cipher");
            entity.Property(m => m.CredentialKind)
                .HasColumnName("credential_kind")
                .HasConversion<string>();
            entity.Property(m => m.SyncIntervalSeconds).HasColumnName("sync_interval_seconds");
            entity.Property(m => m.LastFetchedAt).HasColumnName("last_fetched_at");
            entity.Property(m => m.LastError).HasColumnName("last_error");
            entity.Property(m => m.FailureCount).HasColumnName("failure_count");
            entity.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(m => m.IsActive).HasColumnName("is_active");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => m.Name).IsUnique();
        });
    }

    /// <summary>
    /// 配置仓库成员表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureRepoMember(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RepoMember>(entity =>
        {
            entity.ToTable("repo_members");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.RepoId).HasColumnName("repo_id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.IsMerger).HasColumnName("is_merger");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => new { m.RepoId, m.UserId }).IsUnique();
            entity.HasIndex(m => m.UserId);
        });
    }

    /// <summary>
    /// 配置评审单表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureReview(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Review>(entity =>
        {
            entity.ToTable("reviews");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.RepoId).HasColumnName("repo_id");
            entity.Property(m => m.BaseBranch).HasColumnName("base_branch");
            entity.Property(m => m.HeadBranch).HasColumnName("head_branch");
            entity.Property(m => m.SubmitterId).HasColumnName("submitter_id");
            entity.Property(m => m.Title).HasColumnName("title");
            entity.Property(m => m.Description).HasColumnName("description");
            entity.Property(m => m.Status)
                .HasColumnName("status")
                .HasConversion<string>();
            entity.Property(m => m.CurrentRevisionId).HasColumnName("current_revision_id");
            entity.Property(m => m.BaseSha).HasColumnName("base_sha");
            entity.Property(m => m.HeadSha).HasColumnName("head_sha");
            entity.Property(m => m.ApprovedAt).HasColumnName("approved_at");
            entity.Property(m => m.MergedAt).HasColumnName("merged_at");
            entity.Property(m => m.ClosedAt).HasColumnName("closed_at");
            entity.Property(m => m.MergeCommitSha).HasColumnName("merge_commit_sha");
            entity.Property(m => m.MergeHint).HasColumnName("merge_hint");
            entity.Property(m => m.MergeHintCount).HasColumnName("merge_hint_count");
            entity.Property(m => m.CloseReason).HasColumnName("close_reason");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");

            // 同一仓库同一分支同时只允许一个活跃评审单
            entity.HasIndex(m => new { m.RepoId, m.HeadBranch })
                .IsUnique()
                .HasFilter("status IN ('Open','ChangesRequested','Approved')");
        });
    }

    /// <summary>
    /// 配置评审版本表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureReviewRevision(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewRevision>(entity =>
        {
            entity.ToTable("review_revisions");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.Seq).HasColumnName("seq");
            entity.Property(m => m.BaseSha).HasColumnName("base_sha");
            entity.Property(m => m.HeadSha).HasColumnName("head_sha");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => new { m.ReviewId, m.Seq }).IsUnique();
        });
    }

    /// <summary>
    /// 配置评审审核人表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureReviewReviewer(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewReviewer>(entity =>
        {
            entity.ToTable("review_reviewers");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.State)
                .HasColumnName("state")
                .HasConversion<string>();
            entity.Property(m => m.StateRevisionId).HasColumnName("state_revision_id");
            entity.Property(m => m.Note).HasColumnName("note");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => new { m.ReviewId, m.UserId }).IsUnique();
        });
    }

    /// <summary>
    /// 配置审核结论历史表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureReviewDecision(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewDecision>(entity =>
        {
            entity.ToTable("review_decisions");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.RevisionId).HasColumnName("revision_id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.State)
                .HasColumnName("state")
                .HasConversion<string>();
            entity.Property(m => m.Note).HasColumnName("note");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        });
    }

    /// <summary>
    /// 配置行级评论表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureComment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.ToTable("comments");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.RevisionId).HasColumnName("revision_id");
            entity.Property(m => m.AuthorId).HasColumnName("author_id");
            entity.Property(m => m.FilePath).HasColumnName("file_path");
            entity.Property(m => m.Side).HasColumnName("side");
            entity.Property(m => m.LineNumber).HasColumnName("line_number");
            entity.Property(m => m.AnchorText).HasColumnName("anchor_text");
            entity.Property(m => m.BlobSha).HasColumnName("blob_sha");
            entity.Property(m => m.Body).HasColumnName("body");
            entity.Property(m => m.ParentId).HasColumnName("parent_id");
            entity.Property(m => m.Status)
                .HasColumnName("status")
                .HasConversion<string>();
            entity.Property(m => m.ResolvedBy).HasColumnName("resolved_by");
            entity.Property(m => m.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => m.ReviewId);
        });
    }

    /// <summary>
    /// 配置逐文件“已看过”表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureReviewFileView(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewFileView>(entity =>
        {
            entity.ToTable("review_file_views");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.FilePath).HasColumnName("file_path");
            entity.Property(m => m.BlobSha).HasColumnName("blob_sha");
            entity.Property(m => m.ViewedAt).HasColumnName("viewed_at");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => new { m.ReviewId, m.UserId, m.FilePath }).IsUnique();
        });
    }

    /// <summary>
    /// 配置通知表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureNotification(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.Type)
                .HasColumnName("type")
                .HasConversion<string>();
            entity.Property(m => m.ReviewId).HasColumnName("review_id");
            entity.Property(m => m.PayloadJson).HasColumnName("payload_json");
            entity.Property(m => m.ReadAt).HasColumnName("read_at");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(m => new { m.UserId, m.CreatedAt });
            entity.HasIndex(m => new { m.UserId, m.ReadAt });
        });
    }

    /// <summary>
    /// 配置审计日志表。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureAuditLog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ActorId).HasColumnName("actor_id");
            entity.Property(m => m.Action).HasColumnName("action");
            entity.Property(m => m.TargetType).HasColumnName("target_type");
            entity.Property(m => m.TargetId).HasColumnName("target_id");
            entity.Property(m => m.DetailJson).HasColumnName("detail_json");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        });
    }

    /// <summary>
    /// 统一 DateTime 列的 UTC 存取转换：写入转 UTC，读回标记为 UTC。
    /// </summary>
    /// <param name="modelBuilder">模型构建器</param>
    private static void ConfigureUtcDateTimes(ModelBuilder modelBuilder)
    {
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            value => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            value => value.HasValue
                ? (value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime())
                : value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .Property(property.Name)
                        .HasConversion(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .Property(property.Name)
                        .HasConversion(nullableUtcConverter);
                }
            }
        }
    }
}
