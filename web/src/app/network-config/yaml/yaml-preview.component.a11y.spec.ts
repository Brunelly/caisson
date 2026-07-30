// Accessibility check for the YAML preview/export dialog (NFR5) — opened via the real @angular/cdk/dialog
// Dialog service (ADR 0034). The round-trip service and page state are stubbed so the dialog renders its
// success state (a read-only YAML block + the persistent comments notice) without any HTTP. color-contrast
// is disabled (jsdom has no paint engine); real-browser contrast lives in the e2e harness.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { YamlPreviewComponent } from './yaml-preview.component';

describe('YamlPreviewComponent accessibility', () => {
  afterEach(() => TestBed.inject(Dialog).closeAll());

  it('has no automatically-detectable accessibility violations', async () => {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: DesiredStateRoundTripService,
          useValue: {
            render: () =>
              of({
                kind: 'ok',
                value: {
                  yaml: 'apiVersion: caisson.dev/v1alpha1\nkind: RackDesiredState\n',
                  warnings: ['commentsNotPreserved'],
                },
              }),
          },
        },
        {
          provide: NetworkIntentStateService,
          useValue: {
            rackId: signal('rack-1'),
            renderRequest: () => ({
              vlanCatalogue: [],
              portIntents: [],
              unknownBlocks: [],
              warnings: [],
              schemaVersion: 1,
            }),
          },
        },
      ],
    });
    const dialog = TestBed.inject(Dialog);

    dialog.open(YamlPreviewComponent, { ariaLabelledBy: 'yaml-preview-heading' });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.querySelector('.yaml-preview')).toBeTruthy();
    expect(document.querySelector('.yaml-preview__code')?.textContent).toContain(
      'RackDesiredState',
    );

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });
    expect(results.violations).toEqual([]);
  }, 15000);
});
