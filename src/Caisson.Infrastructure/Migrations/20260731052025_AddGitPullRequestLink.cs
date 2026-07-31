using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGitPullRequestLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "git_pull_request_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repo_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    branch_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: true),
                    pull_request_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    candidate_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_checked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_pull_request_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_pull_request_link_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_git_pull_request_link_rack_created",
                table: "git_pull_request_link",
                columns: new[] { "rack_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_git_pull_request_link_rack_fingerprint_open",
                table: "git_pull_request_link",
                columns: new[] { "rack_id", "candidate_fingerprint" },
                unique: true,
                filter: "status = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "git_pull_request_link");
        }
    }
}
