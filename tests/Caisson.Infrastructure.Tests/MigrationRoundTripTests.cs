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
        "topology_change_summary", "topology_entity_diff", "topology_audit_event",
        "desired_state_ingestion_run", "desired_state_version", "desired_rack_intent",
        "desired_switch_intent", "desired_port_intent", "desired_state_validation_error",
        "drift_apply_job", "drift_apply_job_step",
        "audit_outbox", "audit_denial_bucket",
    };

    private static readonly string[] ExpectedIndexes =
    {
        "ix_topology_snapshot_rack_id_created_at",
        "ux_topology_snapshot_rack_id_version",
        "ix_topology_snapshot_rack_id_completed_at",
        "ux_topology_entity_diff_snapshot_entity_key",
        "ix_topology_entity_diff_rack_id_snapshot_id",
        "ix_topology_entity_diff_rack_id_entity_type_entity_stable_key",
        "ix_topology_audit_event_rack_id_occurred_at",
        "ix_topology_audit_event_correlation_id",
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
        "ux_desired_state_ingestion_run_commit_sha",
        "ux_desired_state_ingestion_run_webhook_delivery_id",
        "ix_desired_state_ingestion_run_started_at",
        "ix_desired_state_version_rack_slug_created_at_id",
        "ix_desired_rack_intent_desired_state_version_id",
        "ix_desired_switch_intent_desired_rack_intent_id_switch_name",
        "ix_desired_port_intent_desired_switch_intent_id_port_name",
        "ix_desired_port_intent_stable_key",
        "ix_desired_state_validation_error_run_created_id",
        "ux_drift_apply_job_drift_item_active",
        "ix_drift_apply_job_rack_id_requested_at",
        "ux_drift_apply_job_step_job_id_step_name",
        "ix_audit_outbox_status_available_at",
        "ux_audit_denial_bucket_key",
        "ix_audit_denial_bucket_window_end_at",
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
