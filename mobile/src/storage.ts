import * as SecureStore from 'expo-secure-store';

/**
 * Persisted app state.
 *
 * Tokens go in SecureStore (Keychain on iOS, Keystore on Android) rather
 * than AsyncStorage. A ward tablet is shared, occasionally lost, and rarely
 * has a screen lock worth the name — plain storage would hand a refresh
 * token to anyone who picks it up.
 *
 * The server URL is not a secret but lives here too, so a factory reset of
 * the app clears everything in one place.
 */
const SERVER_URL = 'hospitalpm.serverUrl';
const REFRESH_TOKEN = 'hospitalpm.refreshToken';

async function get(key: string): Promise<string | null> {
  try {
    return await SecureStore.getItemAsync(key);
  } catch {
    // A device with no secure hardware, or a keychain the OS has locked,
    // must not crash the app on launch.
    return null;
  }
}

async function set(key: string, value: string | null): Promise<void> {
  try {
    if (value === null) await SecureStore.deleteItemAsync(key);
    else await SecureStore.setItemAsync(key, value);
  } catch {
    /* storage unavailable; the session stays in memory for this run */
  }
}

export const storage = {
  getServerUrl: () => get(SERVER_URL),
  setServerUrl: (url: string | null) => set(SERVER_URL, url),
  getRefreshToken: () => get(REFRESH_TOKEN),
  setRefreshToken: (token: string | null) => set(REFRESH_TOKEN, token),
};

/**
 * Normalises whatever the operator typed into something fetch can use.
 *
 * Biomedical staff type "192.168.1.50", "hospitalpm.local/", and
 * "http://192.168.1.50:5000/" interchangeably. Rejecting any of those as
 * malformed would be a support call on the first day of a pilot.
 */
export function normaliseServerUrl(raw: string): string | null {
  const trimmed = raw.trim().replace(/\/+$/, '');
  if (trimmed.length === 0) return null;

  const withScheme = /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;

  try {
    const url = new URL(withScheme);
    if (!url.hostname) return null;
    return `${url.protocol}//${url.host}`;
  } catch {
    return null;
  }
}
