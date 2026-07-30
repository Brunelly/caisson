// Presentational errors/warnings summary for the desired-state YAML round-trip (story #169, AC3/AC4/NFR5).
// Errors are announced in an assertive live region and focus moves to the summary heading so a screen-reader
// user hears them immediately; warnings (e.g. "comments not preserved") are a polite live region. No HTTP,
// no state — the parent (import dialog / preview) feeds it the current issues and warnings.
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  viewChild,
} from '@angular/core';
import type { DesiredStateImportIssueDto } from '../model/network-intent-contracts';

@Component({
  selector: 'app-validation-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './validation-summary.component.scss',
  template: `
    @if (errors().length > 0) {
      <div class="validation-summary__errors" role="alert" aria-live="assertive">
        <h3 #errorsHeading tabindex="-1" class="validation-summary__heading">
          {{ errors().length }} problem{{ errors().length === 1 ? '' : 's' }} prevented import
        </h3>
        <ul class="validation-summary__list">
          @for (issue of errors(); track $index) {
            <li>
              <code class="validation-summary__path">{{ issue.path }}</code>
              — {{ issue.message }}{{ position(issue) }}
            </li>
          }
        </ul>
      </div>
    }

    @if (warnings().length > 0) {
      <div class="validation-summary__warnings" role="status" aria-live="polite">
        <ul class="validation-summary__list">
          @for (warning of warnings(); track warning) {
            <li>{{ warningText(warning) }}</li>
          }
        </ul>
      </div>
    }
  `,
})
export class ValidationSummaryComponent {
  readonly errors = input<DesiredStateImportIssueDto[]>([]);
  readonly warnings = input<string[]>([]);

  private readonly errorsHeading = viewChild<ElementRef<HTMLElement>>('errorsHeading');

  constructor() {
    // Move focus to the summary heading the moment errors appear (NFR5), so the assertive live region and
    // the first failing path are the next thing a keyboard/screen-reader user lands on.
    effect(() => {
      if (this.errors().length > 0) {
        this.errorsHeading()?.nativeElement.focus();
      }
    });
  }

  protected position(issue: DesiredStateImportIssueDto): string {
    if (issue.line == null) {
      return '';
    }
    return issue.column == null
      ? ` (line ${issue.line})`
      : ` (line ${issue.line}, column ${issue.column})`;
  }

  protected warningText(code: string): string {
    return code === 'commentsNotPreserved'
      ? 'Comments are not preserved in v1 and were dropped from the imported document.'
      : code;
  }
}
