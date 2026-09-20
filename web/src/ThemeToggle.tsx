import { useState } from 'react';
import { applyTheme, saveTheme, storedTheme } from './theme';
import type { Theme } from './theme';

/** Sits in the navigation bar. Says what pressing it does, not what is on now. */
export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(storedTheme);

  function toggle() {
    const next: Theme = theme === 'dark' ? 'light' : 'dark';
    applyTheme(next);
    saveTheme(next);
    setTheme(next);
  }

  return (
    <button
      className="btn btn-quiet"
      onClick={toggle}
      aria-pressed={theme === 'dark'}
      title="Switch between the light and dark look. Remembered on this computer."
    >
      {theme === 'dark' ? 'Light look' : 'Dark look'}
    </button>
  );
}
