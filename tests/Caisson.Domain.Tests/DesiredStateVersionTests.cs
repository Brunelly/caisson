using Caisson.Domain.DesiredState;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Story #63, AC1: the constructor guard for the new revision-metadata fields — author fields are
/// tolerated as null (git may omit committer identity) while payload/schemaVersion/ingestedBy are always
/// required, and every new bounded field rejects an over-length value.
/// </summary>
public sealed class DesiredStateVersionTests
{
    private static DesiredStateVersion Create(
        string? authorName = "author", string? authorEmail = "author@example.com", DateTime? authorWhenUtc = null,
        string desiredStateJson = "{}", int schemaVersion = 1, string ingestedBy = "desired-state-ingestion")
        => new(
            Guid.NewGuid(), "rack-1", "a".PadLeft(40, '0'), Guid.NewGuid(), DateTime.UtcNow, "hash-1",
            desiredStateJson, schemaVersion, ingestedBy, authorName, authorEmail, authorWhenUtc ?? DateTime.UtcNow);

    [Fact]
    public void Author_fields_default_to_null_and_still_construct_cleanly()
    {
        var version = new DesiredStateVersion(
            Guid.NewGuid(), "rack-1", "a".PadLeft(40, '0'), Guid.NewGuid(), DateTime.UtcNow, "hash-1",
            "{}", 1, "desired-state-ingestion");

        version.AuthorName.Should().BeNull();
        version.AuthorEmail.Should().BeNull();
        version.AuthorWhenUtc.Should().BeNull();
    }

    [Fact]
    public void Author_fields_are_carried_when_supplied()
    {
        var when = DateTime.UtcNow;
        var version = Create(authorName: "Jane Doe", authorEmail: "jane@example.com", authorWhenUtc: when);

        version.AuthorName.Should().Be("Jane Doe");
        version.AuthorEmail.Should().Be("jane@example.com");
        version.AuthorWhenUtc.Should().Be(when);
    }

    [Fact]
    public void DesiredStateJson_is_required()
    {
        var act = () => Create(desiredStateJson: "");

        act.Should().Throw<ArgumentException>().WithParameterName("desiredStateJson");
    }

    [Fact]
    public void IngestedBy_is_required()
    {
        var act = () => Create(ingestedBy: "");

        act.Should().Throw<ArgumentException>().WithParameterName("ingestedBy");
    }

    [Fact]
    public void SchemaVersion_below_one_is_rejected()
    {
        var act = () => Create(schemaVersion: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("schemaVersion");
    }

    [Fact]
    public void DesiredStateJson_over_the_length_bound_is_rejected()
    {
        var oversized = new string('a', DesiredStateSchema.MaxDesiredStateJsonLength + 1);

        var act = () => Create(desiredStateJson: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("desiredStateJson");
    }

    [Fact]
    public void IngestedBy_over_the_length_bound_is_rejected()
    {
        var oversized = new string('a', DesiredStateSchema.MaxIngestedByLength + 1);

        var act = () => Create(ingestedBy: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("ingestedBy");
    }

    [Fact]
    public void AuthorName_over_the_length_bound_is_rejected()
    {
        var oversized = new string('a', DesiredStateSchema.MaxAuthorNameLength + 1);

        var act = () => Create(authorName: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("authorName");
    }

    [Fact]
    public void AuthorEmail_over_the_length_bound_is_rejected()
    {
        var oversized = new string('a', DesiredStateSchema.MaxAuthorEmailLength + 1);

        var act = () => Create(authorEmail: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("authorEmail");
    }

    [Fact]
    public void SchemaVersion_defaults_to_the_current_schema_version_constant_in_practice()
    {
        DesiredStateSchema.CurrentSchemaVersion.Should().Be(1);
    }
}
