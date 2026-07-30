// Unit tests for DesiredStateRoundTripService (story #169), mirroring network-intent.service.spec.ts's
// HttpTestingController pattern: call the service, assert the outgoing request, flush a canned response,
// then assert the mapped discriminated-union result.
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type {
  DesiredStateRenderRequest,
  DesiredStateRoundTripEnvelopeDto,
} from '../model/network-intent-contracts';
import { DesiredStateRoundTripService } from './desired-state-roundtrip.service';

describe('DesiredStateRoundTripService', () => {
  let service: DesiredStateRoundTripService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';
  const base = `${environment.apiBaseUrl}/api/racks/${rackId}/desired-state`;

  const envelope: DesiredStateRoundTripEnvelopeDto = {
    supportedModel: {
      rackSlug: 'rack-1',
      vlanCatalogue: [{ id: 10, name: 'storage', description: 'iSCSI' }],
      portIntents: [{ switchStableKey: 'sw1', portName: 'eth1', accessVlanId: 10 }],
    },
    unknownBlocks: [{ anchorPath: 'extensions', rawYamlText: 'extensions:\n  l3: {}\n', checksum: 'abc' }],
    warnings: ['commentsNotPreserved'],
    schemaVersion: 1,
  };

  const renderRequest: DesiredStateRenderRequest = {
    vlanCatalogue: envelope.supportedModel.vlanCatalogue,
    portIntents: envelope.supportedModel.portIntents,
    unknownBlocks: envelope.unknownBlocks,
    warnings: envelope.warnings,
    schemaVersion: 1,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(DesiredStateRoundTripService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('parse POSTs the yaml and maps a 200 to ok', async () => {
    const result = firstValueFrom(service.parse(rackId, 'apiVersion: caisson.dev/v1alpha1'));

    const req = httpMock.expectOne(`${base}/parse`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ yaml: 'apiVersion: caisson.dev/v1alpha1' });
    req.flush(envelope);

    await expect(result).resolves.toEqual({ kind: 'ok', value: envelope });
  });

  it('parse maps a 400 into validationError with the richer issues extension', async () => {
    const result = firstValueFrom(service.parse(rackId, 'bad'));

    const req = httpMock.expectOne(`${base}/parse`);
    req.flush(
      {
        errors: { 'spec.vlans[0].vlanId': ['out of range'] },
        issues: [{ path: 'spec.vlans[0].vlanId', message: 'out of range', line: 7, column: 11 }],
      },
      { status: 400, statusText: 'Bad Request' },
    );

    await expect(result).resolves.toEqual({
      kind: 'validationError',
      issues: [{ path: 'spec.vlans[0].vlanId', message: 'out of range', line: 7, column: 11 }],
    });
  });

  it('parse falls back to the standard errors dictionary when no issues extension is present', async () => {
    const result = firstValueFrom(service.parse(rackId, 'bad'));

    const req = httpMock.expectOne(`${base}/parse`);
    req.flush({ errors: { 'metadata.rackSlug': ['is required'] } }, { status: 400, statusText: 'Bad Request' });

    await expect(result).resolves.toEqual({
      kind: 'validationError',
      issues: [{ path: 'metadata.rackSlug', message: 'is required', line: null, column: null }],
    });
  });

  it('parse maps 401/403 into the shared ApiResult branches', async () => {
    const unauthorized = firstValueFrom(service.parse(rackId, 'x'));
    httpMock.expectOne(`${base}/parse`).flush(null, { status: 401, statusText: 'Unauthorized' });
    await expect(unauthorized).resolves.toEqual({ kind: 'unauthorized' });

    const forbidden = firstValueFrom(service.parse(rackId, 'x'));
    httpMock.expectOne(`${base}/parse`).flush(null, { status: 403, statusText: 'Forbidden' });
    await expect(forbidden).resolves.toEqual({ kind: 'forbidden' });
  });

  it('render POSTs the request and maps a 200 to ok', async () => {
    const result = firstValueFrom(service.render(rackId, renderRequest));

    const req = httpMock.expectOne(`${base}/render`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(renderRequest);
    req.flush({ yaml: 'apiVersion: caisson.dev/v1alpha1\n', warnings: ['commentsNotPreserved'] });

    await expect(result).resolves.toEqual({
      kind: 'ok',
      value: { yaml: 'apiVersion: caisson.dev/v1alpha1\n', warnings: ['commentsNotPreserved'] },
    });
  });

  it('render maps a 400 into validationError', async () => {
    const result = firstValueFrom(service.render(rackId, renderRequest));

    const req = httpMock.expectOne(`${base}/render`);
    req.flush(
      { errors: { 'vlanCatalogue[0].id': ['out of range'] } },
      { status: 400, statusText: 'Bad Request' },
    );

    await expect(result).resolves.toMatchObject({ kind: 'validationError' });
  });
});
