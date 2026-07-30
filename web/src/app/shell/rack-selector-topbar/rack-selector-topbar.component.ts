// App chrome top bar (Story #119 Task #127, per the RackSelectorTopBar design). The rack "selector" is
// a READ-ONLY chip, not a dropdown — there is no rack-listing API (`app.routes.ts` documents rack
// selection/listing as out of scope), so a fake dropdown here would be a defect, not a simplification.
// Hosts the theme toggle (top-right, per the story's resolved Q&A) and the live-connection badge.
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { LiveConnectionStatusBarComponent } from '../../shared/connection-status/live-connection-status-bar.component';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { ThemeToggleComponent } from '../theme-toggle/theme-toggle.component';

@Component({
  selector: 'app-rack-selector-topbar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LiveConnectionStatusBarComponent, ThemeToggleComponent],
  styleUrl: './rack-selector-topbar.component.scss',
  template: `
    <header class="topbar" role="banner">
      <div class="topbar__rack" title="Current rack">
        @if (rackId(); as id) {
          <span class="topbar__rack-label">RACK</span>
          <span class="topbar__rack-id">{{ id }}</span>
        } @else {
          <span class="topbar__rack-label">No rack selected</span>
        }
      </div>

      <div class="topbar__right">
        <app-live-connection-status-bar
          variant="badge"
          [status]="topologyState.connectionStatus()"
        />
        <app-theme-toggle />
      </div>
    </header>
  `,
})
export class RackSelectorTopBarComponent {
  readonly rackId = input<string | null>(null);

  protected readonly topologyState = inject(TopologyStateService);
}
