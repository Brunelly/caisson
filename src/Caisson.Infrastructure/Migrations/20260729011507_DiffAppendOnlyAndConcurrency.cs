using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DiffAppendOnlyAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_topology_entity_diff_snapshots_snapshot_id",
                table: "topology_entity_diff");

            migrationBuilder.AddColumn<string>(
                name: "external_device_key",
                table: "switch",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "external_device_key",
                table: "server",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "discovery_job_step",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "discovery_job",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddForeignKey(
                name: "fk_topology_entity_diff_snapshots_snapshot_id",
                table: "topology_entity_diff",
                column: "snapshot_id",
                principalTable: "topology_snapshot",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Finding #6: generalize the append-only guard (TG_TABLE_NAME instead of a hardcoded table
            // name) so one function backs both append-only tables, then add the trigger
            // topology_entity_diff never had — it previously relied only on the FK (now Restrict, above)
            // and the EF-level IAppendOnly guard, neither of which stops raw SQL. Statement-level BEFORE
            // TRUNCATE triggers close the remaining gap: a row-level trigger never fires for TRUNCATE.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION caisson_reject_append_only_mutation()
    RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: % is not permitted (NFR4).', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'raise_exception';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_topology_audit_event_append_only ON topology_audit_event;
CREATE TRIGGER trg_topology_audit_event_append_only
    BEFORE UPDATE OR DELETE ON topology_audit_event
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();

CREATE TRIGGER trg_topology_entity_diff_append_only
    BEFORE UPDATE OR DELETE ON topology_entity_diff
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();

CREATE OR REPLACE FUNCTION caisson_reject_append_only_truncate()
    RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: TRUNCATE is not permitted (NFR4).', TG_TABLE_NAME
        USING ERRCODE = 'raise_exception';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_topology_audit_event_no_truncate
    BEFORE TRUNCATE ON topology_audit_event
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();

CREATE TRIGGER trg_topology_entity_diff_no_truncate
    BEFORE TRUNCATE ON topology_entity_diff
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_topology_entity_diff_no_truncate ON topology_entity_diff;
DROP TRIGGER IF EXISTS trg_topology_audit_event_no_truncate ON topology_audit_event;
DROP FUNCTION IF EXISTS caisson_reject_append_only_truncate();

DROP TRIGGER IF EXISTS trg_topology_entity_diff_append_only ON topology_entity_diff;
DROP TRIGGER IF EXISTS trg_topology_audit_event_append_only ON topology_audit_event;
DROP FUNCTION IF EXISTS caisson_reject_append_only_mutation();

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

            migrationBuilder.DropForeignKey(
                name: "fk_topology_entity_diff_snapshots_snapshot_id",
                table: "topology_entity_diff");

            migrationBuilder.DropColumn(
                name: "external_device_key",
                table: "switch");

            migrationBuilder.DropColumn(
                name: "external_device_key",
                table: "server");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "discovery_job_step");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "discovery_job");

            migrationBuilder.AddForeignKey(
                name: "fk_topology_entity_diff_snapshots_snapshot_id",
                table: "topology_entity_diff",
                column: "snapshot_id",
                principalTable: "topology_snapshot",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
