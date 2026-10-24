using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSentinel.Migrations
{
    /// <inheritdoc />
    public partial class AddRepoSyncBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failure_count",
                table: "repos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "repos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_count",
                table: "repos");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "repos");
        }
    }
}
