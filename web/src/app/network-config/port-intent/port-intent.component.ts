// Routed at .../network-config/ports (story #168, AC2/AC3). Driven entirely by the discovered switch/
// port inventory (TopologyStateService.switches — the additive TopologyGraphDto.Switches field, loaded
// once via the existing topology graph load, NOT a second fetch path): a switch <select> (≤2 options,
// NFR1) plus a native port-grid table, one row per discovered port, showing the current intent state
// via a badge with an Edit action. No manual port/switch creation is possible — the grid only ever
// shows what discovery observed. Supports a `?switch=&port=` query param that auto-opens the editor
// pre-filtered (AC3 deep link from the topology drill-down).
import { Dialog } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import type { PortIntentEditorData, PortIntentEditorResult } from './port-intent-editor.component';
import { PortIntentEditorComponent } from './port-intent-editor.component';

@Component({
  selector: 'app-port-intent',
  standalone: true,
  imports: [StatusBadgeComponent],
  styleUrl: './port-intent.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="port-intent" role="main">
      <header class="port-intent__header">
        <h2>Port Intent</h2>
        @if (switches().length > 1) {
          <div class="port-intent__switch-select">
            <label for="port-intent-switch">Switch</label>
            <select
              id="port-intent-switch"
              [value]="selectedSwitchStableKey()"
              (change)="onSwitchChange($event)"
            >
              @for (switchNode of switches(); track switchNode.stableKey) {
                <option [value]="switchNode.stableKey">
                  {{ switchNode.name }} ({{ switchNode.serial ?? switchNode.stableKey }})
                </option>
              }
            </select>
          </div>
        }
      </header>

      @if (topologyState.loading() && switches().length === 0) {
        <p role="status">Loading discovered inventory…</p>
      } @else if (switches().length === 0) {
        <div class="port-intent__empty" role="status">
          <p>
            No switches or ports have been discovered for this rack yet. Run discovery before
            authoring port intent.
          </p>
        </div>
      } @else if (selectedSwitch(); as switchNode) {
        <div class="port-intent__table-wrapper">
          <table class="port-intent__table">
            <thead>
              <tr>
                <th scope="col">Port</th>
                <th scope="col">Current intent</th>
                @if (permission.canAuthorNetworkConfig()) {
                  <th scope="col">Actions</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (port of switchNode.ports; track port.stableKey) {
                <tr>
                  <td>
                    <span class="port-intent__identifier">{{ port.portName }}</span>
                  </td>
                  <td>
                    @if (vlanIdFor(switchNode.stableKey, port.portName); as vlanId) {
                      <app-status-badge
                        kind="intent-access"
                        [labelText]="'Access VLAN = ' + vlanId + ' (' + vlanNameFor(vlanId) + ')'"
                      />
                    } @else {
                      <app-status-badge kind="intent-inherit" />
                    }
                  </td>
                  @if (permission.canAuthorNetworkConfig()) {
                    <td>
                      <button
                        type="button"
                        (click)="onEditClick(switchNode.stableKey, port.portName)"
                      >
                        Edit
                      </button>
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
export class PortIntentComponent {
  protected readonly topologyState = inject(TopologyStateService);
  protected readonly state = inject(NetworkIntentStateService);
  protected readonly permission = inject(NetworkConfigPermissionService);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(Dialog);

  protected readonly switches = computed(() => this.topologyState.switches());
  private readonly _selectedSwitchStableKey = signal<string | null>(null);
  private readonly autoOpenedDeepLink = signal(false);

  protected readonly selectedSwitchStableKey = computed(
    () => this._selectedSwitchStableKey() ?? this.switches()[0]?.stableKey ?? '',
  );

  protected readonly selectedSwitch = computed(
    () => this.switches().find((s) => s.stableKey === this.selectedSwitchStableKey()) ?? null,
  );

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const rackId = params.get('rackId');
      if (rackId && rackId !== this.topologyState.rackId()) {
        this.autoOpenedDeepLink.set(false);
        this.topologyState.loadRackTopology(rackId);
      }
    });

    // AC3 deep link: once the inventory has loaded, auto-open the editor for a `?switch=&port=` pair
    // exactly once per navigation (guarded by autoOpenedDeepLink so a later, unrelated state change —
    // e.g. the drift overlay refreshing — never reopens it).
    effect(() => {
      if (
        this.autoOpenedDeepLink() ||
        this.switches().length === 0 ||
        !this.permission.canAuthorNetworkConfig()
      ) {
        return;
      }
      const query = this.route.snapshot.queryParamMap;
      const switchStableKey = query.get('switch');
      const portName = query.get('port');
      if (!switchStableKey || !portName) {
        return;
      }
      const port = this.switches()
        .find((s) => s.stableKey === switchStableKey)
        ?.ports.find((p) => p.portName === portName);
      if (!port) {
        return;
      }
      this.autoOpenedDeepLink.set(true);
      this._selectedSwitchStableKey.set(switchStableKey);
      this.openEditor(switchStableKey, portName);
    });
  }

  protected vlanIdFor(switchStableKey: string, portName: string): number | null {
    return this.state.portIntentFor(switchStableKey, portName)?.accessVlanId ?? null;
  }

  protected vlanNameFor(vlanId: number): string {
    return this.state.vlanCatalogue().find((v) => v.id === vlanId)?.name ?? `#${vlanId}`;
  }

  protected onSwitchChange(event: Event): void {
    this._selectedSwitchStableKey.set((event.target as HTMLSelectElement).value);
  }

  protected onEditClick(switchStableKey: string, portName: string): void {
    this.openEditor(switchStableKey, portName);
  }

  private openEditor(switchStableKey: string, portName: string): void {
    const ref = this.dialog.open<PortIntentEditorResult, PortIntentEditorData>(
      PortIntentEditorComponent,
      {
        data: {
          switchStableKey,
          portName,
          currentVlanId: this.vlanIdFor(switchStableKey, portName),
          catalogue: this.state.vlanCatalogue(),
        },
        ariaLabelledBy: 'port-intent-editor-heading',
        hasBackdrop: true,
        backdropClass: 'cds-overlay-backdrop',
        ariaModal: true,
      },
    );
    ref.closed.subscribe((result) => {
      if (!result) {
        return;
      }
      if (result.accessVlanId === null) {
        this.state.clearPortIntent(switchStableKey, portName);
      } else {
        this.state.setPortIntent(switchStableKey, portName, result.accessVlanId);
      }
    });
  }
}
