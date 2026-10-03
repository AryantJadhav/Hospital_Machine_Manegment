import { createContext } from 'react';

export type CurrentUser = {
  userName: string | null;
  fullName: string | null;
  tenantId: string | null;
  roles: string[];
  /** What the server will let this person do. The screen shows what is allowed and no more. */
  permissions: string[];
  /** For a department user: the places they were given, so the screen can say whose equipment it shows. */
  departments: string[];
  /** The roles this person may give to an account on the Staff page. */
  manageableRoles: string[];
  /** The roles whose accounts this person may stop from signing in, and let back in. */
  pausableRoles: string[];
};

export type AuthState = {
  user: CurrentUser | null;
  loading: boolean;
  login: (userName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** True when the user holds any of the given roles. */
  can: (...roles: string[]) => boolean;
  /** True when the user holds any of the given permissions. Use this, not a role, to decide what to show. */
  may: (...permissions: string[]) => boolean;
};

// Kept apart from the provider component so the module holding it exports
// no components, which is what React Fast Refresh needs to work.
export const AuthContext = createContext<AuthState | null>(null);

/**
 * The five kinds of user (HospitalPm.Domain.Identity.Roles). A screen asks what a person may do
 * (`PERMISSIONS`), not which role they hold; the role is for naming the person and for the Staff
 * page. The server enforces all of it - everything here is presentation.
 */
export const ROLES = {
  developer: 'Developer',
  itAdmin: 'ItAdmin',
  bmeHead: 'BmeHead',
  bmeEngineer: 'BmeEngineer',
  departmentUser: 'DepartmentUser',
} as const;

/** What a person reads on screen. The stored name has no spaces. */
export const ROLE_LABEL: Record<string, string> = {
  Developer: 'Developer',
  ItAdmin: 'IT team',
  BmeHead: 'Head of Biomedical',
  BmeEngineer: 'Biomedical engineer',
  DepartmentUser: 'Department user',
};

/** One line on what each role is for. */
export const ROLE_HELP: Record<string, string> = {
  Developer: 'Built and supports the software. Full access.',
  ItAdmin: "The hospital's IT team: staff accounts, backups, updates, the licence and diagnostics.",
  BmeHead: 'Head of Biomedical: the register, schedules, checklists, spare parts, training, reports and staff.',
  BmeEngineer: 'Works the floor: PM rounds, faults, and reading the register. Cannot change what the department has committed to.',
  DepartmentUser: 'Reports faults on the equipment of their own departments, and follows them. Sees nothing outside the departments they are given.',
};

/**
 * What a person may do, named by the thing done. These are the server's own names
 * (HospitalPm.Domain.Identity.Permissions); a screen asks for one of these rather than for a role,
 * so who holds what is decided in one place on the server.
 */
export const PERMISSIONS = {
  registerView: 'register.view',
  departmentView: 'department.view',
  workOrdersNote: 'work-orders.note',
  sparePartsView: 'spare-parts.view',
  checklistsView: 'checklists.view',
  trainingView: 'training.view',
  pmWork: 'pm.work',
  gatePassView: 'gate-pass.view',
  gatePassEdit: 'gate-pass.edit',
  workOrdersView: 'work-orders.view',
  workOrdersReport: 'work-orders.report',
  workOrdersWork: 'work-orders.work',
  equipmentMove: 'equipment.move',
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
  accessManage: 'access.manage',
  systemBackups: 'system.backups',
  systemRestore: 'system.restore',
  systemUpdates: 'system.updates',
  systemDiagnostics: 'system.diagnostics',
  systemLicence: 'system.licence',
  auditView: 'audit.view',
} as const;
