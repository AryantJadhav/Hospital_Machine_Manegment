import { storage } from './storage';

/**
 * API client for the mobile app.
 *
 * Mirrors the web client's approach: access token in memory, refresh token
 * in secure storage, and a single de-duplicated refresh. The server rotates
 * refresh tokens and treats reuse as a leak, so two concurrent refreshes
 * would revoke the chain and sign the technician out mid-round.
 */

let accessToken: string | null = null;
let serverUrl: string | null = null;
let refreshInFlight: Promise<boolean> | null = null;

export type Tokens = {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
};

export type Equipment = {
  id: number;
  assetTag: string;
  serialNumber: string | null;
  equipmentTypeName: string;
  locationName: string;
  manufacturer: string | null;
  model: string | null;
  status: number;
  purchaseDate: string | null;
  installationDate: string | null;
  warrantyExpiryDate: string | null;
  notes: string | null;
};

export type ChecklistItem = {
  key: string;
  label: string;
  type: number;
  required: boolean;
  guidance?: string | null;
  unit?: string | null;
  min?: number | null;
  max?: number | null;
  options?: string[] | null;
};

export type ChecklistSection = { title: string; items: ChecklistItem[] };

export type PmTask = {
  id: number;
  dueDate: string;
  status: number;
  assetTag: string;
  checklistName: string;
  daysLate: number;
};

export type PmForm = {
  id: number;
  dueDate: string;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  checklistTemplateVersionId: number;
  versionNo: number;
  definition: { sections: ChecklistSection[] };
};

/** Mirrors ChecklistItemType on the server. */
export const ITEM_TYPE = {
  passFail: 10,
  yesNo: 20,
  number: 30,
  text: 40,
  choice: 50,
} as const;

export const PM_STATUS_LABEL: Record<number, string> = {
  10: 'Scheduled',
  20: 'Due',
  30: 'Overdue',
  40: 'Completed',
  50: 'Skipped',
};

export const STATUS_LABEL: Record<number, string> = {
  10: 'In store',
  20: 'In service',
  30: 'Under repair',
  40: 'Condemned',
  50: 'Disposed',
};

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export function setServerUrl(url: string | null) {
  serverUrl = url;
}

export function getServerUrl(): string | null {
  return serverUrl;
}

export function setAccessToken(token: string | null) {
  accessToken = token;
}

export async function storeTokens(tokens: Tokens) {
  accessToken = tokens.accessToken;
  await storage.setRefreshToken(tokens.refreshToken);
}

export async function clearSession() {
  accessToken = null;
  await storage.setRefreshToken(null);
}

/**
 * Extracts an asset tag from a scanned barcode.
 *
 * Mirrors AssetTagPayload.Extract on the server. Labels encode a URL so a
 * plain camera app can open the equipment page, but the tag lives in the
 * path — a label glued to a ventilator has to keep working after somebody
 * renames the server. Bare tags are accepted too, for labels printed before
 * the URL scheme and for hand-typed entry off a scratched sticker.
 */
export function extractAssetTag(scanned: string): string {
  const text = (scanned ?? '').trim();
  if (text.length === 0) return '';

  if (!/^https?:\/\//i.test(text)) return text;

  try {
    const url = new URL(text);
    const segments = url.pathname.split('/').filter(Boolean);

    if (segments.length >= 2 && segments[segments.length - 2].toLowerCase() === 'e') {
      return decodeURIComponent(segments[segments.length - 1]);
    }
    return text;
  } catch {
    return text;
  }
}

async function refresh(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;

  const token = await storage.getRefreshToken();
  if (!token || !serverUrl) return false;

  refreshInFlight = (async () => {
    try {
      const res = await fetch(`${serverUrl}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: token }),
      });

      if (!res.ok) {
        await clearSession();
        return false;
      }

      await storeTokens((await res.json()) as Tokens);
      return true;
    } catch {
      // A dropped Wi-Fi signal is not an expired session. Leaving the stored
      // token alone means walking back into range restores the session
      // instead of forcing a login in a corridor.
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  if (!serverUrl) throw new ApiError(0, 'No server configured.');

  const headers = new Headers(init.headers);
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
  if (init.body) headers.set('Content-Type', 'application/json');

  let res: Response;
  try {
    res = await fetch(`${serverUrl}${path}`, { ...init, headers });
  } catch {
    // fetch rejects for DNS failure, refused connection and cleartext
    // blocking alike. On a hospital LAN the cause is almost always the
    // device being off Wi-Fi or on the guest network.
    throw new ApiError(0, 'Cannot reach the server. Check Wi-Fi and the server address.');
  }

  if (res.status === 401 && retry && (await refresh())) {
    return request<T>(path, init, false);
  }

  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      const body = (await res.json()) as { error?: string };
      if (typeof body?.error === 'string') message = body.error;
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(res.status, message);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  login: async (userName: string, password: string) => {
    const tokens = await request<Tokens>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ userName, password }),
    });
    await storeTokens(tokens);
    return tokens;
  },

  me: () =>
    request<{ userName: string | null; fullName: string | null; roles: string[] }>('/api/auth/me'),

  byTag: (assetTag: string) =>
    request<Equipment>(`/api/equipment/by-tag/${encodeURIComponent(assetTag)}`),

  /** Open PM work for one machine. */
  tasksForEquipment: (equipmentId: number) =>
    request<{ items: PmTask[]; total: number }>(
      `/api/pm/tasks?equipmentId=${equipmentId}&pageSize=50`),

  taskForm: (taskId: number) => request<PmForm>(`/api/pm/tasks/${taskId}/form`),

  completeTask: (taskId: number, body: unknown) =>
    request<{ completionId: number; outOfRangeCount: number; replayed: boolean }>(
      `/api/pm/tasks/${taskId}/complete`,
      { method: 'POST', body: JSON.stringify(body) }),

  /** Cheap reachability probe that does not need a session. */
  health: async (url: string) => {
    const res = await fetch(`${url}/health`, { method: 'GET' });
    if (!res.ok) throw new ApiError(res.status, `Server responded ${res.status}`);
    return (await res.json()) as { status: string; version: string };
  },

  restoreSession: async () => {
    if (accessToken) return true;
    return refresh();
  },
};
