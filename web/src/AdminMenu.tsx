import { useEffect, useRef, useState } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { PERMISSIONS } from './auth/context';
import { useAuth } from './auth/useAuth';
import type { Features } from './features';

// A feature here can be switched off for everyone in the settings file. It is offered only while it is on.
// Each is offered only to someone who may use it.
const ITEMS: { to: string; label: string; permission: string; feature?: keyof Features }[] = [
  { to: '/reports', label: 'Reports', permission: PERMISSIONS.reportsView },
  { to: '/compliance', label: 'Compliance report', permission: PERMISSIONS.reportsView },
  { to: '/import', label: 'Import', permission: PERMISSIONS.dataImport, feature: 'import' },
  { to: '/export', label: 'Export data', permission: PERMISSIONS.dataExport, feature: 'export' },
  { to: '/staff', label: 'Staff', permission: PERMISSIONS.staffManage },
  { to: '/access', label: 'Access', permission: PERMISSIONS.accessManage },
  { to: '/backups', label: 'Backups', permission: PERMISSIONS.systemBackups, feature: 'backups' },
  { to: '/diagnostics', label: 'Diagnostics', permission: PERMISSIONS.systemDiagnostics },
  { to: '/licence', label: 'Licence', permission: PERMISSIONS.systemLicence },
  { to: '/audit', label: 'Audit log', permission: PERMISSIONS.auditView },
  { to: '/updates', label: 'Updates', permission: PERMISSIONS.systemUpdates, feature: 'updates' },
];

/**
 * The running-the-installation pages, under one menu.
 *
 * There were thirteen links in one row. At the width of an ordinary hospital
 * PC window the row ran off the edge, taking the account name and Sign out
 * with it. These are the ones used least and only by an Administrator, so
 * they go here; the daily work stays one click away in the bar.
 */
export function AdminMenu({ features }: { features: Features | null }) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const { pathname } = useLocation();
  // Until the switches are known, only what is never switched off.
  const { may } = useAuth();
  const items = ITEMS.filter((i) => may(i.permission) && (!i.feature || features?.[i.feature] === true));

  // The button shows as current when the page underneath it is one of these,
  // so the bar still says where you are.
  const active = items.some((i) => pathname === i.to || pathname.startsWith(`${i.to}/`));

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

  // Someone who may use none of these has no menu.
  if (items.length === 0) return null;

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
          {items.map((i) => (
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
