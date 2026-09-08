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
