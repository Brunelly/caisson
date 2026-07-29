import { TestBed } from '@angular/core/testing';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DriftPermissionService } from './drift-permission.service';

describe('DriftPermissionService', () => {
  function setup(payload: unknown) {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: OidcSecurityService,
          useValue: { getPayloadFromAccessToken: () => of(payload) },
        },
      ],
    });
    return TestBed.inject(DriftPermissionService);
  }

  it('exposes canApplyDrift=true when the roles claim includes DriftApply', () => {
    const service = setup({ roles: ['Operator', 'DriftApply'] });

    expect(service.canApplyDrift()).toBe(true);
  });

  it('exposes canApplyDrift=false when the roles claim omits DriftApply', () => {
    const service = setup({ roles: ['Admin'] });

    expect(service.canApplyDrift()).toBe(false);
  });

  it('exposes canApplyDrift=false when there is no roles claim at all', () => {
    const service = setup({});

    expect(service.canApplyDrift()).toBe(false);
  });
});
