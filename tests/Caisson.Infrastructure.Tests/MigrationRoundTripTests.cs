using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Proves the schema can be applied to latest and rolled back cleanly and idempotently (AC4, NFR3).
/// </summary>
public sealed class MigrationRoundTripTests : IClassFixture<PostgresFixture>
{
    private static readonly string[] ExpectedTables =
    {
        "rack", "topology_snapshot", "switch", "switch_port", "server", "nic",
        "mac_address", "vlan", "lldp_neighbour", "topology_candidate_mapping",
        "topology_change_summary",
    };

    private static readonly string[] ExpectedIndexes =
    {
        "ix_topology_snapshot_rack_id_created_at",
        "ix_switch_snapshot_id_rack_id",
        "ux_switch_snapshot_id_serial",
        "ux_switch_port_snapshot_id_switch_id_port_name",
        "ix_server_snapshot_id_rack_id",
        "ix_server_bmc_uuid",
        "ix_nic_snapshot_id_server_id",
        "ix_nic_mac_primary",
        "ix_mac_address_snapshot_id_mac",
        "ix_lldp_neighbour_snapshot_id_switch_port_id",
        "ix_vlan_snapshot_id_vlan_id",
        "ix_topology_candidate_mapping_snapshot_id_nic_id_switch_port_id",
        "ix_topology_candidate_mapping_snapshot_id_confidence",
        "ix_rack_external_key",
    };

    private readonly PostgresFixture _fixture;

    public MigrationRoundTripTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_applies_rolls_back_cleanly_and_idempotently_then_re_applies()
    {
        // 1. Apply to latest.
        await _fixture.MigrateAsync();

        var tables = await _fixture.GetTableNamesAsync();
        tables.Should().Contain(ExpectedTables);

        var indexes = await _fixture.GetIndexNamesAsync();
        indexes.Should().Contain(ExpectedIndexes);

        // 2. Roll back to nothing via the migrator.
        await MigrateToAsync(Migration.InitialDatabase);

        var afterRollback = await _fixture.GetTableNamesAsync();
        afterRollback.Should().NotContain(ExpectedTables);
        afterRollback.Should().OnlyContain(t => t == "__EFMigrationsHistory");

        // 3. Rolling back again is idempotent (no-op, no error).
        await MigrateToAsync(Migration.InitialDatabase);

        // 4. Re-apply from scratch.
        await _fixture.MigrateAsync();

        var reapplied = await _fixture.GetTableNamesAsync();
        reapplied.Should().Contain(ExpectedTables);
        (await _fixture.GetIndexNamesAsync()).Should().Contain(ExpectedIndexes);
    }

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var context = _fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }
}
