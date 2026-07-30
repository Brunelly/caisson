// App chrome top bar (Story #119 Task #127, per the RackSelectorTopBar design). The rack "selector" is
// a READ-ONLY chip, not a dropdown — there is no rack-listing API (`app.routes.ts` documents rack
// selection/listing as out of scope), so a fake dropdown here would be a defect, not a simplification.
// Hosts the theme toggle (top-right, per the story's resolved Q&A) and the live-connection badge.
//
// Story #123 Task #140 / ADR 0043: below `md` the top bar also hosts the hamburger that opens
// `SidebarNavigationComponent` in a CDK Dialog drawer (`nav-drawer.component.ts`) — the static in-flow
// sidebar column is hidden at that width (app-shell.component.scss). `@angular/cdk/dialog` and
// `nav-drawer.component.ts` are dynamically `import()`ed from `openNavDrawer` rather than statically, so
// their ~20kB (gzipped) of Overlay/Dialog/a11y/portal module code lands in its own lazy chunk instead of
// this component's eager one — `RackSelectorTopBarComponent` renders on every route via `AppShellComponent`,
// so a static import here would tax every desktop visitor for a control most of them never trigger,
// and pushed the initial bundle over its budget when tried (see ADR 0043's Consequences).
import {
  ChangeDetectionStrategy,
  Component,
  EnvironmentInjector,
  inject,
  input,
} from '@angular/core';
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
      <button
        type="button"
        class="topbar__hamburger"
        aria-label="Open navigation"
        aria-haspopup="dialog"
        (click)="openNavDrawer()"
      >
        <svg
          width="18"
          height="18"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M4 6h16M4 12h16M4 18h16" />
        </svg>
      </button>

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
  private readonly injector = inject(EnvironmentInjector);

  protected async openNavDrawer(): Promise<void> {
    const [{ Dialog }, { NavDrawerComponent }] = await Promise.all([
      import('@angular/cdk/dialog'),
      import('../sidebar-navigation/nav-drawer.component'),
    ]);

    this.injector.get(Dialog).open(NavDrawerComponent, {
      data: { rackId: this.rackId() },
      panelClass: 'cds-nav-drawer-panel',
      hasBackdrop: true,
      // Task #131 convention: DS-tokened scrim instead of CDK's un-themed default backdrop.
      backdropClass: 'cds-overlay-backdrop',
      ariaModal: true,
    });
  }
}
