using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSentinel.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    actor_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "varchar(100)", nullable: false),
                    target_type = table.Column<string>(type: "varchar(50)", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    detail_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    author_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "varchar(1000)", nullable: false),
                    side = table.Column<string>(type: "varchar(10)", nullable: false),
                    line_number = table.Column<int>(type: "INTEGER", nullable: false),
                    anchor_text = table.Column<string>(type: "text", nullable: false),
                    blob_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    resolved_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    read_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repo_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repo_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    is_merger = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_members", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", nullable: false),
                    git_url = table.Column<string>(type: "varchar(500)", nullable: false),
                    default_base_branch = table.Column<string>(type: "varchar(200)", nullable: false),
                    credential_cipher = table.Column<string>(type: "text", nullable: true),
                    credential_kind = table.Column<string>(type: "TEXT", nullable: true),
                    sync_interval_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    last_fetched_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_file_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "varchar(1000)", nullable: false),
                    blob_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    viewed_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_file_views", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_reviewers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    state_revision_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_reviewers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    review_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    seq = table.Column<int>(type: "INTEGER", nullable: false),
                    base_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    head_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repo_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    base_branch = table.Column<string>(type: "varchar(200)", nullable: false),
                    head_branch = table.Column<string>(type: "varchar(200)", nullable: false),
                    submitter_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "varchar(200)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    current_revision_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    base_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    head_sha = table.Column<string>(type: "varchar(64)", nullable: false),
                    approved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    merged_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    closed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    merge_commit_sha = table.Column<string>(type: "varchar(64)", nullable: true),
                    merge_hint = table.Column<bool>(type: "INTEGER", nullable: false),
                    merge_hint_count = table.Column<int>(type: "INTEGER", nullable: false),
                    close_reason = table.Column<string>(type: "varchar(500)", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "varchar(64)", nullable: false),
                    display_name = table.Column<string>(type: "varchar(100)", nullable: false),
                    email = table.Column<string>(type: "varchar(256)", nullable: true),
                    password_hash = table.Column<string>(type: "varchar(256)", nullable: false),
                    is_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    must_change_password = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repo_members_repo_id_user_id",
                table: "repo_members",
                columns: new[] { "repo_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repos_name",
                table: "repos",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_file_views_review_id_user_id_file_path",
                table: "review_file_views",
                columns: new[] { "review_id", "user_id", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_reviewers_review_id_user_id",
                table: "review_reviewers",
                columns: new[] { "review_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_revisions_review_id_seq",
                table: "review_revisions",
                columns: new[] { "review_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_repo_id_head_branch",
                table: "reviews",
                columns: new[] { "repo_id", "head_branch" },
                unique: true,
                filter: "status IN ('Open','ChangesRequested','Approved')");

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "repo_members");

            migrationBuilder.DropTable(
                name: "repos");

            migrationBuilder.DropTable(
                name: "review_decisions");

            migrationBuilder.DropTable(
                name: "review_file_views");

            migrationBuilder.DropTable(
                name: "review_reviewers");

            migrationBuilder.DropTable(
                name: "review_revisions");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
