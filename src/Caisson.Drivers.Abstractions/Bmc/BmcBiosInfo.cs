namespace Caisson.Drivers.Abstractions.Bmc;

/// <summary>BIOS/firmware information observed for a server via its BMC.</summary>
/// <param name="Vendor">Observed BIOS vendor, if known.</param>
/// <param name="Version">Observed BIOS version string, if known.</param>
/// <param name="ReleaseDate">Observed BIOS release date, if known.</param>
public sealed record BmcBiosInfo(string? Vendor = null, string? Version = null, DateOnly? ReleaseDate = null);
