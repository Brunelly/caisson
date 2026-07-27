using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialObservedState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rack",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rack", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "topology_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_topology_snapshot_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bmc_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bmc_address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    bmc_uuid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_server_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "switch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    management_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    os_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_switch", x => x.id);
                    table.ForeignKey(
                        name: "fk_switch_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_switch_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "topology_change_summary",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_counts_json = table.Column<string>(type: "jsonb", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_change_summary", x => x.id);
                    table.ForeignKey(
                        name: "fk_topology_change_summary_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_topology_change_summary_snapshots_previous_snapshot_id",
                        column: x => x.previous_snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_topology_change_summary_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vlan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vlan_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vlan", x => x.id);
                    table.ForeignKey(
                        name: "fk_vlan_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_vlan_topology_snapshot_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    mac_primary = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    link_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nic", x => x.id);
                    table.ForeignKey(
                        name: "fk_nic_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_nic_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_nic_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "switch_port",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    switch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    port_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_up = table.Column<bool>(type: "boolean", nullable: true),
                    pvid = table.Column<int>(type: "integer", nullable: true),
                    tagged_vlans = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_switch_port", x => x.id);
                    table.ForeignKey(
                        name: "fk_switch_port_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_switch_port_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_switch_port_switch_switch_id",
                        column: x => x.switch_id,
                        principalTable: "switch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mac_address",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mac = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mac_address", x => x.id);
                    table.ForeignKey(
                        name: "fk_mac_address_nics_nic_id",
                        column: x => x.nic_id,
                        principalTable: "nic",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_mac_address_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_mac_address_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lldp_neighbour",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    switch_port_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chassis_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    port_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    system_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    mgmt_address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lldp_neighbour", x => x.id);
                    table.ForeignKey(
                        name: "fk_lldp_neighbour_racks_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_lldp_neighbour_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_lldp_neighbour_switch_ports_switch_port_id",
                        column: x => x.switch_port_id,
                        principalTable: "switch_port",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "topology_candidate_mapping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    switch_port_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", maxLength: 8192, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_candidate_mapping", x => x.id);
                    table.CheckConstraint("ck_topology_candidate_mapping_confidence", "confidence >= 0.0 AND confidence <= 1.0");
                    table.ForeignKey(
                        name: "fk_topology_candidate_mapping_nic_nic_id",
                        column: x => x.nic_id,
                        principalTable: "nic",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_topology_candidate_mapping_rack_rack_id",
                        column: x => x.rack_id,
                        principalTable: "rack",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_topology_candidate_mapping_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "topology_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_topology_candidate_mapping_switch_port_switch_port_id",
                        column: x => x.switch_port_id,
                        principalTable: "switch_port",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_lldp_neighbour_rack_id",
                table: "lldp_neighbour",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_lldp_neighbour_snapshot_id_switch_port_id",
                table: "lldp_neighbour",
                columns: new[] { "snapshot_id", "switch_port_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lldp_neighbour_switch_port_id",
                table: "lldp_neighbour",
                column: "switch_port_id");

            migrationBuilder.CreateIndex(
                name: "ix_mac_address_nic_id",
                table: "mac_address",
                column: "nic_id");

            migrationBuilder.CreateIndex(
                name: "ix_mac_address_rack_id",
                table: "mac_address",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_mac_address_snapshot_id_mac",
                table: "mac_address",
                columns: new[] { "snapshot_id", "mac" });

            migrationBuilder.CreateIndex(
                name: "ix_nic_mac_primary",
                table: "nic",
                column: "mac_primary");

            migrationBuilder.CreateIndex(
                name: "ix_nic_rack_id",
                table: "nic",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_nic_server_id",
                table: "nic",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_nic_snapshot_id_server_id",
                table: "nic",
                columns: new[] { "snapshot_id", "server_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rack_external_key",
                table: "rack",
                column: "external_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_bmc_uuid",
                table: "server",
                column: "bmc_uuid");

            migrationBuilder.CreateIndex(
                name: "ix_server_rack_id",
                table: "server",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_snapshot_id_rack_id",
                table: "server",
                columns: new[] { "snapshot_id", "rack_id" });

            migrationBuilder.CreateIndex(
                name: "ix_switch_rack_id",
                table: "switch",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_switch_snapshot_id_rack_id",
                table: "switch",
                columns: new[] { "snapshot_id", "rack_id" });

            migrationBuilder.CreateIndex(
                name: "ux_switch_snapshot_id_serial",
                table: "switch",
                columns: new[] { "snapshot_id", "serial" },
                unique: true,
                filter: "serial IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_switch_port_rack_id",
                table: "switch_port",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_switch_port_switch_id",
                table: "switch_port",
                column: "switch_id");

            migrationBuilder.CreateIndex(
                name: "ux_switch_port_snapshot_id_switch_id_port_name",
                table: "switch_port",
                columns: new[] { "snapshot_id", "switch_id", "port_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_candidate_mapping_nic_id",
                table: "topology_candidate_mapping",
                column: "nic_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_candidate_mapping_rack_id",
                table: "topology_candidate_mapping",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_candidate_mapping_snapshot_id_confidence",
                table: "topology_candidate_mapping",
                columns: new[] { "snapshot_id", "confidence" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_topology_candidate_mapping_snapshot_id_nic_id_switch_port_id",
                table: "topology_candidate_mapping",
                columns: new[] { "snapshot_id", "nic_id", "switch_port_id" });

            migrationBuilder.CreateIndex(
                name: "ix_topology_candidate_mapping_switch_port_id",
                table: "topology_candidate_mapping",
                column: "switch_port_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_change_summary_previous_snapshot_id",
                table: "topology_change_summary",
                column: "previous_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_change_summary_rack_id",
                table: "topology_change_summary",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_change_summary_snapshot_id",
                table: "topology_change_summary",
                column: "snapshot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_snapshot_rack_id_created_at",
                table: "topology_snapshot",
                columns: new[] { "rack_id", "created_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_vlan_rack_id",
                table: "vlan",
                column: "rack_id");

            migrationBuilder.CreateIndex(
                name: "ix_vlan_snapshot_id_vlan_id",
                table: "vlan",
                columns: new[] { "snapshot_id", "vlan_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lldp_neighbour");

            migrationBuilder.DropTable(
                name: "mac_address");

            migrationBuilder.DropTable(
                name: "topology_candidate_mapping");

            migrationBuilder.DropTable(
                name: "topology_change_summary");

            migrationBuilder.DropTable(
                name: "vlan");

            migrationBuilder.DropTable(
                name: "nic");

            migrationBuilder.DropTable(
                name: "switch_port");

            migrationBuilder.DropTable(
                name: "server");

            migrationBuilder.DropTable(
                name: "switch");

            migrationBuilder.DropTable(
                name: "topology_snapshot");

            migrationBuilder.DropTable(
                name: "rack");
        }
    }
}
