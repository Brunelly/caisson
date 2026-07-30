import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

// Story #119: `App`'s template is now a thin `<app-shell/>` wrapper (see app.html) so every route
// renders inside the Caisson Design System chrome without any `app.routes.ts` change (AC5). The old
// "renders only the router outlet, no boilerplate content" assertion no longer applies now that the
// shell chrome (sidebar, top bar, theme toggle) always renders — this asserts the shell is what's
// mounted instead.
describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('renders the app shell (sidebar, top bar, router outlet)', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('router-outlet')).toBeTruthy();
    expect(compiled.textContent).toContain('CAISSON');
    expect(compiled.querySelector('[role="radiogroup"]')).toBeTruthy();
  });
});
