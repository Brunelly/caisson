// Story #119 (Task #125): resolves and persists the Caisson Design System theme. Deliberately a plain
// `providedIn: 'root'` signal service, no NgRx — same style as `TopologyStateService`/`ToastService`.
//
// Resolution order (AC3): a validated persisted `localStorage['caisson.theme']` value wins; otherwise
// `prefers-color-scheme` (light -> Light, else Dark); `matchMedia` being unsupported also defaults to
// Dark. Once an explicit/persisted choice exists, subsequent OS scheme changes are ignored — the app
// never auto-switches out from under a user's choice.
//
// FOUC guard (NFR1): `index.html` has a tiny inline `<script>` that duplicates ONLY the read-and-apply
// half of this resolution (no Angular hook runs before first paint). This service's `resolveInitial()`
// re-derives the same theme (there is exactly one resolution algorithm, defined once here) and applies
// it again — idempotent with whatever the inline script already set on `<html>`.
//
// Reliability (NFR5): storage reads/writes are wrapped in try/catch; failures are swallowed and
// debug-logged via `TelemetryService.record(...)`, never thrown, never `console.error`.
import { Injectable, inject, signal } from '@angular/core';
import { TelemetryService } from '../telemetry/telemetry.service';

export type Theme = 'dark' | 'light' | 'hc-dark';

export const THEME_STORAGE_KEY = 'caisson.theme';

const THEMES: readonly Theme[] = ['dark', 'light', 'hc-dark'];

function isTheme(value: unknown): value is Theme {
  return typeof value === 'string' && (THEMES as readonly string[]).includes(value);
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly telemetry = inject(TelemetryService);

  /** True once a persisted value was found at init, or `setTheme` has been called this session — from
   * that point on, OS `prefers-color-scheme` changes are never auto-applied (AC3). */
  private hasExplicitOverride = false;

  private readonly _theme = signal<Theme>('dark');
  readonly theme = this._theme.asReadonly();

  constructor() {
    const { theme, hasPersistedOverride } = this.resolveInitial();
    this.hasExplicitOverride = hasPersistedOverride;
    this._theme.set(theme);
    this.applyToDocument(theme);
    this.watchSystemPreference();
  }

  setTheme(theme: Theme): void {
    if (!isTheme(theme)) {
      return;
    }
    this.hasExplicitOverride = true;
    this._theme.set(theme);
    this.applyToDocument(theme);
    this.persist(theme);
  }

  /** The single source of truth for resolving the initial theme (localStorage -> system -> dark
   * default). `index.html`'s inline script duplicates only the read/apply half of this for pre-paint
   * use; this is the one place the algorithm itself is defined. */
  private resolveInitial(): { theme: Theme; hasPersistedOverride: boolean } {
    const persisted = this.readPersisted();
    if (persisted) {
      return { theme: persisted, hasPersistedOverride: true };
    }
    return { theme: this.resolveSystemPreference(), hasPersistedOverride: false };
  }

  private resolveSystemPreference(): Theme {
    try {
      if (typeof matchMedia !== 'function') {
        return 'dark';
      }
      return matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    } catch (error) {
      this.logDebug('theme.system-preference-read-failed', error);
      return 'dark';
    }
  }

  private watchSystemPreference(): void {
    try {
      if (typeof matchMedia !== 'function') {
        return;
      }
      const media = matchMedia('(prefers-color-scheme: dark)');
      media.addEventListener('change', (event) => {
        if (this.hasExplicitOverride) {
          return;
        }
        const theme: Theme = event.matches ? 'dark' : 'light';
        this._theme.set(theme);
        this.applyToDocument(theme);
      });
    } catch (error) {
      this.logDebug('theme.system-preference-watch-failed', error);
    }
  }

  private applyToDocument(theme: Theme): void {
    document.documentElement.setAttribute('data-theme', theme);
  }

  private readPersisted(): Theme | null {
    try {
      const value = localStorage.getItem(THEME_STORAGE_KEY);
      return isTheme(value) ? value : null;
    } catch (error) {
      this.logDebug('theme.storage-read-failed', error);
      return null;
    }
  }

  private persist(theme: Theme): void {
    try {
      localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch (error) {
      this.logDebug('theme.storage-write-failed', error);
    }
  }

  private logDebug(type: string, error: unknown): void {
    this.telemetry.record(type, null, {
      message: error instanceof Error ? error.message : String(error),
    });
  }
}
