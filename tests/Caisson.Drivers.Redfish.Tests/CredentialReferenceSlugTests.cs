using Caisson.Drivers.Redfish.Credentials;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// Finding #33: <see cref="CredentialReferenceSlug"/> normalization is inherently lossy (separators all
/// collapse to <c>_</c>), so <see cref="CredentialReferenceSlug.Validate"/> restricts the accepted charset
/// up front — closing off the separator-collision class entirely and rejecting an empty reference outright
/// rather than letting it silently fall back to the global (non-per-device) credential. Mirrors the
/// MikroTik driver's equivalent test.
/// </summary>
public sealed class CredentialReferenceSlugTests
{
    [Theory]
    [InlineData("")]
    [InlineData("ilo-1")]
    [InlineData("ilo.1")]
    [InlineData("ilo/1")]
    public void Validate_rejects_an_empty_or_disallowed_reference(string reference)
    {
        var act = () => CredentialReferenceSlug.Validate(reference, "device-1");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_accepts_a_reference_matching_the_allowed_charset()
    {
        var act = () => CredentialReferenceSlug.Validate("ilo_1", "device-1");

        act.Should().NotThrow();
    }
}
