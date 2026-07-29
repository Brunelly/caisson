using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Git;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Caisson.VirtualRack.Fixtures;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Story #62 end-to-end: renders <see cref="VirtualRackDefinition"/>'s ground truth into desired-state
/// YAML via <see cref="DesiredStateYamlRenderer"/>, commits it into a real, local, ephemeral Git
/// repository (via LibGit2Sharp — no network, no git-server container, per ADR 0017's in-process
/// ethos), runs a full poll cycle through the REAL <see cref="DesiredStateIngestionService"/> and
/// <see cref="LibGit2SharpRepositoryProvider"/>, and asserts the persisted typed model matches the
/// ground truth exactly. A second commit then invalidates one rack while a second, unrelated rack goes
/// untouched — proving Q3's partial-accept policy end-to-end (AC3 scenario 2).
/// </summary>
public sealed class DesiredStateIngestionEndToEndTests : IAsyncLifetime
{
    private const string SecondRackSlug = "vrack-2";
    private const string SecondRackYaml = """
        rackSlug: vrack-2
        switches:
          - name: sw-aux
            ports:
              - name: eth0
                accessVlan: 99
        """;

    private readonly PostgresHarness _postgres = new();
    private string _originPath = string.Empty;
    private string _mirrorPath = string.Empty;
    private string _branch = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _originPath = Directory.CreateTempSubdirectory("caisson-desired-state-origin-").FullName;
        _mirrorPath = Path.Combine(Directory.CreateTempSubdirectory("caisson-desired-state-mirror-").FullName, "mirror.git");
        Repository.Init(_originPath);
    }

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_originPath);
        TryDeleteDirectory(Path.GetDirectoryName(_mirrorPath));
        return ((IAsyncDisposable)_postgres).DisposeAsync().AsTask();
    }

    [SkippableFact]
    public async Task Poll_cycle_ingests_the_virtual_rack_ground_truth_exactly()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        CommitFile("desired-state/racks/vrack-1.yaml", DesiredStateYamlRenderer.Render(), "initial commit");

        var result = await RunPollAsync();
        result.Disposition.Should().Be(IngestionRunDisposition.Started);

        await using var context = _postgres.CreateContext();
        var run = await context.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
        run.Status.Should().Be(IngestionRunStatus.Succeeded);

        var tree = await context.ActiveVersionWithTreeAsync(DesiredStateYamlRenderer.RackSlug);
        tree.Should().NotBeNull();
        tree!.Rack.RackSlug.Should().Be(DesiredStateYamlRenderer.RackSlug);
        tree.Switches.Should().ContainSingle().Which.SwitchName.Should().Be(VirtualRackDefinition.SwitchId);

        var ports = tree.Ports.ToDictionary(p => p.PortName);
        ports.Should().HaveCount(4);
        ports[VirtualRackDefinition.CleanPort].AccessVlan.Should().Be(VirtualRackDefinition.CleanVlan);
        ports[VirtualRackDefinition.CleanPort].Description.Should().Be("clean-port");
        ports[VirtualRackDefinition.AmbiguousPortA].AccessVlan.Should().Be(VirtualRackDefinition.AmbiguousVlanA);
        ports[VirtualRackDefinition.AmbiguousPortB].AccessVlan.Should().Be(VirtualRackDefinition.AmbiguousVlanB);
        ports[VirtualRackDefinition.UnmappedPort].AccessVlan.Should().Be(VirtualRackDefinition.UnmappedPortVlan);
    }

    [SkippableFact]
    public async Task Second_commit_invalidating_one_rack_leaves_it_and_the_other_rack_unaffected()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        CommitFile("desired-state/racks/vrack-1.yaml", DesiredStateYamlRenderer.Render(), "initial commit");
        CommitFile("desired-state/racks/vrack-2.yaml", SecondRackYaml, "add second rack");

        await RunPollAsync();

        Guid firstVrack1VersionId;
        Guid firstVrack2VersionId;
        await using (var context = _postgres.CreateContext())
        {
            firstVrack1VersionId = (await context.ActiveVersionForRackAsync(DesiredStateYamlRenderer.RackSlug))!.Id;
            firstVrack2VersionId = (await context.ActiveVersionForRackAsync(SecondRackSlug))!.Id;
        }

        // Second commit: vrack-1 becomes invalid; vrack-2 is left completely untouched.
        CommitFile("desired-state/racks/vrack-1.yaml", DesiredStateYamlRenderer.RenderWithInvalidVlan(), "break vrack-1");

        var secondResult = await RunPollAsync();

        await using var verify = _postgres.CreateContext();
        var run = await verify.DesiredStateIngestionRuns.SingleAsync(r => r.Id == secondResult.RunId);
        run.Status.Should().Be(
            IngestionRunStatus.PartiallySucceeded,
            "vrack-1 fails validation while vrack-2 (unchanged) still counts as a successful outcome");

        var errors = await verify.DesiredStateValidationErrors.Where(e => e.IngestionRunId == run.Id).ToListAsync();
        errors.Should().ContainSingle(e => e.RackSlug == DesiredStateYamlRenderer.RackSlug);

        var vrack1Active = await verify.ActiveVersionForRackAsync(DesiredStateYamlRenderer.RackSlug);
        var vrack2Active = await verify.ActiveVersionForRackAsync(SecondRackSlug);

        vrack1Active!.Id.Should().Be(firstVrack1VersionId, "the invalidated rack must keep its previous valid version active");
        vrack2Active!.Id.Should().Be(firstVrack2VersionId, "the untouched rack must be completely unaffected");
    }

    private async Task<IngestionRunResult> RunPollAsync()
    {
        await using var context = _postgres.CreateContext();
        var git = new LibGit2SharpRepositoryProvider(_originPath, _mirrorPath, NullLogger<LibGit2SharpRepositoryProvider>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new GitIngestionOptions
        {
            Enabled = true,
            RepoUrl = _originPath,
            Branch = _branch,
            PathGlob = "desired-state/racks/*.yaml",
        });

        var service = new DesiredStateIngestionService(
            context, git, new GuidTopologyIdGenerator(), TimeProvider.System, options, new GitIngestionMetrics(),
            NullLogger<DesiredStateIngestionService>.Instance);

        return await service.RunAsync(IngestionTriggerType.Poll, webhookDeliveryId: null, Guid.NewGuid(), default);
    }

    private void CommitFile(string relativePath, string content, string message)
    {
        var fullPath = Path.Combine(_originPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        using var repo = new Repository(_originPath);
        Commands.Stage(repo, "*");
        var signature = new Signature("Caisson Test", "test@example.com", DateTimeOffset.UtcNow);
        repo.Commit(message, signature, signature);
        _branch = repo.Head.FriendlyName;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
