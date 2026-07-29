using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Runner;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>
/// DB-free tests for the webhook ingestion drainer (story #62, AC1): it forwards the queued request's
/// delivery id/correlation id to the shared <c>RunAsync</c> entry point with the Webhook trigger, and a
/// per-request fault is isolated (never thrown back out).
/// </summary>
public sealed class DesiredStateIngestionRunnerTests
{
    [Fact]
    public async Task ProcessOneAsync_invokes_run_async_with_webhook_trigger_and_the_requests_ids()
    {
        var fake = new FakeDesiredStateIngestionService();
        var runner = CreateRunner(fake);
        var request = new WebhookIngestionRequest("delivery-42", Guid.NewGuid());

        await runner.ProcessOneAsync(request, default);

        fake.Calls.Should().ContainSingle();
        fake.Calls[0].Trigger.Should().Be(IngestionTriggerType.Webhook);
        fake.Calls[0].WebhookDeliveryId.Should().Be("delivery-42");
        fake.Calls[0].CorrelationId.Should().Be(request.CorrelationId);
    }

    [Fact]
    public async Task ProcessOneAsync_isolates_a_fault_from_the_service_without_throwing()
    {
        var fake = new FakeDesiredStateIngestionService { ThrowOnNextCall = new InvalidOperationException("boom") };
        var runner = CreateRunner(fake);
        var request = new WebhookIngestionRequest("delivery-1", Guid.NewGuid());

        var act = async () => await runner.ProcessOneAsync(request, default);

        await act.Should().NotThrowAsync();
    }

    private static DesiredStateIngestionRunner CreateRunner(FakeDesiredStateIngestionService fake)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDesiredStateIngestionService>(fake);
        var provider = services.BuildServiceProvider();

        return new DesiredStateIngestionRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new DesiredStateIngestionSignal(),
            NullLogger<DesiredStateIngestionRunner>.Instance);
    }
}
