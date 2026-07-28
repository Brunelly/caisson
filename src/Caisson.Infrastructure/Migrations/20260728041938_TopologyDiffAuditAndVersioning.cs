using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations;

/// <inheritdoc />
public partial class TopologyDiffAuditAndVersioning : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "completed_at_utc",
            table: "topology_snapshot",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "started_at_utc",
            table: "topology_snapshot",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "trigger_type",
            table: "topology_snapshot",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "version",
            table: "topology_snapshot",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "topology_audit_event",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                rack_id = table.Column<Guid>(type: "uuid", nullable: true),
                snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                result = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                details_json = table.Column<string>(type: "jsonb", maxLength: 8192, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_topology_audit_event", x => x.id);
                table.ForeignKey(
                    name: "fk_topology_audit_event_rack_rack_id",
                    column: x => x.rack_id,
                    principalTable: "rack",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_topology_audit_event_snapshots_snapshot_id",
                    column: x => x.snapshot_id,
                    principalTable: "topology_snapshot",
                    principalColumn: "id");
            });

        migrationBuilder.CreateTable(
            name: "topology_entity_diff",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                previous_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                entity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                entity_stable_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                change_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                diff_payload_json = table.Column<string>(type: "jsonb", maxLength: 8192, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_topology_entity_diff", x => x.id);
                table.ForeignKey(
                    name: "fk_topology_entity_diff_rack_rack_id",
                    column: x => x.rack_id,
                    principalTable: "rack",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_topology_entity_diff_snapshots_previous_snapshot_id",
                    column: x => x.previous_snapshot_id,
                    principalTable: "topology_snapshot",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_topology_entity_diff_snapshots_snapshot_id",
                    column: x => x.snapshot_id,
                    principalTable: "topology_snapshot",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_topology_snapshot_rack_id_completed_at",
            table: "topology_snapshot",
            columns: new[] { "rack_id", "completed_at_utc" },
            descending: new[] { false, true });

        // Backfill a per-rack monotonic version for any snapshots that predate this column (story-2 rows
        // all default to 0). Without this, a rack with ≥2 pre-existing snapshots would collide at
        // version 0 and the unique (rack_id, version) index below would fail to build. Numbered 1-based
        // ordered by (created_at, id) to match both the deterministic "latest snapshot" ordering and the
        // 1-based version the ingestion service assigns going forward (maxVersion + 1). A no-op on a
        // greenfield M0 database (no pre-existing snapshots).
        migrationBuilder.Sql(
            """
            WITH ordered AS (
                SELECT id,
                       ROW_NUMBER() OVER (PARTITION BY rack_id ORDER BY created_at_utc, id) AS seq
                FROM topology_snapshot
            )
            UPDATE topology_snapshot AS s
            SET version = ordered.seq
            FROM ordered
            WHERE s.id = ordered.id;
            """);

        migrationBuilder.CreateIndex(
            name: "ux_topology_snapshot_rack_id_version",
            table: "topology_snapshot",
            columns: new[] { "rack_id", "version" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_topology_audit_event_correlation_id",
            table: "topology_audit_event",
            column: "correlation_id");

        migrationBuilder.CreateIndex(
            name: "ix_topology_audit_event_rack_id_occurred_at",
            table: "topology_audit_event",
            columns: new[] { "rack_id", "occurred_at_utc" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_topology_audit_event_snapshot_id",
            table: "topology_audit_event",
            column: "snapshot_id");

        migrationBuilder.CreateIndex(
            name: "ix_topology_entity_diff_previous_snapshot_id",
            table: "topology_entity_diff",
            column: "previous_snapshot_id");

        migrationBuilder.CreateIndex(
            name: "ix_topology_entity_diff_rack_id_entity_type_entity_stable_key",
            table: "topology_entity_diff",
            columns: new[] { "rack_id", "entity_type", "entity_stable_key" });

        migrationBuilder.CreateIndex(
            name: "ix_topology_entity_diff_rack_id_snapshot_id",
            table: "topology_entity_diff",
            columns: new[] { "rack_id", "snapshot_id" });

        migrationBuilder.CreateIndex(
            name: "ux_topology_entity_diff_snapshot_entity_key",
            table: "topology_entity_diff",
            columns: new[] { "snapshot_id", "entity_type", "entity_stable_key" },
            unique: true);

        // NFR4 database-level tamper-evidence: audit events are append-only. A BEFORE UPDATE OR
        // DELETE trigger rejects any mutation of a persisted audit row, so tamper-evidence holds
        // even against raw SQL that bypasses the EF append-only guard.
        migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION caisson_reject_audit_mutation()
    RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'topology_audit_event is append-only: % is not permitted (NFR4).', TG_OP
        USING ERRCODE = 'raise_exception';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_topology_audit_event_append_only
    BEFORE UPDATE OR DELETE ON topology_audit_event
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_audit_mutation();
");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS trg_topology_audit_event_append_only ON topology_audit_event;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS caisson_reject_audit_mutation();");

        migrationBuilder.DropTable(
            name: "topology_audit_event");

        migrationBuilder.DropTable(
            name: "topology_entity_diff");

        migrationBuilder.DropIndex(
            name: "ix_topology_snapshot_rack_id_completed_at",
            table: "topology_snapshot");

        migrationBuilder.DropIndex(
            name: "ux_topology_snapshot_rack_id_version",
            table: "topology_snapshot");

        migrationBuilder.DropColumn(
            name: "completed_at_utc",
            table: "topology_snapshot");

        migrationBuilder.DropColumn(
            name: "started_at_utc",
            table: "topology_snapshot");

        migrationBuilder.DropColumn(
            name: "trigger_type",
            table: "topology_snapshot");

        migrationBuilder.DropColumn(
            name: "version",
            table: "topology_snapshot");
    }
}
