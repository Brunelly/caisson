// The pre-flight validation results panel (story #170, AC4/NFR6). Presentational + focus behaviour only —
// it reads the issue set from NetworkIntentStateService and emits (revalidate)/(issueSelected) for the shell
// to act on; it owns no HTTP. Errors are grouped from Warnings and Safety notices (safety.* codes) each with
// a count chip; every issue row is a button showing a severity DOT + TEXT label (colour is never the sole
// indicator, NFR6) and a monospace RFC 6901 field path. Errors announce via an assertive live region and
// focus moves to the first error after each validation; warnings/safety announce politely. While validating,
// rows are replaced by three shimmer skeleton bars; a clean run shows a success mark.
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { isSafetyIssue, type ValidationIssue } from '../model/preflight-validation-contracts';
import { NetworkIntentStateService } from '../state/network-intent-state.service';

interface IssueGroup {
  key: 'errors' | 'warnings' | 'safety';
  title: string;
  severityLabel: string;
  issues: ValidationIssue[];
}

@Component({
  selector: 'app-validation-issues-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './validation-issues-panel.component.scss',
  template: `
    <section class="panel" aria-labelledby="validation-issues-title">
      <header class="panel__head">
        <div class="panel__title-group">
          <h2 class="panel__title" id="validation-issues-title">Validation issues</h2>
          <p class="panel__stamp">{{ stampText() }}</p>
        </div>
        @if (canRevalidate()) {
          <button
            type="button"
            class="panel__action"
            aria-label="Re-validate configuration"
            [disabled]="state.validating()"
            (click)="revalidate.emit()"
          >
            <svg
              class="panel__action-icon"
              width="16"
              height="16"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.75"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M21 12a9 9 0 1 1-2.64-6.36L21 8" />
              <path d="M21 3v5h-5" />
            </svg>
            <span>{{ state.validating() ? 'Validating…' : 'Re-validate' }}</span>
          </button>
        }
      </header>

      @if (state.validating()) {
        <div class="panel__loading" role="status" aria-live="polite">
          <span class="panel__sr-only">Validating configuration…</span>
          <span class="panel__skeleton"></span>
          <span class="panel__skeleton"></span>
          <span class="panel__skeleton"></span>
        </div>
      } @else if (state.preflightStatus() === 'idle') {
        <div class="panel__empty" role="status">
          <p>Run validation to check this configuration against the current rack topology.</p>
        </div>
      } @else if (isClean()) {
        <div class="panel__clean" role="status">
          <svg
            class="panel__clean-icon"
            width="28"
            height="28"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M20 6 9 17l-5-5" />
          </svg>
          <p class="panel__clean-text">No issues found</p>
        </div>
      } @else {
        <div class="panel__body" #panelBody>
          @for (group of groups(); track group.key) {
            @if (group.issues.length > 0) {
              <section
                class="group"
                [class]="'group--' + group.key"
                [attr.role]="group.key === 'errors' ? 'alert' : 'status'"
                [attr.aria-live]="group.key === 'errors' ? 'assertive' : 'polite'"
                [attr.aria-labelledby]="'group-' + group.key + '-title'"
              >
                <div class="group__head">
                  <h3 class="group__title" [id]="'group-' + group.key + '-title'">
                    {{ group.title }}
                  </h3>
                  <span
                    class="group__count"
                    [attr.aria-label]="group.issues.length + ' ' + group.title"
                  >
                    {{ group.issues.length }}
                  </span>
                </div>
                <ul class="group__list">
                  @for (issue of group.issues; track issue.fieldPath + '|' + issue.code) {
                    <li>
                      <button
                        type="button"
                        class="issue"
                        [attr.data-group]="group.key"
                        (click)="issueSelected.emit(issue)"
                      >
                        <span class="issue__dot" aria-hidden="true"></span>
                        <span class="issue__severity">{{ group.severityLabel }}</span>
                        <span class="issue__body">
                          <span class="issue__message">{{ issue.message }}</span>
                          <code class="issue__path">{{ issue.fieldPath }}</code>
                        </span>
                        <span class="issue__chevron" aria-hidden="true">›</span>
                      </button>
                    </li>
                  }
                </ul>
              </section>
            }
          }
        </div>
      }
    </section>
  `,
})
export class ValidationIssuesPanelComponent {
  protected readonly state = inject(NetworkIntentStateService);

  /** Whether the Re-validate action is shown (hidden for read-only principals who cannot run validation). */
  readonly canRevalidate = input(true);

  /** The user asked to (re-)run validation. */
  readonly revalidate = output<void>();

  /** The user selected an issue to navigate to its offending control. */
  readonly issueSelected = output<ValidationIssue>();

  private readonly body = viewChild<ElementRef<HTMLElement>>('panelBody');

  protected readonly groups = computed<IssueGroup[]>(() => {
    const warnings = this.state.issueWarnings();
    return [
      { key: 'errors', title: 'Errors', severityLabel: 'Error', issues: this.state.issueErrors() },
      {
        key: 'warnings',
        title: 'Warnings',
        severityLabel: 'Warning',
        issues: warnings.filter((w) => !isSafetyIssue(w)),
      },
      {
        key: 'safety',
        title: 'Safety notices',
        severityLabel: 'Safety',
        issues: warnings.filter(isSafetyIssue),
      },
    ];
  });

  protected readonly isClean = computed(
    () =>
      this.state.preflightStatus() === 'validated' &&
      this.state.issueErrors().length === 0 &&
      this.state.issueWarnings().length === 0,
  );

  private lastFocusedRunId: string | null = null;

  constructor() {
    // Move focus to the first error the moment a validation with errors completes (AC6). Guarded by the
    // run id so it fires once per validation, never on unrelated change detection.
    effect(() => {
      const runId = this.state.validationRunId();
      if (
        this.state.preflightStatus() === 'validated' &&
        this.state.issueErrors().length > 0 &&
        runId !== null &&
        runId !== this.lastFocusedRunId
      ) {
        this.lastFocusedRunId = runId;
        queueMicrotask(() => {
          const host = this.body()?.nativeElement ?? document;
          host.querySelector<HTMLButtonElement>('.issue[data-group="errors"]')?.focus();
        });
      }
    });
  }

  protected stampText(): string {
    const at = this.state.lastValidatedAtUtc();
    if (at === null) {
      return 'Not yet validated';
    }
    const date = new Date(at);
    if (Number.isNaN(date.getTime())) {
      return 'Not yet validated';
    }
    return `Last validated ${date.toLocaleString()}`;
  }
}
