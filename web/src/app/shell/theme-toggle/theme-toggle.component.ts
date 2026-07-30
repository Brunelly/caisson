// Theme selector in the app chrome (Story #119 Task #126). A segmented `role="radiogroup"` control —
// not a dropdown — per the WAI-ARIA APG radio-group pattern: native `<button>`s give Enter/Space
// activation for free, arrow keys move AND select (radiogroup semantics, unlike a listbox), and there
// are no overlay-dismissal edge cases (no CDK Overlay needed for a 3-option always-visible control).
// See docs/adr/0034 / topology-search.component.ts for the CDK Overlay pattern this app uses instead
// for anchored dropdowns — not applicable here.
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { Theme } from '../../core/theme/theme.service';
import { ThemeService } from '../../core/theme/theme.service';

interface ThemeOption {
  readonly value: Theme;
  readonly label: string;
}

const THEME_OPTIONS: readonly ThemeOption[] = [
  { value: 'dark', label: 'Dark' },
  { value: 'light', label: 'Light' },
  { value: 'hc-dark', label: 'High contrast' },
];

@Component({
  selector: 'app-theme-toggle',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './theme-toggle.component.scss',
  template: `
    <div class="theme-toggle" role="radiogroup" aria-label="Theme">
      @for (option of options; track option.value) {
        <button
          type="button"
          class="theme-toggle__option"
          [class.theme-toggle__option--checked]="isChecked(option.value)"
          role="radio"
          [attr.aria-checked]="isChecked(option.value)"
          [attr.tabindex]="isChecked(option.value) ? 0 : -1"
          (click)="select(option.value)"
          (keydown)="onKeydown($event, option.value)"
        >
          {{ option.label }}
        </button>
      }
    </div>
  `,
})
export class ThemeToggleComponent {
  private readonly themeService = inject(ThemeService);

  protected readonly options = THEME_OPTIONS;

  protected isChecked(value: Theme): boolean {
    return this.themeService.theme() === value;
  }

  protected select(value: Theme): void {
    this.themeService.setTheme(value);
  }

  /** WAI-ARIA radiogroup pattern: arrow keys move focus AND select the newly focused option (unlike a
   * listbox, where arrows only move a separate "active" pointer). Home/End jump to the first/last
   * option. Enter/Space activation is native `<button>` behaviour and needs no handling here. */
  protected onKeydown(event: KeyboardEvent, current: Theme): void {
    const currentIndex = this.options.findIndex((option) => option.value === current);
    let nextIndex: number;

    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        nextIndex = (currentIndex + 1) % this.options.length;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        nextIndex = (currentIndex - 1 + this.options.length) % this.options.length;
        break;
      case 'Home':
        nextIndex = 0;
        break;
      case 'End':
        nextIndex = this.options.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    const next = this.options[nextIndex];
    this.select(next.value);
    this.focusOption(next.value, event.currentTarget as HTMLElement);
  }

  private focusOption(value: Theme, currentEl: HTMLElement): void {
    const container = currentEl.closest('.theme-toggle');
    const index = this.options.findIndex((option) => option.value === value);
    const target = container?.querySelectorAll<HTMLButtonElement>('.theme-toggle__option')[index];
    target?.focus();
  }
}
