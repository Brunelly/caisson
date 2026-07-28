using System.Net.Http;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>Correlation-id behaviour (AC5): generated when absent/invalid, echoed when valid.</summary>
[Collection(ApiCollection.Name)]
public sealed class CorrelationIdTests
{
    private const string Header = "X-Correlation-Id";

    private readonly CaissonApiFactory _factory;

    public CorrelationIdTests(CaissonApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Generates_a_correlation_id_when_absent()
    {
        if (!_factory.Available)
        {
            return;
        }

        var response = await _factory.CreateClient().GetAsync("/health/live");

        var echoed = response.Headers.GetValues(Header).Single();
        Guid.TryParse(echoed, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Echoes_a_valid_correlation_id_unchanged()
    {
        if (!_factory.Available)
        {
            return;
        }

        var provided = Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(Header, provided);

        var response = await _factory.CreateClient().SendAsync(request);

        response.Headers.GetValues(Header).Single().Should().Be(provided);
    }

    [Fact]
    public async Task Regenerates_an_invalid_correlation_id()
    {
        if (!_factory.Available)
        {
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(Header, "not-a-valid-correlation-id");

        var response = await _factory.CreateClient().SendAsync(request);

        var echoed = response.Headers.GetValues(Header).Single();
        echoed.Should().NotBe("not-a-valid-correlation-id");
        Guid.TryParse(echoed, out _).Should().BeTrue();
    }
}
