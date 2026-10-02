import { createContext } from 'react';

export type CurrentUser = {
  userName: string | null;
  fullName: string | null;
  tenantId: string | null;
  roles: string[];
  /** What the server will let this person do. The screen shows what is allowed and no more. */
  permissions: string[];
};

export type AuthState = {
  user: CurrentUser | null;
  loading: boolean;
  /**
   * `expecting` is the role the login screen's chooser was set to. It changes
   * nothing about the sign-in — it only decides whether the shell says
   * afterwards that the account turned out to be the other kind.
   */
  login: (userName: string, password: string, expecting?: string) => Promise<void>;
  logout: () => Promise<void>;
  /** True when the user holds any of the given roles. */
  can: (...roles: string[]) => boolean;
  /** True when the user holds any of the given permissions. Use this, not a role, to decide what to show. */
  may: (...permissions: string[]) => boolean;
  /** Set when the chooser and the account disagreed. Shown once, then dismissed. */
  signInNotice: string | null;
  dismissNotice: () => void;
};

// Kept apart from the provider component so the module holding it exports
// no components, which is what React Fast Refresh needs to work.
export const AuthContext = createContext<AuthState | null>(null);

/**
 * Two roles, not four.
 *
 * An Employee records what they did; an Admin decides what gets done. The
 * server enforces the same split — everything here is presentation, so a page
 * that forgets a check hides a button rather than opening a door.
 */
export const ROLES = {
  admin: 'Admin',
  employee: 'Employee',
} as const;

/**
 * What a person may do, named by the thing done. These are the server's own names
 * (HospitalPm.Domain.Identity.Permissions); a screen asks for one of these rather than for a role,
 * so who holds what is decided in one place on the server.
 */
export const PERMISSIONS = {
  equipmentEdit: 'equipment.edit',
  equipmentTypesEdit: 'equipment-types.edit',
  locationsEdit: 'locations.edit',
  labelsPrint: 'labels.print',
  dataImport: 'data.import',
  dataExport: 'data.export',
  checklistsEdit: 'checklists.edit',
  pmManage: 'pm.manage',
  workOrdersAssign: 'work-orders.assign',
  workOrdersCancel: 'work-orders.cancel',
  attachmentsDelete: 'attachments.delete',
  sparePartsEdit: 'spare-parts.edit',
  trainingEdit: 'training.edit',
  reportsView: 'reports.view',
  staffManage: 'staff.manage',
  systemBackups: 'system.backups',
  systemRestore: 'system.restore',
  systemUpdates: 'system.updates',
  systemDiagnostics: 'system.diagnostics',
  systemLicence: 'system.licence',
} as const;
