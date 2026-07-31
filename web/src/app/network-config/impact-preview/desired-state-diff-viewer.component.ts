// Renders a raw unified diff (story #171, AC3): a scrollable monospace viewer with '+'/'−' glyphs preceding
// every changed line (colour is never the sole signal), added/removed line tints aliasing --cds-success-bg /
// --cds-error-bg, copy-to-clipboard with a success/failure toast, and collapsible 'N unchanged lines' hunks.
// Split view (current | proposed) >= md, unified below, derived from the same parsed unified diff. Lifts the
// <pre tabindex><code> + navigator.clipboard pattern from yaml-preview.component.ts.
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { ToastService } from '../../shared/toast/toast.service';

type DiffLineType = 'context' | 'add' | 'remove' | 'empty';

interface UnifiedLine {
  type: Exclude<DiffLineType, 'empty'>;
  glyph: ' ' | '+' | '−';
  oldNum: number | null;
  newNum: number | null;
  text: string;
}

interface SplitCell {
  type: DiffLineType;
  num: number | null;
  glyph: '' | '+' | '−';
  text: string;
}

interface Hunk {
  header: string;
  contextCount: number;
  unified: UnifiedLine[];
  split: { left: SplitCell; right: SplitCell }[];
}

@Component({
  selector: 'app-desired-state-diff-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './desired-state-diff-viewer.component.scss',
  template: `
    <section class="diff-viewer" aria-labelledby="diff-viewer-title">
      <header class="diff-viewer__bar">
        <h3 id="diff-viewer-title" class="diff-viewer__title">desired-state.yaml · unified diff</h3>
        <button
          type="button"
          class="diff-viewer__copy"
          (click)="onCopy()"
          [attr.aria-label]="'Copy the raw unified diff to the clipboard'"
        >
          <span aria-hidden="true">⧉</span> Copy diff
        </button>
      </header>

      @if (isEmpty()) {
        <p class="diff-viewer__empty" role="status">
          No textual differences between the baseline and the candidate.
        </p>
      } @else {
        <!-- Split view (>= md): current on the left, proposed on the right -->
        <div class="diff-viewer__split" aria-hidden="true">
          <div class="diff-viewer__pane-head"><span>desired-state.yaml · CURRENT</span></div>
          <div class="diff-viewer__pane-head"><span>desired-state.yaml · PROPOSED</span></div>
          @for (hunk of hunks(); track $index) {
            <button
              type="button"
              class="diff-viewer__hunk diff-viewer__hunk--split"
              (click)="toggleHunk($index)"
              [attr.aria-expanded]="!isCollapsed($index)"
            >
              <span aria-hidden="true">{{ isCollapsed($index) ? '›' : '⌄' }}</span>
              {{ hunk.contextCount }} unchanged lines
            </button>
            @for (row of visibleSplit(hunk, $index); track $index) {
              <span class="diff-viewer__cell" [class]="'diff-viewer__cell--' + row.left.type">
                <span class="diff-viewer__ln">{{ row.left.num }}</span
                ><span class="diff-viewer__glyph" aria-hidden="true">{{ row.left.glyph }}</span
                ><span class="diff-viewer__text">{{ row.left.text }}</span>
              </span>
              <span class="diff-viewer__cell" [class]="'diff-viewer__cell--' + row.right.type">
                <span class="diff-viewer__ln">{{ row.right.num }}</span
                ><span class="diff-viewer__glyph" aria-hidden="true">{{ row.right.glyph }}</span
                ><span class="diff-viewer__text">{{ row.right.text }}</span>
              </span>
            }
          }
        </div>

        <!-- Unified view (< md, and the screen-reader-facing rendering) -->
        <pre
          class="diff-viewer__unified"
          tabindex="0"
          aria-label="Raw unified diff between the baseline and candidate desired state"
        ><code>@for (hunk of hunks(); track $index) {<button type="button" class="diff-viewer__hunk" (click)="toggleHunk($index)" [attr.aria-expanded]="!isCollapsed($index)"><span aria-hidden="true">{{ isCollapsed($index) ? '›' : '⌄' }}</span> {{ hunk.contextCount }} unchanged lines</button>@for (line of visibleUnified(hunk, $index); track $index) {<span class="diff-viewer__line" [class]="'diff-viewer__line--' + line.type"><span class="diff-viewer__glyph" aria-hidden="true">{{ line.glyph }}</span>{{ line.text }}
</span>}}</code></pre>
      }
    </section>
  `,
})
export class DesiredStateDiffViewerComponent {
  private readonly toast = inject(ToastService);

