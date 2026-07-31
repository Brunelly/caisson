using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGitPullRequestStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "git_pull_request_status",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pull_request_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repo_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    pull_request_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    checks_conclusion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failing_checks_count = table.Column<int>(type: "integer", nullable: true),
                    checks_summary = table.Column<string>(type: "jsonb", maxLength: 16384, nullable: true),
                    last_checked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    next_poll_after_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consecutive_poll_failures = table.Column<int>(type: "integer", nullable: false),
                    last_poll_failure_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_pull_request_status", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_pull_request_status_git_pull_request_link_pull_request_",
                        column: x => x.pull_request_link_id,
                        principalTable: "git_pull_request_link",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_git_pull_request_status_lease",
                table: "git_pull_request_status",
                columns: new[] { "next_poll_after_utc", "last_checked_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_git_pull_request_status_rack",
                table: "git_pull_request_status",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ux_git_pull_request_status_link",
                table: "git_pull_request_status",
                column: "pull_request_link_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "git_pull_request_status");
        }
    }
}
