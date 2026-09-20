/**
 * Light or dark, chosen by the person and remembered on this machine.
 *
 * Never taken from the operating system: a shared ward PC that happens to be in
 * Windows dark mode should not change how the product looks. Light is the default
 * and the one used in print.
 */

export type Theme = 'light' | 'dark';

const KEY = 'hospitalpm.theme';

export function storedTheme(): Theme {
  try {
    return localStorage.getItem(KEY) === 'dark' ? 'dark' : 'light';
  } catch {
    // Private window or blocked storage: light, and the choice lasts until reload.
    return 'light';
  }
}

export function applyTheme(theme: Theme): void {
  if (theme === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
  else document.documentElement.removeAttribute('data-theme');
}

export function saveTheme(theme: Theme): void {
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    /* a convenience, not data */
  }
}
