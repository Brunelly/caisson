using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriftApplyPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drift_apply_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    drift_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    claimed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    claimed_by_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_heartbeat_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expected_drift_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_before_vlan = table.Column<int>(type: "integer", nullable: true),
                    expected_after_vlan = table.Column<int>(type: "integer", nullable: false),
                    switch_device_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    port_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    desired_vlan_id = table.Column<int>(type: "integer", nullable: true),
                    device_reason_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    device_confirmed = table.Column<bool>(type: "boolean", nullable: true),
                    before_state_json = table.Column<string>(type: "jsonb", maxLength: 4096, nullable: true),
                    after_state_json = table.Column<string>(type: "jsonb", maxLength: 4096, nullable: true),
                    error_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    error_details_json = table.Column<string>(type: "jsonb", maxLength: 2048, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drift_apply_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_drift_apply_job_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "drift_apply_job_step",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    result_summary_json = table.Column<string>(type: "jsonb", maxLength: 4096, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drift_apply_job_step", x => x.id);
                    table.ForeignKey(
                        name: "fk_drift_apply_job_step_drift_apply_job_job_id",
                        column: x => x.job_id,
                        principalTable: "drift_apply_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_drift_apply_job_rack_id_requested_at",
                table: "drift_apply_job",
                columns: new[] { "rack_id", "requested_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_drift_apply_job_drift_item_active",
                table: "drift_apply_job",
                columns: new[] { "rack_id", "drift_item_id" },
                unique: true,
                filter: "status IN ('Pending','Claimed','Revalidating','Executing')");

            migrationBuilder.CreateIndex(
                name: "ux_drift_apply_job_step_job_id_step_name",
                table: "drift_apply_job_step",
                columns: new[] { "job_id", "step_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drift_apply_job_step");

            migrationBuilder.DropTable(
                name: "drift_apply_job");
        }
    }
}
