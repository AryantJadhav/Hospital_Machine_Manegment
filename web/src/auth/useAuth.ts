import { useContext } from 'react';
import { AuthContext } from './context';
import type { AuthState } from './context';

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
