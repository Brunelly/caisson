import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ToastOutletComponent } from './toast-outlet.component';
import { ToastService } from './toast.service';

describe('ToastOutletComponent', () => {
  let fixture: ComponentFixture<ToastOutletComponent>;
  let toasts: ToastService;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ToastOutletComponent] });
    fixture = TestBed.createComponent(ToastOutletComponent);
    toasts = TestBed.inject(ToastService);
    fixture.detectChanges();
  });

  it('renders nothing when there are no toasts', () => {
    expect(fixture.nativeElement.querySelectorAll('.toast').length).toBe(0);
  });

  it('renders a success toast with role="status" and aria-live="polite"', () => {
    toasts.success('Applied.');
    fixture.detectChanges();

    const el = fixture.nativeElement.querySelector('.toast--success');
    expect(el).toBeTruthy();
    expect(el.getAttribute('role')).toBe('status');
    expect(el.getAttribute('aria-live')).toBe('polite');
    expect(el.textContent).toContain('Applied.');
  });

  it('renders an error toast with role="alert" and aria-live="assertive", including the correlationId', () => {
    toasts.error('Boom.', 'corr-1');
    fixture.detectChanges();

    const el = fixture.nativeElement.querySelector('.toast--error');
    expect(el).toBeTruthy();
    expect(el.getAttribute('role')).toBe('alert');
    expect(el.getAttribute('aria-live')).toBe('assertive');
    expect(el.textContent).toContain('Boom.');
    expect(el.textContent).toContain('corr-1');
  });

  it('the dismiss button removes the toast', () => {
    toasts.error('Boom.');
    fixture.detectChanges();

    fixture.nativeElement.querySelector('.toast__dismiss').click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.toast').length).toBe(0);
  });
});
