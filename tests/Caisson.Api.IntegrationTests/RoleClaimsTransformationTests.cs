using System.Security.Claims;
using Caisson.Api.Security;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #17: the roles claim (app-role, admin-controlled) and the groups claim (directory
/// membership) are not the same trust class — a canonical role name is only ever accepted verbatim from
/// the roles claim; the groups claim must resolve through the reviewed mappings with no passthrough.
/// </summary>
public sealed class RoleClaimsTransformationTests
{
    [Fact]
    public async Task A_canonical_role_name_in_the_roles_claim_is_accepted_directly()
    {
        var transform = new RoleClaimsTransformation(new Dictionary<string, string>());
        var principal = PrincipalWith((RoleClaimsTransformation.RoleClaimType, CaissonRoles.Admin));

        var result = await transform.TransformAsync(principal);

        result.IsInRole(CaissonRoles.Admin).Should().BeTrue();
    }

    [Fact]
    public async Task A_groups_claim_value_that_happens_to_literally_match_a_canonical_role_grants_nothing()
    {
        // The exact scenario finding #17 closes: an IdP emitting a directory group literally named
        // "Admin" must not grant Admin on its own passthrough.
        var transform = new RoleClaimsTransformation(new Dictionary<string, string>());
        var principal = PrincipalWith((RoleClaimsTransformation.GroupsClaimType, CaissonRoles.Admin));

        var result = await transform.TransformAsync(principal);

        result.IsInRole(CaissonRoles.Admin).Should().BeFalse();
    }

    [Fact]
    public async Task A_mapped_groups_claim_value_grants_the_mapped_role()
    {
        var mappings = new Dictionary<string, string> { ["11111111-2222-3333-4444-555555555555"] = CaissonRoles.Operator };
        var transform = new RoleClaimsTransformation(mappings);
        var principal = PrincipalWith((RoleClaimsTransformation.GroupsClaimType, "11111111-2222-3333-4444-555555555555"));

        var result = await transform.TransformAsync(principal);

        result.IsInRole(CaissonRoles.Operator).Should().BeTrue();
    }

    [Fact]
    public async Task An_unmapped_groups_claim_value_grants_nothing()
    {
        var transform = new RoleClaimsTransformation(new Dictionary<string, string>());
        var principal = PrincipalWith((RoleClaimsTransformation.GroupsClaimType, "some-unmapped-group-id"));

        var result = await transform.TransformAsync(principal);

        CaissonRoles.All.Should().OnlyContain(role => !result.IsInRole(role));
    }

    [Fact]
    public void ValidateMappings_throws_when_a_mapping_targets_a_non_canonical_role()
    {
        var mappings = new Dictionary<string, string> { ["group-1"] = "Admni" }; // typo
        var environment = new TestHostEnvironment("Production");

        var act = () => RoleClaimsTransformation.ValidateMappings(environment, mappings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Admni*");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ValidateMappings_throws_when_empty_outside_development_or_testing(string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);

        var act = () => RoleClaimsTransformation.ValidateMappings(environment, new Dictionary<string, string>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*RoleMappings*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void ValidateMappings_allows_empty_under_development_or_testing(string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);

        var act = () => RoleClaimsTransformation.ValidateMappings(environment, new Dictionary<string, string>());

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateMappings_does_not_throw_for_a_fully_canonical_non_empty_map()
    {
        var mappings = new Dictionary<string, string> { ["group-1"] = CaissonRoles.Operator, ["group-2"] = CaissonRoles.ReadOnly };
        var environment = new TestHostEnvironment("Production");

        var act = () => RoleClaimsTransformation.ValidateMappings(environment, mappings);

        act.Should().NotThrow();
    }

    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
    {
        // roleType must match RoleClaimsTransformation.RoleClaimType so ClaimsPrincipal.IsInRole (which
        // checks the identity's own RoleClaimType, ClaimTypes.Role by default) looks at the same claim
        // type the transform actually writes canonical roles onto.
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test",
            nameType: ClaimsIdentity.DefaultNameClaimType, roleType: RoleClaimsTransformation.RoleClaimType);
        return new ClaimsPrincipal(identity);
    }
}
