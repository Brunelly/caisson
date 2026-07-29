using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>DB-free tests for classifying a commit-fetch fault into an <see cref="IngestionErrorCategory"/> (AC6).</summary>
public sealed class DesiredStateIngestionServiceClassificationTests
{
    [Theory]
    [InlineData("Authentication failed for 'https://example.com/repo.git'")]
    [InlineData("invalid credentials supplied")]
    public void Credential_shaped_messages_classify_as_auth(string message)
        => DesiredStateIngestionService.ClassifyFetchException(new InvalidOperationException(message))
            .Should().Be(IngestionErrorCategory.Auth);

    [Theory]
    [InlineData("Failed to connect to host")]
    [InlineData("DNS resolution failed")]
    [InlineData("")]
    public void Other_messages_classify_as_network(string message)
        => DesiredStateIngestionService.ClassifyFetchException(new InvalidOperationException(message))
            .Should().Be(IngestionErrorCategory.Network);
}
