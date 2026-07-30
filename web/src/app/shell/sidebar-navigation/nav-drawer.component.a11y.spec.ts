// Automated accessibility check for the mobile nav drawer's content (Story #123 Task #140/#143), the
// new markup this story adds to the shell. Mirrors app-shell.a11y.spec.ts's jsdom axe pattern —
// `color-contrast` disabled here for the same reason (jsdom cannot paint); the real, meaningful contrast
// check is the Playwright e2e pass (topology-harness.spec.ts's "responsive (sm/md)" describe block,
// which opens the real drawer via the real hamburger in a real browser).
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import axe from 'axe-core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NavDrawerComponent } from './nav-drawer.component';

describe('NavDrawerComponent accessibility', () => {
  let fixture: ComponentFixture<NavDrawerComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NavDrawerComponent],
      providers: [
        provideRouter([]),
        { provide: DIALOG_DATA, useValue: { rackId: 'rack-1' } },
        { provide: DialogRef, useValue: { close: vi.fn() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(NavDrawerComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: {
        'color-contrast': { enabled: false },
      },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  it('the close button is labelled and the reused sidebar nav renders real routerLinks for the given rackId', () => {
    const close = fixture.nativeElement.querySelector('.nav-drawer__close');
    expect(close.getAttribute('aria-label')).toBe('Close navigation');

    const links = fixture.nativeElement.querySelectorAll('a.sidebar__nav-item');
    expect(links.length).toBe(3);
  });
});
