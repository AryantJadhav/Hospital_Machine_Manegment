import { useEffect, useRef, useState } from 'react';
import { NavLink, useLocation } from 'react-router-dom';

const ITEMS = [
  { to: '/compliance', label: 'Compliance report' },
  { to: '/import', label: 'Import' },
  { to: '/export', label: 'Export data' },
  { to: '/staff', label: 'Staff' },
  { to: '/backups', label: 'Backups' },
  { to: '/diagnostics', label: 'Diagnostics' },
  { to: '/licence', label: 'Licence' },
  { to: '/updates', label: 'Updates' },
];

/**
 * The running-the-installation pages, under one menu.
 *
 * There were thirteen links in one row. At the width of an ordinary hospital
 * PC window the row ran off the edge, taking the account name and Sign out
 * with it. These are the ones used least and only by an Administrator, so
 * they go here; the daily work stays one click away in the bar.
 */
export function AdminMenu() {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const { pathname } = useLocation();

  // The button shows as current when the page underneath it is one of these,
  // so the bar still says where you are.
  const active = ITEMS.some((i) => pathname === i.to || pathname.startsWith(`${i.to}/`));

  useEffect(() => {
    if (!open) return;

    const onPointer = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };

    document.addEventListener('mousedown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div className="nav-menu" ref={root}>
      <button
        type="button"
        className={active ? 'nav-link nav-menu-button active' : 'nav-link nav-menu-button'}
        aria-haspopup="true"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        Admin <span aria-hidden="true">▾</span>
      </button>

      {open && (
        <div className="nav-menu-list">
          {ITEMS.map((i) => (
            <NavLink
              key={i.to}
              to={i.to}
              className={({ isActive }) => (isActive ? 'nav-menu-item active' : 'nav-menu-item')}
              onClick={() => setOpen(false)}
            >
              {i.label}
            </NavLink>
          ))}
        </div>
      )}
    </div>
  );
}
