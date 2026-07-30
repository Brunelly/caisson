// The VLAN catalogue create/edit form (story #168, AC1) — a CDK Dialog panel (ADR 0034), mirroring
// apply-confirmation-dialog.component.ts's Dialog.open()/DialogRef<Result> pattern. Cancel/Escape/
// backdrop-click all resolve `closed` with `undefined`; only Submit (gated by inline validation) closes
// with the validated entry. VLAN id is editable only when creating — editing a saved entry's id would
// silently create a different catalogue entry rather than updating the intended one, so it is fixed
// (shown read-only) once an entry exists.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  NETWORK_INTENT_MAX_DESCRIPTION_LENGTH,
  NETWORK_INTENT_MAX_VLAN,
  NETWORK_INTENT_MAX_VLAN_NAME_LENGTH,
  NETWORK_INTENT_MIN_VLAN,
} from '../model/network-intent-bounds';
import type { VlanCatalogueEntryDto } from '../model/network-intent-contracts';

export interface VlanFormDialogData {
  mode: 'create' | 'edit';
  entry: VlanCatalogueEntryDto | null;
  /** Every VLAN id already in the catalogue, EXCLUDING the entry being edited (so editing without
   * changing the id never self-flags as a duplicate). */
  existingIds: readonly number[];
}

export type VlanFormDialogResult = VlanCatalogueEntryDto | undefined;

interface VlanFormErrors {
  id: string | null;
  name: string | null;
  description: string | null;
}

const NO_ERRORS: VlanFormErrors = { id: null, name: null, description: null };

@Component({
  selector: 'app-vlan-form-dialog',
  standalone: true,
  styleUrl: './vlan-form-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="vlan-dialog" (submit)="onSubmit($event)">
      <h2 id="vlan-dialog-heading" class="vlan-dialog__heading">
        {{ data.mode === 'create' ? 'Add VLAN' : 'Edit VLAN' }}
      </h2>

      <div class="vlan-dialog__field">
        <label for="vlan-dialog-id">VLAN ID</label>
        <input
          id="vlan-dialog-id"
          type="number"
          [min]="minVlan"
          [max]="maxVlan"
          [value]="id()"
          [readonly]="data.mode === 'edit'"
          [attr.aria-invalid]="errors().id ? 'true' : null"
          [attr.aria-describedby]="errors().id ? 'vlan-dialog-id-error' : null"
          (input)="onIdInput($event)"
        />
        @if (errors().id; as message) {
          <p id="vlan-dialog-id-error" class="vlan-dialog__error" role="alert">{{ message }}</p>
        }
      </div>

      <div class="vlan-dialog__field">
        <label for="vlan-dialog-name">Name</label>
        <input
          id="vlan-dialog-name"
          type="text"
          [value]="name()"
          [attr.aria-invalid]="errors().name ? 'true' : null"
          [attr.aria-describedby]="errors().name ? 'vlan-dialog-name-error' : null"
          (input)="onNameInput($event)"
        />
        @if (errors().name; as message) {
          <p id="vlan-dialog-name-error" class="vlan-dialog__error" role="alert">{{ message }}</p>
        }
      </div>

      <div class="vlan-dialog__field">
        <label for="vlan-dialog-description">Description (optional)</label>
        <input
          id="vlan-dialog-description"
          type="text"
          [value]="description()"
          [attr.aria-invalid]="errors().description ? 'true' : null"
          [attr.aria-describedby]="errors().description ? 'vlan-dialog-description-error' : null"
          (input)="onDescriptionInput($event)"
        />
        @if (errors().description; as message) {
          <p id="vlan-dialog-description-error" class="vlan-dialog__error" role="alert">
            {{ message }}
          </p>
        }
      </div>

      <div class="vlan-dialog__actions">
        <button type="button" class="vlan-dialog__cancel" (click)="cancel()">Cancel</button>
        <button type="submit" class="vlan-dialog__submit">
          {{ data.mode === 'create' ? 'Add VLAN' : 'Save changes' }}
        </button>
      </div>
    </form>
  `,
})
export class VlanFormDialogComponent {
  protected readonly data = inject<VlanFormDialogData>(DIALOG_DATA);
  private readonly dialogRef = inject(DialogRef<VlanFormDialogResult>);

  protected readonly minVlan = NETWORK_INTENT_MIN_VLAN;
  protected readonly maxVlan = NETWORK_INTENT_MAX_VLAN;

  protected readonly id = signal(this.data.entry?.id ?? this.minVlan);
  protected readonly name = signal(this.data.entry?.name ?? '');
  protected readonly description = signal(this.data.entry?.description ?? '');
  protected readonly errors = signal<VlanFormErrors>(NO_ERRORS);

  protected onIdInput(event: Event): void {
    this.id.set(Number((event.target as HTMLInputElement).value));
  }

  protected onNameInput(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
  }

  protected onDescriptionInput(event: Event): void {
    this.description.set((event.target as HTMLInputElement).value);
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    const errors = this.validate();
    this.errors.set(errors);
    if (errors.id || errors.name || errors.description) {
      return;
    }

    const description = this.description().trim();
    this.dialogRef.close({
      id: this.id(),
      name: this.name().trim(),
      description: description.length > 0 ? description : null,
    });
  }

  private validate(): VlanFormErrors {
    const id = this.id();
    const name = this.name().trim();
    const description = this.description().trim();

    let idError: string | null = null;
    if (!Number.isInteger(id) || id < this.minVlan || id > this.maxVlan) {
      idError = `VLAN ID must be an integer between ${this.minVlan} and ${this.maxVlan}.`;
    } else if (this.data.existingIds.includes(id)) {
      idError = `VLAN ID ${id} already exists in this rack.`;
    }

    let nameError: string | null = null;
    if (name.length === 0) {
      nameError = 'VLAN name is required.';
    } else if (name.length > NETWORK_INTENT_MAX_VLAN_NAME_LENGTH) {
      nameError = `VLAN name exceeds the ${NETWORK_INTENT_MAX_VLAN_NAME_LENGTH}-character bound.`;
    }

    const descriptionError =
      description.length > NETWORK_INTENT_MAX_DESCRIPTION_LENGTH
        ? `Description exceeds the ${NETWORK_INTENT_MAX_DESCRIPTION_LENGTH}-character bound.`
        : null;

    return { id: idError, name: nameError, description: descriptionError };
  }
}
