using Caisson.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Caisson.Infrastructure.Persistence.Conversions;

/// <summary>
/// Converts <see cref="MacAddressValue"/> to/from its normalized string form for storage. The stored
/// value is always the canonical lowercase 12-hex representation, so reading back via
/// <see cref="MacAddressValue.Parse"/> is loss-free.
/// </summary>
public sealed class MacAddressValueConverter : ValueConverter<MacAddressValue, string>
{
    public MacAddressValueConverter()
        : base(
            mac => mac.Value,
            stored => MacAddressValue.Parse(stored))
    {
    }
}
