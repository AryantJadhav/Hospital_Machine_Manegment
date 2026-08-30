import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { api, clearTokens, getRefreshToken, restoreSession, storeTokens } from '../api/client';
import type { TokenResponse } from '../api/client';
import { AuthContext } from './context';
import type { CurrentUser } from './context';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [loading, setLoading] = useState(true);

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

  const login = useCallback(async (userName: string, password: string) => {
    const tokens = await api.post<TokenResponse>('/api/auth/login', { userName, password });
    storeTokens(tokens);
    setUser(await api.get<CurrentUser>('/api/auth/me'));
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
  }, []);

  const can = useCallback(
    (...roles: string[]) => (user ? roles.some((r) => user.roles.includes(r)) : false),
    [user],
  );

  const value = useMemo(
    () => ({ user, loading, login, logout, can }),
    [user, loading, login, logout, can],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
