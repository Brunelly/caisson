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
  ];

  it.each(cases)('renders the default label for kind "%s"', (kind, expectedLabel) => {
    fixture.componentInstance.kind = kind;
    fixture.detectChanges();

    expect(badgeEl().textContent?.trim()).toBe(expectedLabel);
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
});
