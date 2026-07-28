import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { firstValueFrom, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { extractRoles, hasRecognisedRole, roleGuard } from './role.guard';

describe('extractRoles', () => {
  it('reads a string-array roles claim', () => {
    expect(extractRoles({ roles: ['Admin', 'Operator'] })).toEqual(['Admin', 'Operator']);
  });

  it('wraps a single-string roles claim into an array', () => {
    expect(extractRoles({ roles: 'ReadOnly' })).toEqual(['ReadOnly']);
  });

  it('returns an empty array when there is no roles claim', () => {
    expect(extractRoles({})).toEqual([]);
  });

  it('returns an empty array for a null/non-object payload', () => {
    expect(extractRoles(null)).toEqual([]);
    expect(extractRoles(undefined)).toEqual([]);
  });
});

describe('hasRecognisedRole', () => {
  it('accepts each of the four canonical roles', () => {
    expect(hasRecognisedRole({ roles: ['Admin'] })).toBe(true);
    expect(hasRecognisedRole({ roles: ['Operator'] })).toBe(true);
    expect(hasRecognisedRole({ roles: ['ReadOnly'] })).toBe(true);
    expect(hasRecognisedRole({ roles: ['ServiceAccount'] })).toBe(true);
  });

  it('rejects an unrecognised role', () => {
    expect(hasRecognisedRole({ roles: ['SomeOtherRole'] })).toBe(false);
  });

  it('rejects an authenticated payload with no roles claim', () => {
    expect(hasRecognisedRole({ sub: 'user-1' })).toBe(false);
  });
});

describe('roleGuard', () => {
  function setup(isAuthenticated: boolean, payload: unknown) {
    const authorize = vi.fn();
    const oidcStub = {
      isAuthenticated$: of({ isAuthenticated }),
      getPayloadFromAccessToken: () => of(payload),
      authorize,
    };

    TestBed.configureTestingModule({
      providers: [
        { provide: OidcSecurityService, useValue: oidcStub },
        { provide: Router, useValue: { parseUrl: (url: string) => ({ __urlTree: url }) } },
      ],
    });

    return { authorize };
  }

  it('redirects to login and returns false when not authenticated', async () => {
    const { authorize } = setup(false, null);
    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(roleGuard({} as never, {} as never) as never),
    );

    expect(authorize).toHaveBeenCalled();
    expect(result).toBe(false);
  });

  it('returns true for an authenticated user with a recognised role', async () => {
    setup(true, { roles: ['ReadOnly'] });
    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(roleGuard({} as never, {} as never) as never),
    );

    expect(result).toBe(true);
  });

  it('returns an access-denied UrlTree for an authenticated user with no recognised role', async () => {
    setup(true, { roles: ['SomeOtherRole'] });
    const result = await TestBed.runInInjectionContext(() =>
      firstValueFrom(roleGuard({} as never, {} as never) as never),
    );

    expect(result).toEqual({ __urlTree: '/access-denied' });
  });
});
