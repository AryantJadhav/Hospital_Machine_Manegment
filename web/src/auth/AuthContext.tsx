import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { api, clearTokens, getRefreshToken, restoreSession, storeTokens } from '../api/client';
import type { TokenResponse } from '../api/client';
import { AuthContext, ROLES } from './context';
import type { CurrentUser } from './context';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [signInNotice, setSignInNotice] = useState<string | null>(null);

  // On load, try the stored refresh token before showing the login form, so
  // reopening a tablet mid-shift does not look like being logged out.
  useEffect(() => {
    let cancelled = false;

    (async () => {
      if (await restoreSession()) {
        try {
          const me = await api.get<CurrentUser>('/api/auth/me');
          if (!cancelled) setUser(me);
        } catch {
          clearTokens();
        }
      }
      if (!cancelled) setLoading(false);
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const login = useCallback(async (userName: string, password: string, expecting?: string) => {
    const tokens = await api.post<TokenResponse>('/api/auth/login', { userName, password });
    storeTokens(tokens);
    const me = await api.get<CurrentUser>('/api/auth/me');

    // The chooser on the login screen is a signpost, not a gate. What someone
    // may do comes from their account and never from which button they
    // pressed, so a mismatch is not a failure and refusing it would only add
    // a way to be stuck. It is said out loud instead, because a technician who
    // pressed "Administration" and then cannot find the Staff tab deserves to
    // know why rather than to file a bug.
    //
    // Note this is decided after authentication, so it tells a stranger
    // nothing: you have to already hold the password to see it.
    setSignInNotice(mismatchNotice(expecting, me));
    setUser(me);
  }, []);

  const logout = useCallback(async () => {
    const refreshToken = getRefreshToken();
    try {
      if (refreshToken) await api.post('/api/auth/logout', { refreshToken });
    } catch {
      // Revoking server-side is best effort. Clearing locally must happen
      // regardless, or a failed network call leaves the user apparently
      // signed in.
    }
    clearTokens();
    setUser(null);
    setSignInNotice(null);
  }, []);

  const dismissNotice = useCallback(() => setSignInNotice(null), []);

  const can = useCallback(
    (...roles: string[]) => (user ? roles.some((r) => user.roles.includes(r)) : false),
    [user],
  );

  const may = useCallback(
    (...permissions: string[]) => (user ? permissions.some((p) => user.permissions.includes(p)) : false),
    [user],
  );

  const value = useMemo(
    () => ({ user, loading, login, logout, can, may, signInNotice, dismissNotice }),
    [user, loading, login, logout, can, may, signInNotice, dismissNotice],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

function mismatchNotice(expecting: string | undefined, me: CurrentUser): string | null {
  if (!expecting || me.roles.includes(expecting)) return null;

  return me.roles.includes(ROLES.admin)
    ? 'You chose the employee sign-in. This is an administrator account, so you have full access.'
    : 'You chose the administration sign-in. This is an employee account, so administration is not available.';
}
