// Functional CanDeactivate guard test (story #168, AC4), mirroring role.guard.spec.ts's
// TestBed.runInInjectionContext pattern. The CDK Dialog is mocked (not rendered for real) — this is a
// unit test of the guard's own branching (dirty short-circuit, and closed-result -> boolean mapping),
// not an integration test of DiscardChangesDialogComponent's own markup/behaviour.
import { Dialog } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { DiscardChangesDialogComponent } from './discard-changes-dialog.component';
import { unsavedNetworkIntentChangesGuard } from './unsaved-changes.guard';

function setup(dirty: boolean, dialogClosedValue?: boolean) {
  const open = vi.fn(() => ({ closed: of(dialogClosedValue) }));

  TestBed.configureTestingModule({
    providers: [
      { provide: NetworkIntentStateService, useValue: { dirty: () => dirty } },
      { provide: Dialog, useValue: { open } },
    ],
  });

  return { open };
}

describe('unsavedNetworkIntentChangesGuard', () => {
  it('returns true synchronously without opening a dialog when the draft is not dirty', async () => {
    const { open } = setup(false);

    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(unsavedNetworkIntentChangesGuard()),
    );

    expect(result).toBe(true);
    expect(open).not.toHaveBeenCalled();
  });

  it('opens the discard-changes dialog when dirty, and resolves false when the user clicks "Keep editing"', async () => {
    const { open } = setup(true, false);

    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(unsavedNetworkIntentChangesGuard()),
    );

    expect(result).toBe(false);
    expect(open).toHaveBeenCalledWith(
      DiscardChangesDialogComponent,
      expect.objectContaining({
        ariaLabelledBy: 'discard-changes-dialog-heading',
        hasBackdrop: true,
        ariaModal: true,
      }),
    );
  });

  it('opens the discard-changes dialog when dirty, and resolves true when the user clicks "Discard changes"', async () => {
    setup(true, true);

    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(unsavedNetworkIntentChangesGuard()),
    );

    expect(result).toBe(true);
  });

  it('resolves false when the dialog closes with no result (e.g. Escape/backdrop click)', async () => {
    setup(true, undefined);

    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(unsavedNetworkIntentChangesGuard()),
    );

    expect(result).toBe(false);
  });
});
