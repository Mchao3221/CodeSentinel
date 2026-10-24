using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSentinel.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_repo_members_user_id",
                table: "repo_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_id_created_at",
                table: "notifications",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_id_read_at",
                table: "notifications",
                columns: new[] { "user_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "IX_comments_review_id",
                table: "comments",
                column: "review_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_repo_members_user_id",
                table: "repo_members");

            migrationBuilder.DropIndex(
                name: "IX_notifications_user_id_created_at",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_user_id_read_at",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_comments_review_id",
                table: "comments");
        }
    }
}
