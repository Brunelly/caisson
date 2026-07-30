import { CdkConnectedOverlay, CdkOverlayOrigin, Overlay } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EnvironmentInjector,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { RackCatalogueService } from '../../core/racks/rack-catalogue.service';
import type { RackSummary } from '../../core/racks/rack-catalogue.models';
import { LiveConnectionStatusBarComponent } from '../../shared/connection-status/live-connection-status-bar.component';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { ThemeToggleComponent } from '../theme-toggle/theme-toggle.component';

@Component({
  selector: 'app-rack-selector-topbar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CdkConnectedOverlay,
    CdkOverlayOrigin,
    LiveConnectionStatusBarComponent,
    ThemeToggleComponent,
  ],
  styleUrl: './rack-selector-topbar.component.scss',
  template: ` <header class="topbar" role="banner">
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
        aria-hidden="true"
      >
        <path d="M4 6h16M4 12h16M4 18h16" />
      </svg>
    </button>
    <button
      #trigger
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      type="button"
      class="topbar__rack"
      role="combobox"
      aria-label="Select rack"
      aria-haspopup="listbox"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="open() ? listboxId : null"
      [attr.aria-activedescendant]="activeOptionId()"
      [disabled]="catalogue.loading() || catalogue.racks().length === 0"
      (click)="toggle()"
      (keydown)="onTriggerKeydown($event)"
    >
      @if (selected(); as rack) {
        <span class="topbar__rack-name">{{ rack.name }}</span
        ><span class="topbar__rack-id">{{ rack.externalKey }}</span>
      } @else {
        <span class="topbar__rack-label">{{ selectorLabel() }}</span>
      }
      <span aria-hidden="true">▾</span>
    </button>
    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayHasBackdrop]="true"
      cdkConnectedOverlayBackdropClass="cdk-overlay-transparent-backdrop"
      [cdkConnectedOverlayScrollStrategy]="scrollStrategy"
      (backdropClick)="close(true)"
      (detach)="close(false)"
    >
      <ul
        [id]="listboxId"
        class="rack-options"
        role="listbox"
        tabindex="-1"
        aria-label="Available racks"
        (keydown)="onListKeydown($event)"
      >
        @for (rack of catalogue.racks(); track rack.id; let index = $index) {
          <li
            #option
            [id]="optionId(index)"
            role="option"
            tabindex="-1"
            class="rack-option"
            [class.rack-option--active]="index === activeIndex()"
            [attr.aria-selected]="rack.id === rackId()"
            (click)="select(rack)"
            (keydown.enter)="select(rack)"
            (keydown.space)="select(rack); $event.preventDefault()"
            (mouseenter)="activeIndex.set(index)"
          >
            <span>{{ rack.name }}</span
            ><span class="rack-option__id">{{ rack.externalKey }}</span>
          </li>
        }
      </ul>
    </ng-template>
    <div class="topbar__right">
      @if (rackId()) {
        <app-live-connection-status-bar
          variant="badge"
          [status]="topologyState.connectionStatus()"
        />
      }
      <app-theme-toggle />
    </div>
  </header>`,
})
export class RackSelectorTopBarComponent {
  readonly rackId = input<string | null>(null);
  protected readonly catalogue = inject(RackCatalogueService);
  protected readonly topologyState = inject(TopologyStateService);
  private readonly router = inject(Router);
  private readonly overlay = inject(Overlay);
  private readonly injector = inject(EnvironmentInjector);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  protected readonly open = signal(false);
  protected readonly activeIndex = signal(0);
  protected readonly listboxId = 'rack-selector-listbox';
  protected readonly scrollStrategy = this.overlay.scrollStrategies.close();
  protected readonly selected = computed(
    () => this.catalogue.racks().find((rack) => rack.id === this.rackId()) ?? null,
  );
  protected readonly activeOptionId = computed(() =>
    this.open() ? this.optionId(this.activeIndex()) : null,
  );
  constructor() {
    this.catalogue.load().subscribe();
  }
  protected selectorLabel(): string {
    if (this.catalogue.loading()) return 'Loading racks…';
    const result = this.catalogue.result();
    return result && result.kind !== 'ok' ? 'Racks unavailable' : 'No racks available';
  }
  protected optionId(index: number): string {
    return `rack-selector-option-${index}`;
  }
  protected toggle(): void {
    if (this.open()) this.close(true);
    else this.openList();
  }
  private openList(): void {
    const selectedIndex = this.catalogue.racks().findIndex((rack) => rack.id === this.rackId());
    this.activeIndex.set(Math.max(0, selectedIndex));
    this.open.set(true);
    setTimeout(() => document.getElementById(this.optionId(this.activeIndex()))?.focus());
  }
  protected close(restore: boolean): void {
    this.open.set(false);
    if (restore) setTimeout(() => this.trigger().nativeElement.focus());
  }
  protected onTriggerKeydown(event: KeyboardEvent): void {
    if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(event.key)) {
      event.preventDefault();
      this.openList();
    }
  }
  protected onListKeydown(event: KeyboardEvent): void {
    const racks = this.catalogue.racks();
    if (event.key === 'Tab') return this.close(false);
    if (event.key === 'Escape') {
      event.preventDefault();
      return this.close(true);
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      this.activeIndex.set((this.activeIndex() + step + racks.length) % racks.length);
      document.getElementById(this.optionId(this.activeIndex()))?.focus();
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      const rack = racks[this.activeIndex()];
      if (rack) this.select(rack);
    }
  }
  protected select(rack: RackSummary): void {
    this.close(true);
    void this.router.navigate(['/racks', rack.id, 'topology']);
  }
  protected async openNavDrawer(): Promise<void> {
    const [{ Dialog }, { NavDrawerComponent }] = await Promise.all([
      import('@angular/cdk/dialog'),
      import('../sidebar-navigation/nav-drawer.component'),
    ]);
    this.injector.get(Dialog).open(NavDrawerComponent, {
      data: { rackId: this.rackId() },
      panelClass: 'cds-nav-drawer-panel',
      hasBackdrop: true,
      backdropClass: 'cds-overlay-backdrop',
      ariaModal: true,
    });
  }
}
