using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriftPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drift_report",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    computed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    computation_version = table.Column<int>(type: "integer", nullable: false),
                    total_items = table.Column<int>(type: "integer", nullable: false),
                    counts_by_severity_json = table.Column<string>(type: "jsonb", maxLength: 2048, nullable: false),
                    has_ambiguities = table.Column<bool>(type: "boolean", nullable: false),
                    is_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drift_report", x => x.id);
                    table.ForeignKey(
                        name: "fk_drift_report_desired_state_version_desired_revision_id",
                        column: x => x.desired_revision_id,
                        principalTable: "desired_state_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_drift_report_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_drift_report_snapshots_observed_snapshot_id",
                        column: x => x.observed_snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "drift_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    drift_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    drift_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    drift_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actionable = table.Column<bool>(type: "boolean", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expected_value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actual_value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    why = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", maxLength: 8192, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drift_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_drift_item_drift_reports_drift_report_id",
                        column: x => x.drift_report_id,
                        principalTable: "drift_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_drift_item_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_drift_item_rack_id_drift_item_id",
                table: "drift_item",
                columns: new[] { "rack_id", "drift_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_drift_item_report_id_created_at",
                table: "drift_item",
                columns: new[] { "drift_report_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_drift_item_report_id_drift_item_id",
                table: "drift_item",
                columns: new[] { "drift_report_id", "drift_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_drift_report_desired_revision_id",
                table: "drift_report",
                column: "desired_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_drift_report_observed_snapshot_id",
                table: "drift_report",
                column: "observed_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ix_drift_report_rack_id_computed_at",
                table: "drift_report",
                columns: new[] { "rack_id", "computed_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_drift_report_rack_desired_observed",
                table: "drift_report",
                columns: new[] { "rack_id", "desired_revision_id", "observed_snapshot_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drift_item");

            migrationBuilder.DropTable(
                name: "drift_report");
        }
    }
}
