import { createContext } from 'react';

export type CurrentUser = {
  userName: string | null;
  fullName: string | null;
  tenantId: string | null;
  roles: string[];
};

export type AuthState = {
  user: CurrentUser | null;
  loading: boolean;
  login: (userName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** True when the user holds any of the given roles. */
  can: (...roles: string[]) => boolean;
};

// Kept apart from the provider component so the module holding it exports
// no components, which is what React Fast Refresh needs to work.
export const AuthContext = createContext<AuthState | null>(null);

export const ROLES = {
  admin: 'Admin',
  biomedicalHead: 'BiomedicalHead',
  seniorEngineer: 'SeniorEngineer',
  technician: 'Technician',
} as const;
