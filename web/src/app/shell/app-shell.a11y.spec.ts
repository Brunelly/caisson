// Automated accessibility check for the app chrome shell (Story #119 Task #128), mirroring
// topology-page.a11y.spec.ts's jsdom axe pattern. `color-contrast` is disabled here — jsdom has no
// real layout/paint engine, so it cannot compute rendered colours; the real, meaningful contrast check
// is the Playwright e2e pass (web/e2e/theme-shell.spec.ts), which runs axe with color-contrast enabled
// in a real browser across all three themes.
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import axe from 'axe-core';
import { beforeEach, describe, expect, it } from 'vitest';
import { AppShellComponent } from './app-shell.component';

describe('AppShellComponent accessibility', () => {
  let fixture: ComponentFixture<AppShellComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: {
        // jsdom has no layout/paint engine, so contrast can't be computed here — see the Playwright
        // e2e a11y pass (theme-shell.spec.ts) for the real, browser-based contrast check.
        'color-contrast': { enabled: false },
      },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  // Story #123 Task #140: the mobile nav-drawer trigger is always in the DOM (CSS-hidden above `md` —
  // rack-selector-topbar.component.scss) rather than conditionally rendered, so it's covered by the
  // scan above at every viewport; this pins down its labelling specifically. The drawer's own content
  // (NavDrawerComponent, opened via CDK Dialog) is covered separately by
  // nav-drawer.component.a11y.spec.ts, since it never mounts inside AppShellComponent's own fixture.
  it('the mobile nav-drawer trigger is a labelled button', () => {
    const hamburger = fixture.nativeElement.querySelector('.topbar__hamburger');
    expect(hamburger?.tagName).toBe('BUTTON');
    expect(hamburger?.getAttribute('aria-label')).toBe('Open navigation');
  });
});
