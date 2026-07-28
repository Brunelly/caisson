using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discovery_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    triggered_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    last_heartbeat_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    result_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancellation_requested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovery_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_discovery_job_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discovery_job_snapshots_result_snapshot_id",
                        column: x => x.result_snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "rack_discovery_schedule",
                columns: table => new
                {
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    jitter_seconds = table.Column<int>(type: "integer", nullable: false),
                    next_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_success_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rack_discovery_schedule", x => x.rack_id);
                    table.ForeignKey(
                        name: "fk_rack_discovery_schedule_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovery_job_step",
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
                    result_summary_json = table.Column<string>(type: "jsonb", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovery_job_step", x => x.id);
                    table.ForeignKey(
                        name: "fk_discovery_job_step_discovery_job_job_id",
                        column: x => x.job_id,
                        principalTable: "discovery_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discovery_job_rack_id_created_at",
                table: "discovery_job",
                columns: new[] { "rack_id", "created_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_discovery_job_result_snapshot_id",
                table: "discovery_job",
                column: "result_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_job_rack_active",
                table: "discovery_job",
                column: "rack_id",
                unique: true,
                filter: "status IN ('Queued','InProgress')");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_job_rack_idempotency_key",
                table: "discovery_job",
                columns: new[] { "rack_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_job_step_job_id_step_name",
                table: "discovery_job_step",
                columns: new[] { "job_id", "step_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rack_discovery_schedule_enabled_next_run",
                table: "rack_discovery_schedule",
                columns: new[] { "enabled", "next_run_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_job_step");

            migrationBuilder.DropTable(
                name: "rack_discovery_schedule");

            migrationBuilder.DropTable(
                name: "discovery_job");
        }
    }
}
