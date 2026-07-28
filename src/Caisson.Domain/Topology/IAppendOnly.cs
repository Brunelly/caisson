namespace Caisson.Domain.Topology;

/// <summary>
/// Marks a tamper-evident, append-only record: once inserted it must never be modified <b>or</b>
/// deleted (NFR4). This is stronger than the snapshot-scoped immutability of <see cref="ISnapshotScoped"/>
/// — snapshot content may still be deleted for retention/rollback, whereas audit and diff rows are
/// permanent. The <c>DbContext</c> guard recognises this interface generically and the database enforces
/// it as well (a <c>BEFORE UPDATE OR DELETE</c> trigger on the audit table), so tamper-evidence holds
/// even against raw SQL.
/// </summary>
public interface IAppendOnly
{
}
