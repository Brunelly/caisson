using System.Security.Cryptography;
using System.Text;

namespace Caisson.Domain.Auditing;

/// <summary>
/// Derives a stable, reproducible outbox/audit id from a (subject id, action) pair (story #308, ADR
/// 0064) — used for terminal job transitions so a concurrently-racing or retried reconciliation sweep
/// can never stage two outbox rows for the same (job, terminal-action) transition, even though the
/// primary defence is the reaper's own <c>FOR UPDATE SKIP LOCKED</c> claim (this is belt-and-braces).
/// </summary>
public static class DeterministicAuditId
{
    /// <summary>Computes a deterministic <see cref="Guid"/> from <paramref name="subjectId"/> and <paramref name="action"/>.</summary>
    public static Guid For(Guid subjectId, string action)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        Span<byte> hash = stackalloc byte[32];
        var input = $"{subjectId:N}|{action}";
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return new Guid(hash[..16]);
    }
}
