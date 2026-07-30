// Routed at .../network-config/vlans (story #168, AC1). A DS data-grid (native <table> in a
// cds-elevated-card wrapper), structurally mirroring drift-reports-list.component.ts. Create/edit use a
// CDK Dialog form (vlan-form-dialog.component.ts); mutations update NetworkIntentStateService's signals
// immediately — nothing reaches the server until the shell's persistent Save action runs. Mutating
// controls (Add/Edit/Retire) are entirely ABSENT — not merely disabled — for a principal lacking the
// NetworkConfigAuthor permission, matching apply-action.component.ts's gating style; the grid itself
// stays visible and read-only for a Read Only user.
import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { isVlanReferencedByPortIntent } from '../model/network-intent-validation';
import type { VlanCatalogueEntryDto } from '../model/network-intent-contracts';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import type { VlanFormDialogData, VlanFormDialogResult } from './vlan-form-dialog.component';
import { VlanFormDialogComponent } from './vlan-form-dialog.component';

@Component({
  selector: 'app-vlan-catalogue',
  standalone: true,
  styleUrl: './vlan-catalogue.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="vlan-catalogue" role="main">
      <header class="vlan-catalogue__header">
        <h2>VLAN Catalogue</h2>
        @if (permission.canAuthorNetworkConfig()) {
          <button type="button" class="vlan-catalogue__add" (click)="onAddClick()">Add VLAN</button>
        }
      </header>

      @if (serverErrors().length > 0) {
        <div class="vlan-catalogue__server-errors" role="alert">
          <p>The server rejected the last save:</p>
          <ul>
            @for (error of serverErrors(); track error.field) {
              <li>{{ error.messages.join(' ') }}</li>
            }
          </ul>
        </div>
      }

      @if (state.loading() && state.vlanCatalogue().length === 0) {
        <p role="status">Loading VLAN catalogue…</p>
      } @else if (state.loadError()) {
        <p role="alert">
          Something went wrong loading this rack's network intent. Try again shortly.
        </p>
      } @else if (state.vlanCatalogue().length === 0) {
        <p role="status">No VLANs defined for this rack yet.</p>
      } @else {
        <div class="vlan-catalogue__table-wrapper">
          <table class="vlan-catalogue__table">
            <thead>
              <tr>
                <th scope="col">VLAN ID</th>
                <th scope="col">Name</th>
                <th scope="col">Description</th>
                @if (permission.canAuthorNetworkConfig()) {
                  <th scope="col">Actions</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (entry of state.vlanCatalogue(); track entry.id) {
                <tr>
                  <td>
                    <span class="vlan-catalogue__identifier">{{ entry.id }}</span>
                  </td>
                  <td>{{ entry.name }}</td>
                  <td>{{ entry.description ?? '—' }}</td>
                  @if (permission.canAuthorNetworkConfig()) {
                    <td class="vlan-catalogue__row-actions">
                      <button type="button" (click)="onEditClick(entry)">Edit</button>
                      <button type="button" (click)="onRetireClick(entry)">Retire</button>
                      @if (retireBlockedId() === entry.id) {
                        <p class="vlan-catalogue__retire-blocked" role="alert">
                          VLAN {{ entry.id }} is still referenced by a port intent — clear that
                          intent first.
                        </p>
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class VlanCatalogueComponent {
  protected readonly state = inject(NetworkIntentStateService);
  protected readonly permission = inject(NetworkConfigPermissionService);
  private readonly dialog = inject(Dialog);

  protected readonly retireBlockedId = signal<number | null>(null);

  protected readonly serverErrors = computed(() =>
    this.state.fieldErrors().filter((error) => error.field.startsWith('vlanCatalogue')),
  );

  protected onAddClick(): void {
    const ref = this.dialog.open<VlanFormDialogResult, VlanFormDialogData>(
      VlanFormDialogComponent,
      {
        data: {
          mode: 'create',
          entry: null,
          existingIds: this.state.vlanCatalogue().map((entry) => entry.id),
        },
        ariaLabelledBy: 'vlan-dialog-heading',
        hasBackdrop: true,
        backdropClass: 'cds-overlay-backdrop',
        ariaModal: true,
      },
    );
    ref.closed.subscribe((result) => {
      if (result) {
        this.state.addVlan(result);
      }
    });
  }

  protected onEditClick(entry: VlanCatalogueEntryDto): void {
    const existingIds = this.state
      .vlanCatalogue()
      .map((v) => v.id)
      .filter((id) => id !== entry.id);
    const ref = this.dialog.open<VlanFormDialogResult, VlanFormDialogData>(
      VlanFormDialogComponent,
      {
        data: { mode: 'edit', entry, existingIds },
        ariaLabelledBy: 'vlan-dialog-heading',
        hasBackdrop: true,
        backdropClass: 'cds-overlay-backdrop',
        ariaModal: true,
      },
    );
    ref.closed.subscribe((result) => {
      if (result) {
        this.state.updateVlan(entry.id, result);
      }
    });
  }

  protected onRetireClick(entry: VlanCatalogueEntryDto): void {
    if (isVlanReferencedByPortIntent(entry.id, this.state.portIntents())) {
      this.retireBlockedId.set(entry.id);
      return;
    }
    this.retireBlockedId.set(null);
    this.state.retireVlan(entry.id);
  }
}
