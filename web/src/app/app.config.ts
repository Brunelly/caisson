import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { provideCaissonAuth } from './core/auth/auth.config';
import { authInterceptor } from './core/auth/auth.interceptor';
import { ThemeService } from './core/theme/theme.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideCaissonAuth(),
    provideHttpClient(withInterceptors([authInterceptor])),
    // Constructs ThemeService at bootstrap so its reactive machinery (the prefers-color-scheme change
    // listener) is live for the whole app lifetime, not only once some component happens to inject it.
    // index.html's inline script already applied the correct `data-theme` before first paint (NFR1);
    // this just brings the one resolution algorithm (theme.service.ts) online to reconcile/keep it live.
    provideAppInitializer(() => {
      inject(ThemeService);
    }),
  ],
};
