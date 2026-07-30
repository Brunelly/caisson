import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, finalize, shareReplay, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { toApiResult } from '../../topology/services/api-result';
import type { RackCatalogueResult, RackSummary } from './rack-catalogue.models';

@Injectable({ providedIn: 'root' })
export class RackCatalogueService {
  private readonly http = inject(HttpClient);
  private request: Observable<RackCatalogueResult> | null = null;

  readonly racks = signal<RackSummary[]>([]);
  readonly loading = signal(false);
  readonly result = signal<RackCatalogueResult | null>(null);

  load(force = false): Observable<RackCatalogueResult> {
    if (force) this.request = null;
    if (!this.request) {
      this.loading.set(true);
      this.request = toApiResult(
        this.http.get<RackSummary[]>(`${environment.apiBaseUrl}/api/racks`),
      ).pipe(
        tap((result) => {
          this.result.set(result);
          if (result.kind === 'ok') this.racks.set(result.value);
          else this.request = null;
        }),
        finalize(() => this.loading.set(false)),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
    }
    return this.request;
  }
}
