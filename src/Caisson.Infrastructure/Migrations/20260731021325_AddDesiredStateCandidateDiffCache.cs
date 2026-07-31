using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredStateCandidateDiffCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "desired_state_candidate_diff_cache",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    baseline_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    raw_unified_diff = table.Column<string>(type: "text", maxLength: 4194304, nullable: false),
                    structured_summary_json = table.Column<string>(type: "jsonb", maxLength: 2097152, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_state_candidate_diff_cache", x => x.id);
                    table.ForeignKey(
                        name: "fk_desired_state_candidate_diff_cache_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_candidate_diff_cache_rack_expires",
                table: "desired_state_candidate_diff_cache",
                columns: new[] { "rack_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_desired_state_candidate_diff_cache_rack_baseline_candidate",
                table: "desired_state_candidate_diff_cache",
                columns: new[] { "rack_id", "baseline_revision_id", "candidate_sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "desired_state_candidate_diff_cache");
        }
    }
}
