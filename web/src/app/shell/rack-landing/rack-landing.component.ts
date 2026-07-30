import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { RackCatalogueService } from '../../core/racks/rack-catalogue.service';

@Component({
  selector: 'app-rack-landing',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="rack-landing">
      @if (catalogue.loading()) {
        <p role="status">Loading racks…</p>
      } @else if (catalogue.result()?.kind === 'ok' && catalogue.racks().length === 0) {
        <p role="status">No racks are available.</p>
      } @else if (isError()) {
        <p role="alert">Racks could not be loaded.</p>
        <button type="button" (click)="load(true)">Retry</button>
      }
    </main>
  `,
  styles: [
    `
      .rack-landing {
        padding: var(--cds-sp-6);
        color: var(--cds-text-primary);
      }
    `,
  ],
})
export class RackLandingComponent {
  protected readonly catalogue = inject(RackCatalogueService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.load();
  }

  protected load(force = false): void {
    this.catalogue
      .load(force)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((result) => {
        if (result.kind === 'ok' && result.value[0]) {
          void this.router.navigate(['/racks', result.value[0].id, 'topology'], {
            replaceUrl: true,
          });
        } else if (result.kind === 'unauthorized' || result.kind === 'forbidden') {
          void this.router.navigate(['/access-denied'], { replaceUrl: true });
        }
      });
  }

  protected isError(): boolean {
    const result = this.catalogue.result();
    return (
      result !== null &&
      result.kind !== 'ok' &&
      result.kind !== 'unauthorized' &&
      result.kind !== 'forbidden'
    );
  }
}
