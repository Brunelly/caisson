// Client-side typeahead across servers/NIC MACs/switches/ports/VLANs (AC2). No /topology/search
// endpoint exists — the medium-rack cap keeps the whole graph resident client-side (ADR 0015), so this
// searches the in-memory index built from the current TopologyStateService graph.
//
// Interactive-UI baseline: results render in a CDK Overlay (hasBackdrop + backdropClick, scroll-strategy
// close()) rather than a hand-rolled document click listener; full ARIA combobox/listbox wiring;
// Escape/Tab/outside-click all close and (Escape) return focus to the input.
import { CdkConnectedOverlay, CdkOverlayOrigin, Overlay } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { findNodeById } from '../model/topology-graph-model';
import type { TopologyGraphNode } from '../model/topology-graph-model';
import { GROUP_LABELS, buildSearchIndex, groupByType, searchEntries } from './search-index';
import type { SearchIndexEntry } from './search-index';
import { TopologyStateService } from '../state/topology-state.service';

@Component({
  selector: 'app-topology-search',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, CdkConnectedOverlay, CdkOverlayOrigin],
  styleUrl: './topology-search.component.scss',
  template: `
    <div class="topology-search">
      <input
        #input
        cdkOverlayOrigin
        #origin="cdkOverlayOrigin"
        type="text"
        class="topology-search__input"
        role="combobox"
        aria-haspopup="listbox"
        aria-autocomplete="list"
        aria-label="Search topology by server, MAC, switch, port or VLAN"
        placeholder="Search servers, MACs, switches, ports, VLANs…"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-controls]="isOpen() ? listboxId : null"
        [attr.aria-activedescendant]="activeOptionId()"
        [(ngModel)]="queryModel"
        (input)="onInput()"
        (focus)="onFocus()"
        (keydown)="onKeydown($event)"
      />

      <ng-template
        cdkConnectedOverlay
        [cdkConnectedOverlayOrigin]="origin"
        [cdkConnectedOverlayOpen]="isOpen()"
        [cdkConnectedOverlayHasBackdrop]="true"
        cdkConnectedOverlayBackdropClass="cdk-overlay-transparent-backdrop"
        [cdkConnectedOverlayScrollStrategy]="scrollStrategy"
        [cdkConnectedOverlayWidth]="inputWidth()"
        (backdropClick)="close(true)"
        (detach)="close(false)"
      >
        <ul
          [id]="listboxId"
          class="topology-search__results"
          role="listbox"
          aria-label="Search results"
        >
          @for (group of groups(); track group.type) {
            <li
              class="topology-search__group"
              role="group"
              [attr.aria-label]="groupLabel(group.type)"
            >
              <div class="topology-search__group-heading">{{ groupLabel(group.type) }}</div>
              <ul class="topology-search__group-list" role="none">
                @for (result of group.entries; track result.id) {
                  <li
                    [id]="optionId(result.id)"
                    role="option"
                    [attr.aria-selected]="result.id === activeId()"
                    class="topology-search__option"
                    [class.topology-search__option--active]="result.id === activeId()"
                    [class.topology-search__option--mono]="
                      result.type !== 'server' && result.type !== 'switch'
                    "
                    (mousedown)="select(result); $event.preventDefault()"
                    (mouseenter)="activeId.set(result.id)"
                  >
                    {{ result.label }}
                  </li>
                }
              </ul>
            </li>
          } @empty {
            @if (queryModel.trim().length > 0) {
              <li class="topology-search__empty">No matches for "{{ queryModel }}"</li>
            }
          }
        </ul>
      </ng-template>
    </div>
  `,
})
export class TopologySearchComponent {
  private readonly state = inject(TopologyStateService);
  private readonly overlay = inject(Overlay);

  readonly resultSelected = output<TopologyGraphNode>();

  protected readonly listboxId = 'topology-search-listbox';
  protected readonly scrollStrategy = this.overlay.scrollStrategies.close();

