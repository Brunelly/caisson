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
});
