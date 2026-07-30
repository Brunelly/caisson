// Independent DriftSeverity -> badge mapping (ADR 0033): High -> red/unmapped tokens + a critical
// glyph, Medium -> amber/ambiguous tokens, Low -> neutral/muted tokens — deliberately NOT a direct bind
// of `severity` onto StatusBadgeComponent's confidence-band kinds, since confidence High renders green
// ("good") while drift severity High is "bad". Built on the shared, token-only StatusBadgeComponent so
// colours/icons never drift from the rest of the app's badge vocabulary.
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { SeverityBadgeKind } from '../../shared/badge/status-badge.component';
import type { DriftSeverity } from '../model/drift-contracts';

const SEVERITY_BADGE_KIND: Record<DriftSeverity, SeverityBadgeKind> = {
  High: 'severity-high',
  Medium: 'severity-medium',
  Low: 'severity-low',
};

@Component({
  selector: 'app-drift-severity-badge',
  standalone: true,
  imports: [StatusBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<app-status-badge [kind]="badgeKind()" />`,
})
export class DriftSeverityBadgeComponent {
  readonly severity = input.required<DriftSeverity>();
  protected readonly badgeKind = computed(() => SEVERITY_BADGE_KIND[this.severity()]);
}
