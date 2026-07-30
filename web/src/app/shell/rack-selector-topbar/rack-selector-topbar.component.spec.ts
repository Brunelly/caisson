// Story #123 Task #140: covers the hamburger's mobile nav-drawer wiring (opens `NavDrawerComponent` in
// a CDK Dialog with the DS-tokened scrim, and closes on navigation) — mirrors
// drift/apply/apply-action.component.spec.ts's dialog-assertion pattern.
//
// `openNavDrawer` dynamically `import()`s `@angular/cdk/dialog`/`nav-drawer.component.ts` (see that
// method's own comment for why) — dynamic `import()` is a language construct zone.js cannot patch, so
// `fixture.whenStable()` never observes it. Tests poll via `vi.waitFor` instead of the usual
// `whenStable()` await for exactly that reason.
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RackCatalogueService } from '../../core/racks/rack-catalogue.service';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { RackSelectorTopBarComponent } from './rack-selector-topbar.component';

@Component({ standalone: true, template: '' })
class BlankRouteComponent {}

@Component({
  standalone: true,
  imports: [RackSelectorTopBarComponent],
  template: `<app-rack-selector-topbar [rackId]="rackId()" />`,
})
class HostComponent {
  readonly rackId = signal<string | null>('rack-1');
}

describe('RackSelectorTopBarComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let router: Router;
  const racks = [
    { id: 'rack-1', externalKey: 'RACK-001', name: 'Rack One' },
    { id: 'rack-2', externalKey: 'RACK-002', name: 'Rack Two' },
  ];
  const catalogue = {
    racks: signal(racks),
    loading: signal(false),
    result: signal({ kind: 'ok' as const, value: racks }),
    load: vi.fn(() => of({ kind: 'ok' as const, value: racks })),
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankRouteComponent }]),
        { provide: RackCatalogueService, useValue: catalogue },
        { provide: TopologyStateService, useValue: { connectionStatus: () => 'live' } },
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    router = TestBed.inject(Router);
  });

  it('opens the rack list and navigates to the selected rack', async () => {
    await fixture.whenStable();
    fixture.detectChanges();

    const trigger = fixture.nativeElement.querySelector('.topbar__rack') as HTMLButtonElement;
    expect(trigger.textContent).toContain('Rack One');
    trigger.click();
    fixture.detectChanges();

    const options = document.querySelectorAll<HTMLElement>('.rack-option');
    expect(options).toHaveLength(2);
    options[1].click();
    await fixture.whenStable();

    expect(router.url).toBe('/racks/rack-2/topology');
  });

  afterEach(() => {
    // A still-open CDK dialog from a test that didn't close it would otherwise leak into the next
    // test's `.nav-drawer` lookups (same convention as apply-action.component.spec.ts).
    document
      .querySelectorAll('.nav-drawer')
      .forEach((el) => el.closest('.cdk-overlay-pane')?.remove());
  });

  function hamburger(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.topbar__hamburger');
  }

  function drawerEl(): HTMLElement | null {
    return document.querySelector('.nav-drawer');
  }

  async function openDrawer(): Promise<HTMLElement> {
    hamburger().click();
    return vi.waitFor(() => {
      const drawer = drawerEl();
      expect(drawer).toBeTruthy();
      return drawer!;
    });
  }

  it('opens the nav drawer in a true (aria-modal) CDK Dialog carrying the current rackId', async () => {
    const drawer = await openDrawer();

    // The rackId must have reached SidebarNavigationComponent via DIALOG_DATA — with no rackId it
    // renders `aria-disabled` spans instead of real `<a routerLink>` nav items (sidebar-navigation
    // .component.ts).
    const navLinks = await vi.waitFor(() => {
      const links = drawer.querySelectorAll('a.sidebar__nav-item');
      expect(links.length).toBe(3);
      return links;
    });
    expect([...navLinks].some((a) => a.getAttribute('href')?.includes('rack-1'))).toBe(true);

    const dialogContainer = drawer.closest('[role="dialog"]');
    expect(dialogContainer?.getAttribute('aria-modal')).toBe('true');
    expect(document.querySelector('.cds-overlay-backdrop')).toBeTruthy();
  });

  it('closes the drawer when a navigation completes (e.g. a nav-item click)', async () => {
    await openDrawer();

    await router.navigateByUrl('/racks/rack-1/topology');
    fixture.detectChanges();

    await vi.waitFor(() => expect(drawerEl()).toBeNull());
  });

  it('the close button dismisses the drawer', async () => {
    const drawer = await openDrawer();

    drawer.querySelector<HTMLButtonElement>('.nav-drawer__close')!.click();

    await vi.waitFor(() => expect(drawerEl()).toBeNull());
  });
});
