// Story #123 Task #140 / ADR 0043: mobile nav drawer content, opened via `@angular/cdk/dialog` from
// rack-selector-topbar.component.ts's hamburger button. Deliberately a thin wrapper around the EXISTING
// `SidebarNavigationComponent` (unchanged, "verbatim" per the plan) rather than a second nav-item list —
// `rackId` arrives through `DIALOG_DATA` (CDK Dialog does not bind a dynamically-opened component's own
// `@Input()`/`input()`s, only `apply-confirmation-dialog.component.ts`'s established `DIALOG_DATA`
// pattern), and the drawer closes itself on the next completed navigation so selecting Topology/Drift
// dismisses the drawer without any change to the sidebar's own routerLink markup.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import { SidebarNavigationComponent } from './sidebar-navigation.component';

export interface NavDrawerData {
  rackId: string | null;
}

@Component({
  selector: 'app-nav-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SidebarNavigationComponent],
  styleUrl: './nav-drawer.component.scss',
  template: `
    <div class="nav-drawer">
      <button
        type="button"
        class="nav-drawer__close"
        aria-label="Close navigation"
        (click)="dialogRef.close()"
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M18 6 6 18" />
          <path d="m6 6 12 12" />
        </svg>
      </button>

      <app-sidebar-navigation [rackId]="data.rackId" />
    </div>
  `,
})
export class NavDrawerComponent {
  protected readonly data = inject<NavDrawerData>(DIALOG_DATA);
  protected readonly dialogRef = inject(DialogRef<void>);

  constructor() {
    const router = inject(Router);
    router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe(() => this.dialogRef.close());
  }
}
