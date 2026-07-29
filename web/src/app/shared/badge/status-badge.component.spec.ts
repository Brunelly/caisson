import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { BadgeKind } from './status-badge.component';
import { StatusBadgeComponent } from './status-badge.component';

@Component({
  standalone: true,
  imports: [StatusBadgeComponent],
  template: `<app-status-badge [kind]="kind" [labelText]="labelText" />`,
})
class HostComponent {
  kind: BadgeKind = 'confirmed';
  labelText: string | undefined = undefined;
}

describe('StatusBadgeComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  function badgeEl(): HTMLElement {
    return fixture.nativeElement.querySelector('.status-badge');
  }

  const cases: [BadgeKind, string][] = [
    ['confirmed', 'Confirmed'],
    ['ambiguous', 'Ambiguous'],
    ['unmapped', 'Unmapped'],
    ['High', 'High confidence'],
    ['Medium', 'Medium confidence'],
    ['Low', 'Low confidence'],
    ['severity-high', 'High severity'],
    ['severity-medium', 'Medium severity'],
    ['severity-low', 'Low severity'],
  ];

  it.each(cases)('renders the default label for kind "%s"', (kind, expectedLabel) => {
    fixture.componentInstance.kind = kind;
    fixture.detectChanges();

    // toContain (not toBe): severity kinds additionally render a leading icon glyph (NFR5) alongside
    // the text label, so the element's full text content is "<icon> <label>", not just "<label>".
    expect(badgeEl().textContent?.trim()).toContain(expectedLabel);
  });

  it.each(cases)('applies a css class keyed off a lowercased kind for "%s"', (kind) => {
    fixture.componentInstance.kind = kind;
    fixture.detectChanges();

    expect(badgeEl().className).toContain(`status-badge--${kind.toLowerCase()}`);
  });

  it('overrides the default label when labelText is provided', () => {
    fixture.componentInstance.kind = 'High';
    fixture.componentInstance.labelText = '95% match';
    fixture.detectChanges();

    expect(badgeEl().textContent?.trim()).toBe('95% match');
  });

  const severityIconCases: [BadgeKind, string][] = [
    ['severity-high', '✕'],
    ['severity-medium', '▲'],
    ['severity-low', 'ℹ'],
  ];

  it.each(severityIconCases)(
    'renders an aria-hidden icon glyph for severity kind "%s" (NFR5: never colour-only)',
    (kind, expectedIcon) => {
      fixture.componentInstance.kind = kind;
      fixture.detectChanges();

      const icon = badgeEl().querySelector('.status-badge__icon');
      expect(icon?.getAttribute('aria-hidden')).toBe('true');
      expect(icon?.textContent).toBe(expectedIcon);
    },
  );

  it('renders no icon for non-severity kinds', () => {
    fixture.componentInstance.kind = 'confirmed';
    fixture.detectChanges();

    expect(badgeEl().querySelector('.status-badge__icon')).toBeNull();
  });
});