  /** The raw unified diff text (server-computed, LF-only). */
  readonly diff = input.required<string>();

  private readonly collapsed = signal<ReadonlySet<number>>(new Set());

  protected readonly hunks = computed<Hunk[]>(() => parseUnifiedDiff(this.diff()));
  protected readonly isEmpty = computed(() => this.hunks().length === 0);

  protected isCollapsed(index: number): boolean {
    return this.collapsed().has(index);
  }

  protected toggleHunk(index: number): void {
    const next = new Set(this.collapsed());
    if (next.has(index)) {
      next.delete(index);
    } else {
      next.add(index);
    }
    this.collapsed.set(next);
  }

  /** Context lines are hidden when a hunk is collapsed; +/− change lines always stay visible. */
  protected visibleUnified(hunk: Hunk, index: number): UnifiedLine[] {
    return this.isCollapsed(index)
      ? hunk.unified.filter((l) => l.type !== 'context')
      : hunk.unified;
  }

  protected visibleSplit(hunk: Hunk, index: number): { left: SplitCell; right: SplitCell }[] {
    return this.isCollapsed(index)
      ? hunk.split.filter((r) => r.left.type !== 'context' || r.right.type !== 'context')
      : hunk.split;
  }

  protected async onCopy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.diff());
      this.toast.success('Diff copied to the clipboard.');
    } catch {
      this.toast.error('Could not copy the diff to the clipboard.');
    }
  }
}

/** Parses a standard `@@ -a,b +c,d @@` unified diff into hunks with unified + split-view line models. */
function parseUnifiedDiff(diff: string): Hunk[] {
  const lines = diff.split('\n');
  const hunks: Hunk[] = [];
  let current: Hunk | null = null;
  let oldNum = 0;
  let newNum = 0;
  let pendingRemoves: SplitCell[] = [];
  let pendingAdds: SplitCell[] = [];

  const flush = () => {
    if (!current) {
      return;
    }
    const count = Math.max(pendingRemoves.length, pendingAdds.length);
    for (let i = 0; i < count; i++) {
      current.split.push({
        left: pendingRemoves[i] ?? { type: 'empty', num: null, glyph: '', text: '' },
        right: pendingAdds[i] ?? { type: 'empty', num: null, glyph: '', text: '' },
      });
    }
    pendingRemoves = [];
    pendingAdds = [];
  };

  for (const raw of lines) {
    if (raw.startsWith('@@')) {
      flush();
      // The server's UnifiedDiffFormatter always emits the ',count' segment, but tolerate git's convention
      // of omitting it when the count is 1 so a future formatter change can't silently mis-number lines.
      const match = /@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(raw);
      oldNum = match ? Number(match[1]) : 0;
      newNum = match ? Number(match[2]) : 0;
      current = { header: raw, contextCount: 0, unified: [], split: [] };
      hunks.push(current);
      continue;
    }
    if (!current || raw.length === 0) {
      continue;
    }

    const marker = raw[0];
    const text = raw.slice(1);
    if (marker === '+') {
      current.unified.push({ type: 'add', glyph: '+', oldNum: null, newNum, text });
      pendingAdds.push({ type: 'add', num: newNum, glyph: '+', text });
      newNum++;
    } else if (marker === '-') {
      current.unified.push({ type: 'remove', glyph: '−', oldNum, newNum: null, text });
      pendingRemoves.push({ type: 'remove', num: oldNum, glyph: '−', text });
      oldNum++;
    } else {
      // Context line: flush any pending add/remove run, then emit an aligned context row on both sides.
      flush();
      current.contextCount++;
      current.unified.push({ type: 'context', glyph: ' ', oldNum, newNum, text });
      current.split.push({
        left: { type: 'context', num: oldNum, glyph: '', text },
        right: { type: 'context', num: newNum, glyph: '', text },
      });
      oldNum++;
      newNum++;
    }
  }
  flush();
  return hunks;
}
