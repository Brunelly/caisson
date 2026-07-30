// Single source of truth for the network-intent authoring bounds on the client, mirroring
// Caisson.Domain.DesiredState.DesiredStateSchema (MinVlan/MaxVlan/MaxDescriptionLength) and
// Caisson.Domain.NetworkConfig.NetworkIntentValidator.MaxVlanNameLength exactly (story #168, NFR5) —
// every VLAN-catalogue/port-intent form in this feature reads these constants rather than re-declaring
// its own magic numbers, so client-side and server-side validation can never silently disagree.

/** Mirrors DesiredStateSchema.MinVlan. */
export const NETWORK_INTENT_MIN_VLAN = 1;

/** Mirrors DesiredStateSchema.MaxVlan. */
export const NETWORK_INTENT_MAX_VLAN = 4094;

/** Mirrors NetworkIntentValidator.MaxVlanNameLength (itself DesiredStateSchema.MaxSwitchNameLength). */
export const NETWORK_INTENT_MAX_VLAN_NAME_LENGTH = 64;

/** Mirrors DesiredStateSchema.MaxDescriptionLength. */
export const NETWORK_INTENT_MAX_DESCRIPTION_LENGTH = 256;