  protected readonly inputRef = viewChild.required<ElementRef<HTMLInputElement>>('input');
  protected readonly inputWidth = signal<number>(280);

  queryModel = '';
  private readonly query = signal('');
  protected readonly isOpen = signal(false);
  protected readonly activeId = signal<string | null>(null);

  private readonly index = computed(() => {
    const graph = this.state.graph();
    return graph ? buildSearchIndex(graph) : [];
  });

  protected readonly groups = computed(() =>
    groupByType(searchEntries(this.index(), this.query())),
  );
  private readonly flatEntries = computed(() => this.groups().flatMap((g) => g.entries));

  protected readonly activeOptionId = computed(() => {
    const id = this.activeId();
    return id ? this.optionId(id) : null;
  });

  private readonly queryInput$ = new Subject<string>();

  constructor() {
    this.queryInput$
      .pipe(debounceTime(150), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe((value) => {
        this.query.set(value);
        this.isOpen.set(value.trim().length > 0);
        this.activeId.set(this.flatEntries()[0]?.id ?? null);
      });

    // Keep the active option valid whenever the result set changes underneath the user (typing, or
    // the underlying graph refreshing live) rather than pointing at a stale/removed entry.
    effect(() => {
      const entries = this.flatEntries();
      const current = this.activeId();
      if (current && !entries.some((e) => e.id === current)) {
        this.activeId.set(entries[0]?.id ?? null);
      }
    });
  }

  protected optionId(id: string): string {
    return `topology-search-option-${id.replace(/[^a-zA-Z0-9_-]/g, '_')}`;
  }

  protected groupLabel(type: SearchIndexEntry['type']): string {
    return GROUP_LABELS[type];
  }

  protected onInput(): void {
    this.inputWidth.set(this.inputRef().nativeElement.getBoundingClientRect().width || 280);
    this.queryInput$.next(this.queryModel);
  }

  /** Set for the duration of a programmatic refocus (Escape/select) so that focus event doesn't
   * immediately reopen the panel via onFocus — genuine user refocus (e.g. tabbing back in) still does. */
  private suppressNextFocusReopen = false;

  protected onFocus(): void {
    if (this.suppressNextFocusReopen) {
      this.suppressNextFocusReopen = false;
      return;
    }
    if (this.queryModel.trim().length > 0) {
      this.isOpen.set(true);
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.moveActive(1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.moveActive(-1);
        break;
      case 'Enter': {
        const active = this.currentActiveEntry();
        if (active) {
          event.preventDefault();
          this.select(active);
        }
        break;
      }
      // Space is intentionally left untouched: this is a combobox INPUT, not a discrete listbox/menu,
      // and queries like "vlan 120" (AC2) require typing a literal space.
      case 'Escape':
        event.preventDefault();
        this.close(true);
        break;
      case 'Tab':
        this.close(false);
        break;
    }
  }

  /** CDK backdropClick/detach also call this; `refocusInput` is false there since the input never
   * lost focus in the outside-click case, and true for Escape, which must return focus explicitly. */
  protected close(refocusInput: boolean): void {
    this.isOpen.set(false);
    if (refocusInput) {
      this.suppressNextFocusReopen = true;
      this.inputRef().nativeElement.focus();
    }
  }

  protected select(entry: SearchIndexEntry): void {
    const graph = this.state.graph();
    const node = graph ? findNodeById(graph, entry.id) : null;
    if (node) {
      this.resultSelected.emit(node);
    }
    this.queryModel = entry.label;
    this.close(true);
  }

  private currentActiveEntry(): SearchIndexEntry | undefined {
    const id = this.activeId();
    return this.flatEntries().find((e) => e.id === id);
  }

  private moveActive(delta: 1 | -1): void {
    const entries = this.flatEntries();
    if (entries.length === 0) {
      return;
    }

    const currentIndex = entries.findIndex((e) => e.id === this.activeId());
    const nextIndex = (currentIndex + delta + entries.length) % entries.length;
    this.activeId.set(entries[nextIndex].id);
    this.isOpen.set(true);
  }
}
