using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DesiredStateIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "desired_state_ingestion_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    commit_author = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    commit_time_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    commit_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_delivery_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    error_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error_summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_state_ingestion_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "desired_state_validation_error",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    line = table.Column<int>(type: "integer", nullable: true),
                    column = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_state_validation_error", x => x.id);
                    table.ForeignKey(
                        name: "fk_desired_state_validation_error_desired_state_ingestion_run_",
                        column: x => x.ingestion_run_id,
                        principalTable: "desired_state_ingestion_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "desired_state_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ingestion_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_state_version", x => x.id);
                    table.ForeignKey(
                        name: "fk_desired_state_version_desired_state_ingestion_run_ingestion",
                        column: x => x.ingestion_run_id,
                        principalTable: "desired_state_ingestion_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "desired_rack_intent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_state_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stable_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_rack_intent", x => x.id);
                    table.ForeignKey(
                        name: "fk_desired_rack_intent_desired_state_versions_desired_state_ve",
                        column: x => x.desired_state_version_id,
                        principalTable: "desired_state_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "desired_switch_intent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_rack_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    switch_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stable_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_switch_intent", x => x.id);
                    table.ForeignKey(
                        name: "fk_desired_switch_intent_desired_rack_intent_desired_rack_inte",
                        column: x => x.desired_rack_intent_id,
                        principalTable: "desired_rack_intent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "desired_port_intent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_switch_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    port_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stable_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    access_vlan = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    neighbor_system_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    neighbor_port_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_desired_port_intent", x => x.id);
                    table.CheckConstraint("ck_desired_port_intent_access_vlan", "access_vlan >= 1 AND access_vlan <= 4094");
                    table.ForeignKey(
                        name: "fk_desired_port_intent_desired_switch_intents_desired_switch_i",
                        column: x => x.desired_switch_intent_id,
                        principalTable: "desired_switch_intent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_desired_port_intent_desired_switch_intent_id_port_name",
                table: "desired_port_intent",
                columns: new[] { "desired_switch_intent_id", "port_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_desired_port_intent_stable_key",
                table: "desired_port_intent",
                column: "stable_key");

            migrationBuilder.CreateIndex(
                name: "ix_desired_rack_intent_desired_state_version_id",
                table: "desired_rack_intent",
                column: "desired_state_version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_ingestion_run_started_at",
                table: "desired_state_ingestion_run",
                column: "started_at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_desired_state_ingestion_run_commit_sha",
                table: "desired_state_ingestion_run",
                column: "commit_sha",
                unique: true,
                filter: "commit_sha IS NOT NULL AND status IN ('Running','Succeeded','PartiallySucceeded','ValidationFailed')");

            migrationBuilder.CreateIndex(
                name: "ux_desired_state_ingestion_run_webhook_delivery_id",
                table: "desired_state_ingestion_run",
                column: "webhook_delivery_id",
                unique: true,
                filter: "webhook_delivery_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_validation_error_ingestion_run_id",
                table: "desired_state_validation_error",
                column: "ingestion_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_version_ingestion_run_id",
                table: "desired_state_version",
                column: "ingestion_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_version_rack_slug_created_at_id",
                table: "desired_state_version",
                columns: new[] { "rack_slug", "created_at_utc", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_desired_switch_intent_desired_rack_intent_id_switch_name",
                table: "desired_switch_intent",
                columns: new[] { "desired_rack_intent_id", "switch_name" },
                unique: true);

            // Story #62 (NFR7): the four IAppendOnly desired-state tables get the same tamper-evidence
            // trigger as topology_audit_event/topology_entity_diff (see the DiffAppendOnlyAndConcurrency
            // migration), reusing the already-generalized caisson_reject_append_only_mutation()/
            // caisson_reject_append_only_truncate() functions rather than redefining them.
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_desired_state_version_append_only
    BEFORE UPDATE OR DELETE ON desired_state_version
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();
CREATE TRIGGER trg_desired_state_version_no_truncate
    BEFORE TRUNCATE ON desired_state_version
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();

CREATE TRIGGER trg_desired_rack_intent_append_only
    BEFORE UPDATE OR DELETE ON desired_rack_intent
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();
CREATE TRIGGER trg_desired_rack_intent_no_truncate
    BEFORE TRUNCATE ON desired_rack_intent
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();

CREATE TRIGGER trg_desired_switch_intent_append_only
    BEFORE UPDATE OR DELETE ON desired_switch_intent
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();
CREATE TRIGGER trg_desired_switch_intent_no_truncate
    BEFORE TRUNCATE ON desired_switch_intent
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();

CREATE TRIGGER trg_desired_port_intent_append_only
    BEFORE UPDATE OR DELETE ON desired_port_intent
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();
CREATE TRIGGER trg_desired_port_intent_no_truncate
    BEFORE TRUNCATE ON desired_port_intent
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();

CREATE TRIGGER trg_desired_state_validation_error_append_only
    BEFORE UPDATE OR DELETE ON desired_state_validation_error
    FOR EACH ROW EXECUTE FUNCTION caisson_reject_append_only_mutation();
CREATE TRIGGER trg_desired_state_validation_error_no_truncate
    BEFORE TRUNCATE ON desired_state_validation_error
    FOR EACH STATEMENT EXECUTE FUNCTION caisson_reject_append_only_truncate();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_desired_state_validation_error_no_truncate ON desired_state_validation_error;
DROP TRIGGER IF EXISTS trg_desired_state_validation_error_append_only ON desired_state_validation_error;
DROP TRIGGER IF EXISTS trg_desired_port_intent_no_truncate ON desired_port_intent;
DROP TRIGGER IF EXISTS trg_desired_port_intent_append_only ON desired_port_intent;
DROP TRIGGER IF EXISTS trg_desired_switch_intent_no_truncate ON desired_switch_intent;
DROP TRIGGER IF EXISTS trg_desired_switch_intent_append_only ON desired_switch_intent;
DROP TRIGGER IF EXISTS trg_desired_rack_intent_no_truncate ON desired_rack_intent;
DROP TRIGGER IF EXISTS trg_desired_rack_intent_append_only ON desired_rack_intent;
DROP TRIGGER IF EXISTS trg_desired_state_version_no_truncate ON desired_state_version;
DROP TRIGGER IF EXISTS trg_desired_state_version_append_only ON desired_state_version;
");

            migrationBuilder.DropTable(
                name: "desired_port_intent");

            migrationBuilder.DropTable(
                name: "desired_state_validation_error");

            migrationBuilder.DropTable(
                name: "desired_switch_intent");

            migrationBuilder.DropTable(
                name: "desired_rack_intent");

            migrationBuilder.DropTable(
                name: "desired_state_version");

            migrationBuilder.DropTable(
                name: "desired_state_ingestion_run");
        }
    }
}
