// Accessibility check for the validation summary (NFR5): errors live in an assertive region and warnings
// in a polite one, and focus lands on the error heading. color-contrast is disabled for the same
// jsdom-has-no-paint-engine reason as the sibling specs; real-browser contrast is in the e2e harness.
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { describe, expect, it } from 'vitest';
import { ValidationSummaryComponent } from './validation-summary.component';

describe('ValidationSummaryComponent accessibility', () => {
  it('errors are an assertive live region and focus moves to the heading', async () => {
    TestBed.configureTestingModule({ imports: [ValidationSummaryComponent] });
    const fixture = TestBed.createComponent(ValidationSummaryComponent);
    fixture.componentRef.setInput('errors', [
      { path: 'spec.vlans[0].vlanId', message: 'out of range', line: 7, column: 11 },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    const region = fixture.nativeElement.querySelector('[role="alert"]');
    expect(region).toBeTruthy();
    expect(region.getAttribute('aria-live')).toBe('assertive');
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.validation-summary__heading'));

    const results = await axe.run(fixture.nativeElement, { rules: { 'color-contrast': { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it('warnings are a polite live region', async () => {
    TestBed.configureTestingModule({ imports: [ValidationSummaryComponent] });
    const fixture = TestBed.createComponent(ValidationSummaryComponent);
    fixture.componentRef.setInput('warnings', ['commentsNotPreserved']);
    fixture.detectChanges();
    await fixture.whenStable();

    const region = fixture.nativeElement.querySelector('[role="status"]');
    expect(region).toBeTruthy();
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.textContent).toContain('Comments are not preserved');

    const results = await axe.run(fixture.nativeElement, { rules: { 'color-contrast': { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});
