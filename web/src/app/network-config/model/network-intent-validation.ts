// Client-side mirror of Caisson.Domain.NetworkConfig.NetworkIntentValidator.Validate (story #168,
// NFR5: "no duplicated validation logic across screens") — the VLAN Catalogue and Port Intent screens
// both call this single function for immediate inline errors; the server re-runs the authoritative
// C# version on every PUT/validate call regardless, so this is a UX convenience, never the source of
// truth.
import {
  NETWORK_INTENT_MAX_DESCRIPTION_LENGTH,
  NETWORK_INTENT_MAX_VLAN,
  NETWORK_INTENT_MAX_VLAN_NAME_LENGTH,
  NETWORK_INTENT_MIN_VLAN,
} from './network-intent-bounds';
import type { PortAccessIntentDto, VlanCatalogueEntryDto } from './network-intent-contracts';

export interface NetworkIntentFieldError {
  field: string;
  message: string;
}

/** Mirrors NetworkIntentValidator.Validate field-for-field, including the "block deletion of a VLAN
 * still referenced by a port intent" rule falling out of the same "port intent VLAN must exist in the
 * catalogue" check (there is no separate referenced-VLAN pass to keep in sync). */
export function validateNetworkIntent(
  vlanCatalogue: readonly VlanCatalogueEntryDto[],
  portIntents: readonly PortAccessIntentDto[],
): NetworkIntentFieldError[] {
  const errors: NetworkIntentFieldError[] = [];
  const catalogueIds = new Set<number>();

  vlanCatalogue.forEach((entry, i) => {
    const idField = `vlanCatalogue[${i}].id`;
    const nameField = `vlanCatalogue[${i}].name`;
    const descriptionField = `vlanCatalogue[${i}].description`;

    if (entry.id < NETWORK_INTENT_MIN_VLAN || entry.id > NETWORK_INTENT_MAX_VLAN) {
      errors.push({
        field: idField,
        message: `VLAN ID ${entry.id} is out of range [${NETWORK_INTENT_MIN_VLAN}, ${NETWORK_INTENT_MAX_VLAN}].`,
      });
    } else if (catalogueIds.has(entry.id)) {
      errors.push({ field: idField, message: `VLAN ID ${entry.id} already exists in this rack.` });
    } else {
      catalogueIds.add(entry.id);
    }

    if (!entry.name || entry.name.trim().length === 0) {
      errors.push({ field: nameField, message: 'VLAN name is required.' });
    } else if (entry.name.length > NETWORK_INTENT_MAX_VLAN_NAME_LENGTH) {
      errors.push({
        field: nameField,
        message: `VLAN name exceeds the ${NETWORK_INTENT_MAX_VLAN_NAME_LENGTH}-character bound.`,
      });
    }

    if (entry.description && entry.description.length > NETWORK_INTENT_MAX_DESCRIPTION_LENGTH) {
      errors.push({
        field: descriptionField,
        message: `Description exceeds the ${NETWORK_INTENT_MAX_DESCRIPTION_LENGTH}-character bound.`,
      });
    }
  });

  portIntents.forEach((intent, i) => {
    const switchField = `portIntents[${i}].switchStableKey`;
    const portField = `portIntents[${i}].portName`;
    const vlanField = `portIntents[${i}].accessVlanId`;

    if (!intent.switchStableKey) {
      errors.push({ field: switchField, message: 'switchStableKey is required.' });
    }
    if (!intent.portName) {
      errors.push({ field: portField, message: 'portName is required.' });
    }
    if (intent.accessVlanId !== null && !catalogueIds.has(intent.accessVlanId)) {
      errors.push({
        field: vlanField,
        message: `VLAN ${intent.accessVlanId} does not exist in this rack's VLAN catalogue.`,
      });
    }
  });

  return errors;
}

/** Whether a VLAN id is still referenced by any port intent — the client-immediacy half of "block
 * deletion of a VLAN still referenced by a port intent" (AC1/Q2); the server enforces the same rule
 * authoritatively via validateNetworkIntent/NetworkIntentValidator on save. */
export function isVlanReferencedByPortIntent(
  vlanId: number,
  portIntents: readonly PortAccessIntentDto[],
): boolean {
  return portIntents.some((intent) => intent.accessVlanId === vlanId);
}
