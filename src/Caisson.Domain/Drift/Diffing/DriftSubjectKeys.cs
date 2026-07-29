using Caisson.Domain.Topology.Diffing;

namespace Caisson.Domain.Drift.Diffing;

/// <summary>
/// The canonical, versioned subject-key definitions for drift items (story #64, the story's answered
/// canonical-key question). Deliberately a NEW scheme — sibling to, not a reuse of,
/// <c>Topology.Diffing.StableKeys</c>: the persisted desired-side key
/// (<c>DesiredPortIntent.StableKey</c>, <c>"{rackSlug}|{switchName}|{portName}"</c> hashed against the
/// git-ingested rack slug) and the observed-side key (<c>StableKeys.ForSwitchPort</c>, keyed off the
/// trusted device key + serial/IP) are not string-comparable — drift joins the two sides on natural
/// attributes instead (<c>DriftEngine</c>), then re-keys the result under this scheme so a schema change
/// on either upstream side never silently reshapes a persisted drift subject key.
/// </summary>
/// <remarks>
/// Every key is prefixed with a literal schema version segment (<c>"v1"</c>) so a future revision of the
/// key format (e.g. adding a segment) can be introduced without colliding with keys already persisted
/// under the old format. Each free-form segment is escaped via
/// <see cref="StableKeys.EscapeSegment(string)"/> — the same <c>|</c>/<c>%</c> percent-encoding defence
/// used for observed-state stable keys — so a device- or git-controlled value containing the literal
/// separator can never make two different subjects collide onto the same key (finding #3's precedent).
/// </remarks>
public static class DriftSubjectKeys
{
    private const string SchemaVersion = "v1";

    /// <summary>Versioned subject key for a switch port: <c>v1|{rackKey}|{switchName}|{portName}</c>.</summary>
    public static string ForSwitchPort(string rackKey, string switchName, string portName)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackKey);
        ArgumentException.ThrowIfNullOrEmpty(switchName);
        ArgumentException.ThrowIfNullOrEmpty(portName);

        return string.Join(
            "|",
            SchemaVersion,
            StableKeys.EscapeSegment(rackKey),
            StableKeys.EscapeSegment(switchName),
            StableKeys.EscapeSegment(portName));
    }

    /// <summary>Versioned subject key for a server NIC: <c>v1|{rackKey}|{nicMac}</c>.</summary>
    public static string ForServerNic(string rackKey, string nicMac)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackKey);
        ArgumentException.ThrowIfNullOrEmpty(nicMac);

        return string.Join("|", SchemaVersion, StableKeys.EscapeSegment(rackKey), StableKeys.EscapeSegment(nicMac));
    }
}
