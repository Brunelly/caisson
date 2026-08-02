using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// A minimal, hand-rolled <see cref="IServiceScopeFactory"/> that hands out a fresh
/// <see cref="CaissonDbContext"/> per scope (mirroring the real host's per-tick scope) without pulling in
/// a full DI container — the only service <see cref="AuditOutboxDispatcher"/> resolves from its scope.
/// </summary>
internal sealed class FakeScopeFactory : IServiceScopeFactory
{
    private readonly PostgresFixture _fixture;
    private readonly Action? _onScopeCreated;

    public FakeScopeFactory(PostgresFixture fixture, Action? onScopeCreated = null)
    {
        _fixture = fixture;
        _onScopeCreated = onScopeCreated;
    }

    /// <summary>
    /// Creating the per-tick scope is the one deterministic point a test can act on that sits AFTER a
    /// service has taken its in-memory snapshot of the work to do and BEFORE it touches the database —
    /// which is exactly where a concurrent request lands in the interleavings these tests reproduce.
    /// </summary>
    public IServiceScope CreateScope()
    {
        _onScopeCreated?.Invoke();
        return new FakeScope(_fixture.CreateContext());
    }

    private sealed class FakeScope : IServiceScope, IAsyncDisposable
    {
        private readonly CaissonDbContext _context;

        public FakeScope(CaissonDbContext context)
        {
            _context = context;
            ServiceProvider = new FakeServiceProvider(context);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _context.Dispose();

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly CaissonDbContext _context;

        public FakeServiceProvider(CaissonDbContext context) => _context = context;

        public object? GetService(Type serviceType) => serviceType == typeof(CaissonDbContext) ? _context : null;
    }
}

/// <summary>A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> only advances when <see cref="Advance"/> is called.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// A <see cref="ManualTimeProvider"/> that also runs a callback on its <paramref name="fireOnCall"/>-th
/// <see cref="GetUtcNow"/> call. The clock is the one collaborator a background service consults at
/// well-defined points in its own flow, which makes it the deterministic seam for injecting "something
/// else changed this row while you were working on it" — no threads, no sleeps, no flakiness.
/// </summary>
internal sealed class HookingTimeProvider : TimeProvider
{
    private readonly int _fireOnCall;
    private readonly Action _onFire;
    private DateTimeOffset _now;
    private int _calls;

    public HookingTimeProvider(DateTimeOffset start, int fireOnCall, Action onFire)
    {
        _now = start;
        _fireOnCall = fireOnCall;
        _onFire = onFire;
    }

    /// <summary>Whether the callback actually ran — asserted so a test can never silently stop reproducing.</summary>
    public bool Fired { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Increment(ref _calls) == _fireOnCall)
        {
            Fired = true;
            _onFire();
        }

        return _now;
    }

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>Builds an <see cref="AuditOutboxDispatcher"/> wired to a <see cref="FakeScopeFactory"/> for deterministic tests.</summary>
internal static class AuditOutboxDispatcherTestFactory
{
    public static AuditOutboxDispatcher Create(
        PostgresFixture fixture, TimeProvider time, AuditDurabilityOptions options)
        => new(
            new FakeScopeFactory(fixture),
            time,
            Options.Create(options),
            new AuditOutboxMetrics(),
            NullLogger<AuditOutboxDispatcher>.Instance);
}
