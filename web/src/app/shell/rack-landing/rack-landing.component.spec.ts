import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { RackCatalogueResult } from '../../core/racks/rack-catalogue.models';
import { RackCatalogueService } from '../../core/racks/rack-catalogue.service';
import { RackLandingComponent } from './rack-landing.component';

describe('RackLandingComponent', () => {
  let fixture: ComponentFixture<RackLandingComponent>;
  let router: Router;
  let response: Subject<RackCatalogueResult>;
  const catalogue = {
    racks: signal<{ id: string; externalKey: string; name: string }[]>([]),
    loading: signal(true),
    result: signal<RackCatalogueResult | null>(null),
    load: vi.fn(),
  };

  beforeEach(() => {
    response = new Subject<RackCatalogueResult>();
    catalogue.racks.set([]);
    catalogue.loading.set(true);
    catalogue.result.set(null);
    catalogue.load.mockReset().mockReturnValue(response);
    TestBed.configureTestingModule({
      imports: [RackLandingComponent],
      providers: [provideRouter([]), { provide: RackCatalogueService, useValue: catalogue }],
    });
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(RackLandingComponent);
    fixture.detectChanges();
  });

  function finish(result: RackCatalogueResult): void {
    catalogue.result.set(result);
    catalogue.loading.set(false);
    if (result.kind === 'ok') catalogue.racks.set(result.value);
    response.next(result);
    response.complete();
    fixture.detectChanges();
  }

  it('routes to the first accessible rack after a successful load', () => {
    finish({ kind: 'ok', value: [{ id: 'rack-1', externalKey: 'R1', name: 'Rack One' }] });

    expect(router.navigate).toHaveBeenCalledWith(['/racks', 'rack-1', 'topology'], {
      replaceUrl: true,
    });
  });

  it('renders the empty state when no racks are accessible', () => {
    finish({ kind: 'ok', value: [] });

    expect(fixture.nativeElement.textContent).toContain('No racks are available.');
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it.each([{ kind: 'unauthorized' as const }, { kind: 'forbidden' as const }])(
    'redirects $kind results to access denied',
    (result) => {
      finish(result);

      expect(router.navigate).toHaveBeenCalledWith(['/access-denied'], { replaceUrl: true });
    },
  );

  it('renders an error and retries with a forced catalogue refresh', () => {
    finish({ kind: 'error', status: 500, correlationId: 'corr-1' });
    const retryResponse = new Subject<RackCatalogueResult>();
    catalogue.load.mockReturnValue(retryResponse);

    const retry = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain(
      'Racks could not be loaded.',
    );
    retry.click();

    expect(catalogue.load).toHaveBeenLastCalledWith(true);
  });
});
