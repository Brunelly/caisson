using Caisson.Drivers.MikroTik.Credentials;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// Finding #33: <see cref="CredentialReferenceSlug"/> normalization is inherently lossy (separators all
/// collapse to <c>_</c>), so <see cref="CredentialReferenceSlug.Validate"/> restricts the accepted charset
/// up front — closing off the separator-collision class entirely and rejecting an empty reference outright
/// rather than letting it silently fall back to the global (non-per-device) credential.
/// </summary>
public sealed class CredentialReferenceSlugTests
{
    [Theory]
    [InlineData("")]
    [InlineData("rack1-sw")]
    [InlineData("rack1.sw")]
    [InlineData("rack1/sw")]
    [InlineData(" ")]
    public void Validate_rejects_an_empty_or_disallowed_reference(string reference)
    {
        var act = () => CredentialReferenceSlug.Validate(reference, "device-1");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("rack1_sw")]
    [InlineData("RACK1")]
    [InlineData("a")]
    public void Validate_accepts_a_reference_matching_the_allowed_charset(string reference)
    {
        var act = () => CredentialReferenceSlug.Validate(reference, "device-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_rejects_a_reference_over_64_characters()
    {
        var act = () => CredentialReferenceSlug.Validate(new string('a', 65), "device-1");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Normalize_collapses_case_but_not_the_now_disallowed_separator_characters()
    {
        // rack1_sw and RACK1_SW are both valid under the charset and legitimately collide on case —
        // that residual ambiguity is caught by RackDefinitionValidation's configuration-wide check, not here.
        CredentialReferenceSlug.Normalize("rack1_sw").Should().Be(CredentialReferenceSlug.Normalize("RACK1_SW"));
    }
}
