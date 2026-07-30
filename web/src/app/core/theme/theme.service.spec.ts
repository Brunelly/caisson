import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TelemetryService } from '../telemetry/telemetry.service';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

class FakeStorage implements Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> {
  private readonly store = new Map<string, string>();

  constructor(private readonly options: { failGetItem?: boolean; failSetItem?: boolean } = {}) {}

  getItem(key: string): string | null {
    if (this.options.failGetItem) {
      throw new DOMException('Storage disabled', 'SecurityError');
    }
    return this.store.has(key) ? (this.store.get(key) as string) : null;
  }

  setItem(key: string, value: string): void {
    if (this.options.failSetItem) {
      throw new DOMException('Quota exceeded', 'QuotaExceededError');
    }
    this.store.set(key, value);
  }

  removeItem(key: string): void {
    this.store.delete(key);
  }
}

/** A minimal `matchMedia` stub that supports the one `(prefers-color-scheme: dark)` change listener
 * `ThemeService` registers, plus answering both the dark/light queries consistently. */
function stubMatchMedia(initialPrefersLight: boolean) {
  let prefersLight = initialPrefersLight;
  const darkQueryListeners = new Set<(event: { matches: boolean }) => void>();

  const matchMedia = vi.fn((query: string) => ({
    get matches() {
      return query.includes('light') ? prefersLight : !prefersLight;
    },
    media: query,
    addEventListener: (_type: string, listener: (event: { matches: boolean }) => void) => {
      if (query.includes('dark')) {
        darkQueryListeners.add(listener);
      }
    },
    removeEventListener: (_type: string, listener: (event: { matches: boolean }) => void) => {
      darkQueryListeners.delete(listener);
    },
  }));

  vi.stubGlobal('matchMedia', matchMedia);

  return {
    /** Simulates the OS scheme changing and fires the `(prefers-color-scheme: dark)` change event. */
    setPrefersLight(value: boolean): void {
      prefersLight = value;
      darkQueryListeners.forEach((listener) => listener({ matches: !value }));
    },
  };
}

function createService(): ThemeService {
  return TestBed.inject(ThemeService);
}

describe('ThemeService', () => {
  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('resolves system dark with no persisted value', () => {
    vi.stubGlobal('localStorage', new FakeStorage());
    stubMatchMedia(false);

    const service = createService();

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('resolves system light with no persisted value', () => {
    vi.stubGlobal('localStorage', new FakeStorage());
    stubMatchMedia(true);

    const service = createService();

    expect(service.theme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('defaults to dark when matchMedia is unsupported', () => {
    vi.stubGlobal('localStorage', new FakeStorage());
    vi.stubGlobal('matchMedia', undefined);

    const service = createService();

    expect(service.theme()).toBe('dark');
  });

  it('a persisted override wins over the system preference', () => {
    const storage = new FakeStorage();
    storage.setItem(THEME_STORAGE_KEY, 'light');
    vi.stubGlobal('localStorage', storage);
    stubMatchMedia(false); // system prefers dark, but the persisted 'light' should win

    const service = createService();

    expect(service.theme()).toBe('light');
  });

  it('ignores a garbage persisted value and falls back to system preference', () => {
    const storage = new FakeStorage();
    storage.setItem(THEME_STORAGE_KEY, 'solarized');
    vi.stubGlobal('localStorage', storage);
    stubMatchMedia(false);

    const service = createService();

    expect(service.theme()).toBe('dark');
  });

  it('restores a persisted hc-dark preference across a simulated reload', () => {
    const storage = new FakeStorage();
    storage.setItem(THEME_STORAGE_KEY, 'hc-dark');
    stubMatchMedia(false);

    vi.stubGlobal('localStorage', storage);
    const first = createService();
    expect(first.theme()).toBe('hc-dark');

    // Simulate a page reload: a fresh injector/service instance, same underlying storage.
    TestBed.resetTestingModule();
    vi.stubGlobal('localStorage', storage);
    const second = createService();

    expect(second.theme()).toBe('hc-dark');
  });

  it('a matchMedia "change" event does NOT overwrite an existing persisted preference', () => {
    const storage = new FakeStorage();
    storage.setItem(THEME_STORAGE_KEY, 'dark');
    vi.stubGlobal('localStorage', storage);
    const media = stubMatchMedia(false);

    const service = createService();
    expect(service.theme()).toBe('dark');

    media.setPrefersLight(true); // OS scheme flips to light

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('a matchMedia "change" event DOES apply when no explicit/persisted preference exists yet', () => {
    vi.stubGlobal('localStorage', new FakeStorage());
    const media = stubMatchMedia(false);

    const service = createService();
    expect(service.theme()).toBe('dark');

    media.setPrefersLight(true);

    expect(service.theme()).toBe('light');
  });

  it('setTheme degrades silently (no throw, debug-log only) when localStorage throws QuotaExceededError', () => {
    vi.stubGlobal('localStorage', new FakeStorage({ failSetItem: true }));
    stubMatchMedia(false);

    const telemetry = TestBed.inject(TelemetryService);
    const recordSpy = vi.spyOn(telemetry, 'record');
    const service = createService();

    expect(() => service.setTheme('light')).not.toThrow();
    expect(service.theme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(recordSpy).toHaveBeenCalledWith(
      'theme.storage-write-failed',
      null,
      expect.objectContaining({ message: expect.any(String) }),
    );
  });

  it('setTheme ignores an invalid theme value', () => {
    vi.stubGlobal('localStorage', new FakeStorage());
    stubMatchMedia(false);

    const service = createService();
    // @ts-expect-error deliberately invalid at the type level too
    service.setTheme('neon');

    expect(service.theme()).toBe('dark');
  });
});
