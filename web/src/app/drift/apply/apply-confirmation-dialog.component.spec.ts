import { describe, expect, it } from 'vitest';
import type { DriftItemDto } from '../model/drift-contracts';
import { rollbackWindowTextFor } from './apply-confirmation-dialog.component';

function item(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|sw-01|ether5',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch',
    details: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('rollbackWindowTextFor', () => {
  it('renders the numeric rollback window from details when present', () => {
    expect(rollbackWindowTextFor(item({ details: { rollbackWindowSeconds: 120 } }))).toBe(
      'Confirmed-commit rollback window: 120 seconds.',
    );
  });

  it('never hardcodes the story-illustrative 120s default when details omits the field', () => {
    const text = rollbackWindowTextFor(item({ details: null }));
    expect(text).not.toContain('120');
    expect(text).toContain('automatic confirmed-commit rollback');
  });

  it('falls back to non-numeric copy for a malformed (non-number) rollbackWindowSeconds', () => {
    const text = rollbackWindowTextFor(item({ details: { rollbackWindowSeconds: 'soon' } }));
    expect(text).toContain('automatic confirmed-commit rollback');
  });

  it('falls back to non-numeric copy when details is not an object', () => {
    const text = rollbackWindowTextFor(item({ details: 'opaque-string' as never }));
    expect(text).toContain('automatic confirmed-commit rollback');
  });
});
