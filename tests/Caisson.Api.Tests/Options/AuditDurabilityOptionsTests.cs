using System.ComponentModel.DataAnnotations;
using Caisson.Api.Options;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.Tests.Options;

/// <summary>
/// Proves <see cref="AuditDurabilityOptions"/> has safe defaults and rejects non-positive values (story
/// #308 step 1 acceptance criterion) via the same <c>ValidateDataAnnotations</c> mechanism
/// <c>Program.cs</c> wires up with <c>ValidateOnStart()</c>.
/// </summary>
public sealed class AuditDurabilityOptionsTests
{
    [Fact]
    public void Defaults_are_all_valid()
    {
        var options = new AuditDurabilityOptions();

        Validate(options).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(AuditDurabilityOptions.OutboxPollIntervalSeconds), 0)]
    [InlineData(nameof(AuditDurabilityOptions.OutboxBatchSize), 0)]
    [InlineData(nameof(AuditDurabilityOptions.OutboxLeaseSeconds), -1)]
    [InlineData(nameof(AuditDurabilityOptions.OutboxMaxAttempts), 0)]
    [InlineData(nameof(AuditDurabilityOptions.OutboxRetryBaseDelaySeconds), -5)]
    [InlineData(nameof(AuditDurabilityOptions.OutboxRetryMaxDelaySeconds), 0)]
    [InlineData(nameof(AuditDurabilityOptions.DenialFirstN), 0)]
    [InlineData(nameof(AuditDurabilityOptions.DenialWindowSeconds), -1)]
    [InlineData(nameof(AuditDurabilityOptions.DenialFlushIntervalSeconds), 0)]
    [InlineData(nameof(AuditDurabilityOptions.DenialMaxActiveBuckets), 0)]
    public void Non_positive_values_fail_validation(string propertyName, int invalidValue)
    {
        var options = new AuditDurabilityOptions();
        typeof(AuditDurabilityOptions).GetProperty(propertyName)!.SetValue(options, invalidValue);

        var results = Validate(options);

        results.Should().Contain(r => r.MemberNames.Contains(propertyName));
    }

    private static List<ValidationResult> Validate(AuditDurabilityOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
