import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { DriftSeverity } from '../model/drift-contracts';
import { DriftSeverityBadgeComponent } from './drift-severity-badge.component';

@Component({
  standalone: true,
  imports: [DriftSeverityBadgeComponent],
  template: `<app-drift-severity-badge [severity]="severity" />`,
})
class HostComponent {
  severity: DriftSeverity = 'Low';
}

describe('DriftSeverityBadgeComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  function badgeEl(): HTMLElement {
    return fixture.nativeElement.querySelector('.status-badge');
  }

  it('maps High severity onto the unmapped (red) token class, never the confidence "High" (green) class', () => {
    fixture.componentInstance.severity = 'High';
    fixture.detectChanges();

    expect(badgeEl().className).toContain('status-badge--severity-high');
    expect(badgeEl().className).not.toContain('status-badge--high');
    expect(badgeEl().textContent?.trim()).toContain('High severity');
  });

  it('maps Medium severity onto the ambiguous (amber) token class', () => {
    fixture.componentInstance.severity = 'Medium';
    fixture.detectChanges();

    expect(badgeEl().className).toContain('status-badge--severity-medium');
    expect(badgeEl().textContent?.trim()).toContain('Medium severity');
  });

  it('maps Low severity onto the neutral/muted token class, never the red "unmapped"/"low" classes', () => {
    fixture.componentInstance.severity = 'Low';
    fixture.detectChanges();

    expect(badgeEl().className).toContain('status-badge--severity-low');
    expect(badgeEl().className).not.toContain('status-badge--unmapped');
    expect(badgeEl().textContent?.trim()).toContain('Low severity');
  });
});
