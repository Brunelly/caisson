// App chrome sidebar (Story #119 Task #127, per the SidebarNavigation design). Only Topology and Drift
// are real, navigable routes (built against the actual `app.routes.ts` entries) — any other item shown
// for visual fidelity to the design is rendered non-interactively (`aria-disabled`, no `routerLink`);
// no new routes or backing behaviour are invented here.
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-sidebar-navigation',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  styleUrl: './sidebar-navigation.component.scss',
  template: `
    <aside class="sidebar">
      <div class="sidebar__brand">
        <span class="sidebar__brand-mark" aria-hidden="true">C</span>
        <span class="sidebar__brand-text">
          <span class="sidebar__brand-name">CAISSON</span>
          <span class="sidebar__brand-sub">rack ops</span>
        </span>
      </div>

      <nav class="sidebar__nav" aria-label="Primary">
        <p class="sidebar__group-label">Fabric</p>

        @if (rackId(); as id) {
          <a
            class="sidebar__nav-item"
            [routerLink]="['/racks', id, 'topology']"
            routerLinkActive="sidebar__nav-item--active"
          >
            <svg
              class="sidebar__nav-icon"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.75"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <rect x="16" y="16" width="6" height="6" rx="1" />
              <rect x="2" y="16" width="6" height="6" rx="1" />
              <rect x="9" y="2" width="6" height="6" rx="1" />
              <path d="M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3" />
              <path d="M12 12V8" />
            </svg>
            <span class="sidebar__nav-label-text">Topology</span>
          </a>
          <a
            class="sidebar__nav-item"
            [routerLink]="['/racks', id, 'drift']"
            routerLinkActive="sidebar__nav-item--active"
          >
            <svg
              class="sidebar__nav-icon"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.75"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <circle cx="18" cy="18" r="3" />
              <circle cx="6" cy="6" r="3" />
              <path d="M13 6h3a2 2 0 0 1 2 2v7" />
              <path d="M11 18H8a2 2 0 0 1-2-2V9" />
            </svg>
            <span class="sidebar__nav-label-text">Drift</span>
          </a>
        } @else {
          <span class="sidebar__nav-item sidebar__nav-item--disabled" aria-disabled="true">
            <span class="sidebar__nav-label-text">Topology</span>
          </span>
          <span class="sidebar__nav-item sidebar__nav-item--disabled" aria-disabled="true">
            <span class="sidebar__nav-label-text">Drift</span>
          </span>
        }

        <p class="sidebar__group-label">System</p>
        <span
          class="sidebar__nav-item sidebar__nav-item--disabled"
          aria-disabled="true"
          title="Not available in this release"
        >
          <svg
            class="sidebar__nav-icon"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M20 7h-9M14 17H5" />
            <circle cx="17" cy="17" r="3" />
            <circle cx="7" cy="7" r="3" />
          </svg>
          <span class="sidebar__nav-label-text">Settings</span>
        </span>
      </nav>
    </aside>
  `,
})
export class SidebarNavigationComponent {
  readonly rackId = input<string | null>(null);
}
