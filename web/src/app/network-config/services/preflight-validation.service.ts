// Typed client for DesiredStatePreflightController (story #170): POST preflight-validate, NetworkConfigAuthor-
// gated. Modelled on NetworkIntentService/DesiredStateRoundTripService. The endpoint returns 200 with the
// grouped issue set even when the candidate is invalid (validation failures are never 4xx/5xx, NFR1), so the
// standard ApiResult mapping is sufficient — a 200 with errors is still `kind: 'ok'`.
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { type ApiResult, toApiResult } from '../../topology/services/api-result';
import type { PortAccessIntentDto, VlanCatalogueEntryDto } from '../model/network-intent-contracts';
import type { PreflightValidationResponse } from '../model/preflight-validation-contracts';

@Injectable({ providedIn: 'root' })
export class PreflightValidationService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/desired-state`;
  }

  /** Runs the schema → semantic → safety pipeline against the rack's latest observed topology. */
  validate(
    rackId: string,
    vlanCatalogue: VlanCatalogueEntryDto[],
    portIntents: PortAccessIntentDto[],
  ): Observable<ApiResult<PreflightValidationResponse>> {
    return toApiResult(
      this.http.post<PreflightValidationResponse>(`${this.rackUrl(rackId)}/preflight-validate`, {
        vlanCatalogue,
        portIntents,
      }),
    );
  }
}
