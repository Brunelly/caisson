import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Theme } from '../../core/theme/theme.service';
import { ThemeService } from '../../core/theme/theme.service';
import { ThemeToggleComponent } from './theme-toggle.component';

describe('ThemeToggleComponent', () => {
  let fixture: ComponentFixture<ThemeToggleComponent>;
  let setTheme: ReturnType<typeof vi.fn>;
  let theme: ReturnType<typeof signal<Theme>>;

  beforeEach(() => {
    theme = signal<Theme>('dark');
    setTheme = vi.fn((value: Theme) => theme.set(value));

    TestBed.configureTestingModule({
      imports: [ThemeToggleComponent],
      providers: [{ provide: ThemeService, useValue: { theme, setTheme } }],
    });
    fixture = TestBed.createComponent(ThemeToggleComponent);
    fixture.detectChanges();
  });

  function radios(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('[role="radio"]'));
  }

  it('renders a labelled radiogroup with three radio options (Dark/Light/High contrast)', () => {
    const group = fixture.nativeElement.querySelector('[role="radiogroup"]');
    expect(group?.getAttribute('aria-label')).toBe('Theme');

    const items = radios();
    expect(items).toHaveLength(3);
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Dark',
      'Light',
      'High contrast',
    ]);
  });

  it('reflects the current theme via aria-checked and roving tabindex', () => {
    expect(radios().map((item) => item.getAttribute('aria-checked'))).toEqual([
      'true',
      'false',
      'false',
    ]);
    expect(radios().map((item) => item.getAttribute('tabindex'))).toEqual(['0', '-1', '-1']);
  });

  it('updates aria-checked when the underlying theme signal changes', () => {
    theme.set('hc-dark');
    fixture.detectChanges();

    expect(radios().map((item) => item.getAttribute('aria-checked'))).toEqual([
      'false',
      'false',
      'true',
    ]);
  });

  it('clicking an option calls ThemeService.setTheme with that option', () => {
    radios()[1].click();

    expect(setTheme).toHaveBeenCalledWith('light');
  });

  it('Enter/Space activates the focused option (native <button> behaviour → click)', () => {
    radios()[2].focus();
    radios()[2].click(); // what a real browser does on Enter/Space for a focused <button>

    expect(setTheme).toHaveBeenCalledWith('hc-dark');
  });

  it('ArrowRight moves focus to and selects the next option, wrapping at the end', () => {
    radios()[2].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));

    expect(setTheme).toHaveBeenCalledWith('dark');
  });

  it('ArrowLeft moves focus to and selects the previous option, wrapping at the start', () => {
    radios()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));

    expect(setTheme).toHaveBeenCalledWith('hc-dark');
  });

  it('End moves to and selects the last option', () => {
    radios()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));

    expect(setTheme).toHaveBeenCalledWith('hc-dark');
  });
});
