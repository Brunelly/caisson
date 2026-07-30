// The per-port access-VLAN intent editor (story #168, AC2) — a CDK Dialog panel (ADR 0034: focus-trap,
// role="dialog"/aria-modal, Escape-to-close, backdrop, focus-restore all come from the shared Dialog
// primitive, mirroring apply-confirmation-dialog.component.ts). Contains exactly one control: a native
// <select> styled via cds-form-input whose first option is "Unchanged / Inherit" followed by every
// catalogue VLAN. Because the option list IS the catalogue, selecting a non-catalogue VLAN is
// structurally impossible (AC2) — the native select also gives the full interactive/accessibility
// baseline (close on select/outside-click/Escape, keyboard nav, native listbox ARIA, theme-safe states)
// for free.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import type { VlanCatalogueEntryDto } from '../model/network-intent-contracts';

const INHERIT_VALUE = 'inherit';

export interface PortIntentEditorData {
  switchStableKey: string;
  portName: string;
  currentVlanId: number | null;
  catalogue: readonly VlanCatalogueEntryDto[];
}

export type PortIntentEditorResult = { accessVlanId: number | null } | undefined;

@Component({
  selector: 'app-port-intent-editor',
  standalone: true,
  styleUrl: './port-intent-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="port-intent-editor">
      <h2 id="port-intent-editor-heading" class="port-intent-editor__heading">
        {{ data.switchStableKey }} / {{ data.portName }}
      </h2>

      <div class="port-intent-editor__field">
        <label for="port-intent-editor-select">Access VLAN intent</label>
        <select id="port-intent-editor-select" (change)="onSelectChange($event)">
          <option [value]="inheritValue" [selected]="isSelected(inheritValue)">
            Unchanged / Inherit
          </option>
          @for (vlan of data.catalogue; track vlan.id) {
            <option [value]="vlan.id" [selected]="isSelected(vlan.id + '')">
              VLAN {{ vlan.id }} — {{ vlan.name }}
            </option>
          }
        </select>
      </div>

      <div class="port-intent-editor__actions">
        <button type="button" class="port-intent-editor__cancel" (click)="cancel()">Cancel</button>
        <button type="button" class="port-intent-editor__apply" (click)="apply()">Apply</button>
      </div>
    </div>
  `,
})
export class PortIntentEditorComponent {
  protected readonly data = inject<PortIntentEditorData>(DIALOG_DATA);
  private readonly dialogRef = inject(DialogRef<PortIntentEditorResult>);

  protected readonly inheritValue = INHERIT_VALUE;
  protected readonly selectedValue = signal(
    this.data.currentVlanId === null ? INHERIT_VALUE : String(this.data.currentVlanId),
  );

  protected onSelectChange(event: Event): void {
    this.selectedValue.set((event.target as HTMLSelectElement).value);
  }

  /** Per-option `[selected]` (rather than relying solely on the parent `<select>`'s own `[value]`
   * binding) so the initially-current VLAN is reliably pre-selected regardless of DOM creation order:
   * Angular's control-flow (`@for`) options are inserted as a separate, later update than the `<select>`
   * element's own bindings, so setting `[value]` on the select alone can race the browser's native
   * value-matching algorithm against option elements that don't exist yet at that instant — some engines
   * silently self-heal once the options DO exist, but the WHATWG spec text for the `value` IDL setter
   * does not guarantee that reconciliation, so it cannot be relied on across browsers. */
  protected isSelected(value: string): boolean {
    return this.selectedValue() === value;
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }

  protected apply(): void {
    const value = this.selectedValue();
    this.dialogRef.close({ accessVlanId: value === INHERIT_VALUE ? null : Number(value) });
  }
}
