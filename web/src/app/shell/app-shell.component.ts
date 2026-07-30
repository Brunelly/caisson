// Root layout shell (Story #119 Task #127, per the CaissonAppShell design): frame, sidebar, top bar,
// background mesh, the 3 elevation tiers, and the toast outlet, wrapping every routed page. `app.ts`
// renders this instead of a bare `<router-outlet/>` (see app.html) — a deliberate choice over
// restructuring `app.routes.ts` into nested children, so routing/guards/API calls are completely
// unchanged (AC5) while every route now renders inside the chrome.
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { ToastOutletComponent } from '../shared/toast/toast-outlet.component';
import { RackSelectorTopBarComponent } from './rack-selector-topbar/rack-selector-topbar.component';
import { SidebarNavigationComponent } from './sidebar-navigation/sidebar-navigation.component';

@Component({
  selector: 'app-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    SidebarNavigationComponent,
    RackSelectorTopBarComponent,
    ToastOutletComponent,
  ],
  styleUrl: './app-shell.component.scss',
  template: `
    <div class="shell">
      <div class="shell__mesh" aria-hidden="true"></div>

      <app-sidebar-navigation class="shell__sidebar" [rackId]="rackId()" />

      <div class="shell__main">
        <app-rack-selector-topbar [rackId]="rackId()" />
        <div class="shell__content">
          <router-outlet />
        </div>
      </div>

      <app-toast-outlet />
    </div>
  `,
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly rootRoute = inject(ActivatedRoute);

  /** `:rackId` lives on the routed (leaf) page's own route, not necessarily the shell's root route —
   * walk the activated-route tree on every completed navigation to find it, reactively, as a signal. */
  protected readonly rackId = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.extractRackId()),
      startWith(this.extractRackId()),
    ),
    { initialValue: null },
  );

  private extractRackId(): string | null {
    let current: ActivatedRoute | null = this.rootRoute;
    let rackId: string | null = null;
    while (current) {
      rackId = current.snapshot.paramMap.get('rackId') ?? rackId;
      current = current.firstChild;
    }
    return rackId;
  }
}
