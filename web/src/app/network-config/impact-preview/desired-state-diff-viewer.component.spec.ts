// Component spec for the unified-diff viewer (story #171, AC3): a required `diff` input drives the parsed
// +/− glyph lines (colour is never the sole signal), a copy-to-clipboard button, and collapsible context
// hunks. Mirrors the signal-input/`setInput` conventions of the other network-config component specs.
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastService } from '../../shared/toast/toast.service';
import { DesiredStateDiffViewerComponent } from './desired-state-diff-viewer.component';

// A one-hunk unified diff: one context line, one removed line, one added line.
const DIFF = '@@ -1,2 +1,2 @@\n context\n-old\n+new\n';

describe('DesiredStateDiffViewerComponent', () => {
  const toast = { success: vi.fn(), error: vi.fn() } satisfies Pick<
    ToastService,
    'success' | 'error'
  >;

  beforeEach(() => {
    toast.success.mockReset();
    toast.error.mockReset();
    TestBed.configureTestingModule({
      providers: [{ provide: ToastService, useValue: toast }],
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function render(diff: string) {
    const fixture = TestBed.createComponent(DesiredStateDiffViewerComponent);
    fixture.componentRef.setInput('diff', diff);
    fixture.detectChanges();
    return fixture;
  }

  it('renders +/− glyph lines for the added and removed rows', () => {
    const fixture = render(DIFF);
    const host = fixture.nativeElement as HTMLElement;

    const addLine = host.querySelector('.diff-viewer__line--add');
    const removeLine = host.querySelector('.diff-viewer__line--remove');
    expect(addLine).toBeTruthy();
    expect(removeLine).toBeTruthy();

    // NFR5: the glyph — not colour alone — encodes add vs remove.
    expect(addLine!.querySelector('.diff-viewer__glyph')!.textContent).toContain('+');
    expect(removeLine!.querySelector('.diff-viewer__glyph')!.textContent).toContain('−');
    expect(addLine!.textContent).toContain('new');
    expect(removeLine!.textContent).toContain('old');
  });

  it('onCopy writes the raw diff to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });

    const fixture = render(DIFF);
    const copy = fixture.nativeElement.querySelector('.diff-viewer__copy') as HTMLButtonElement;
    copy.click();
    await fixture.whenStable();

    expect(writeText).toHaveBeenCalledWith(DIFF);
    expect(toast.success).toHaveBeenCalled();
  });

  it('toggling a hunk collapses its context lines while keeping +/− change lines visible', () => {
    const fixture = render(DIFF);
    const host = fixture.nativeElement as HTMLElement;

    // The unified <pre> renders one context line before collapse.
    expect(host.querySelectorAll('.diff-viewer__unified .diff-viewer__line--context').length).toBe(
      1,
    );

    // Toggle the first (unified) hunk button.
    const hunkButton = host.querySelector(
      '.diff-viewer__unified .diff-viewer__hunk',
    ) as HTMLButtonElement;
    expect(hunkButton.getAttribute('aria-expanded')).toBe('true');
    hunkButton.click();
    fixture.detectChanges();

    expect(hunkButton.getAttribute('aria-expanded')).toBe('false');
    expect(host.querySelectorAll('.diff-viewer__unified .diff-viewer__line--context').length).toBe(
      0,
    );
    // The +/− change lines stay visible even when the context is collapsed.
    expect(host.querySelector('.diff-viewer__unified .diff-viewer__line--add')).toBeTruthy();
    expect(host.querySelector('.diff-viewer__unified .diff-viewer__line--remove')).toBeTruthy();
  });
});
