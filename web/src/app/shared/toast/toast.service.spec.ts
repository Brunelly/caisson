import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastService } from './toast.service';

describe('ToastService', () => {
  let service: ToastService;

  beforeEach(() => {
    vi.useFakeTimers();
    service = new ToastService();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('queues a success toast with no correlationId', () => {
    service.success('Applied.');

    expect(service.toasts()).toEqual([
      { id: 0, kind: 'success', text: 'Applied.', correlationId: null },
    ]);
  });

  it('queues an error toast carrying an optional correlationId', () => {
    service.error('Boom.', 'corr-1');

    expect(service.toasts()).toEqual([
      { id: 0, kind: 'error', text: 'Boom.', correlationId: 'corr-1' },
    ]);
  });

  it('auto-dismisses a success toast after the timeout', () => {
    service.success('Applied.');
    expect(service.toasts().length).toBe(1);

    vi.advanceTimersByTime(6000);

    expect(service.toasts().length).toBe(0);
  });

  it('does not auto-dismiss an error toast', () => {
    service.error('Boom.');

    vi.advanceTimersByTime(60_000);

    expect(service.toasts().length).toBe(1);
  });

  it('dismiss() removes a toast by id without affecting others', () => {
    service.error('First.');
    service.error('Second.');
    const [first, second] = service.toasts();

    service.dismiss(first.id);

    expect(service.toasts()).toEqual([second]);
  });
});
