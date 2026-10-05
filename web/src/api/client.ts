/**
 * API client with automatic access-token refresh.
 *
 * The access token is held in memory only. The refresh token goes to
 * localStorage because a technician's tablet is closed and reopened all
 * shift, and forcing a fresh login each time is how staff stop using
 * software. Keeping the short-lived token out of storage limits what an XSS
 * bug could walk away with to a 15-minute window.
 */

const REFRESH_KEY = 'hospitalpm.refresh';

let accessToken: string | null = null;
let refreshInFlight: Promise<boolean> | null = null;

export type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
};

export function getRefreshToken(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY);
  } catch {
    // Private windows and locked-down browsers throw on access.
    return null;
  }
}

function setRefreshToken(token: string | null) {
  try {
    if (token) localStorage.setItem(REFRESH_KEY, token);
    else localStorage.removeItem(REFRESH_KEY);
  } catch {
    /* storage unavailable; session stays in-memory only */
  }
}

export function storeTokens(tokens: TokenResponse) {
  accessToken = tokens.accessToken;
  setRefreshToken(tokens.refreshToken);
}

export function clearTokens() {
  accessToken = null;
  setRefreshToken(null);
}

export function hasSession(): boolean {
  return accessToken !== null || getRefreshToken() !== null;
}

export class ApiError extends Error {
  // Declared explicitly rather than as constructor parameter properties:
  // the project builds with erasableSyntaxOnly, which forbids syntax that
  // cannot be stripped without emitting code.
  readonly status: number;
  readonly body?: unknown;

  constructor(status: number, message: string, body?: unknown) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

/**
 * Exchanges the refresh token for a new pair.
 *
 * Deduplicated: several requests can 401 at once when a token expires, and
 * without this they would each try to refresh. Since the server rotates
 * refresh tokens and treats reuse as a leak, concurrent refreshes would
 * revoke the whole chain and log the user out.
 */
async function refresh(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;

  const token = getRefreshToken();
  if (!token) return false;

  refreshInFlight = (async () => {
    try {
      const res = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: token }),
      });

      if (!res.ok) {
        clearTokens();
        return false;
      }

      storeTokens((await res.json()) as TokenResponse);
      return true;
    } catch {
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

/** Sent on the window when any request is answered "locked", so the lock screen can replace the page. */
export const LOCKED_EVENT = 'hospitalpm:locked';

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const headers = new Headers(init.headers);
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
  if (init.body && !(init.body instanceof FormData) && !(init.body instanceof Blob)) {
    headers.set('Content-Type', 'application/json');
  }

  const res = await fetch(path, { ...init, headers });

  if (res.status === 401 && retry && (await refresh())) {
    return request<T>(path, init, false);
  }

  // The installation has been locked by its supplier. Whatever the page was doing, the lock screen takes over.
  if (res.status === 423) {
    window.dispatchEvent(new Event(LOCKED_EVENT));
  }

  if (!res.ok) {
    let body: unknown;
    let message = `Request failed (${res.status})`;
    try {
      body = await res.json();
      const asRecord = body as Record<string, unknown>;
      if (typeof asRecord?.error === 'string') message = asRecord.error;
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(res.status, message, body);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined }),
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  del: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  /** Sends a form with files. The browser adds its own multipart boundary. */
  postForm: <T>(path: string, form: FormData) => request<T>(path, { method: 'POST', body: form }),
  /**
   * Opens a saved file in a new tab. The file is fetched with the sign-in and shown from
   * a temporary local address, because a plain link to it would arrive without the sign-in.
   * The tab is opened first, in the click, or the browser takes a tab opened later for a pop-up.
   */
  view: async (path: string): Promise<void> => {
    const tab = window.open('', '_blank');
    try {
      const send = () =>
        fetch(path, { headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {} });
      let res = await send();
      if (res.status === 401 && (await refresh())) res = await send();
      if (!res.ok) throw new ApiError(res.status, `Could not open the file (${res.status})`);

      const url = URL.createObjectURL(await res.blob());
      if (tab) tab.location.href = url;
      else window.location.href = url;
      // Left for a minute so the tab can load it, then let go of it.
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (err) {
      tab?.close();
      throw err;
    }
  },
  /**
   * Fetches a file with the sign-in and hands back its bytes, for a page that shows it itself:
   * a preview shown in the page, before anyone decides to keep a copy.
   */
  blob: async (path: string): Promise<Blob> => {
    const send = () =>
      fetch(path, { headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {} });
    let res = await send();
    if (res.status === 401 && (await refresh())) res = await send();

    if (!res.ok) {
      let message = `Could not open the file (${res.status})`;
      try {
        const body = (await res.json()) as Record<string, unknown>;
        if (typeof body?.error === 'string') message = body.error;
      } catch {
        /* non-JSON error body */
      }
      throw new ApiError(res.status, message);
    }

    return res.blob();
  },
  /**
   * Sends a file as the whole request, streamed from disk and never held in the page's memory, with any extra
   * headers. For files too large for a form: a backup is as big as the database and its photos.
   */
  uploadFile: <T>(path: string, file: File, headers: Record<string, string> = {}) =>
    request<T>(path, { method: 'POST', body: file, headers }),
  upload: <T>(path: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<T>(path, { method: 'POST', body: form });
  },
  /** Downloads a binary response, bypassing JSON parsing. */
  download: (path: string, filename: string) => downloadCore(path, filename),

  /** Uploads a file and downloads whatever comes back — the annotated import report. */
  uploadDownload: (path: string, file: File, filename: string) => {
    const form = new FormData();
    form.append('file', file);
    return downloadCore(path, filename, { method: 'POST', form });
  },

  /** Downloads the result of a POST — label sheets take a body of ids. */
  downloadPost: (path: string, body: unknown, filename: string) =>
    downloadCore(path, filename, {
      method: 'POST',
      body: JSON.stringify(body),
      contentType: 'application/json',
    }),
};

async function downloadCore(
  path: string,
  filename: string,
  init?: { method?: string; body?: string; contentType?: string; form?: FormData },
): Promise<void> {
  const send = () => {
    const headers = new Headers();
    if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
    // Deliberately not set for FormData: the browser has to add its own
    // multipart boundary, and setting it by hand breaks the upload.
    if (init?.contentType && !init.form) headers.set('Content-Type', init.contentType);
    return fetch(path, {
      method: init?.method ?? 'GET',
      body: init?.form ?? init?.body,
      headers,
    });
  };

  let res = await send();
  if (res.status === 401 && (await refresh())) res = await send();

  if (!res.ok) {
    let message = `Download failed (${res.status})`;
    try {
      const body = (await res.json()) as Record<string, unknown>;
      if (typeof body?.error === 'string') message = body.error;
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(res.status, message);
  }

  // 204 means there was nothing to hand back — a clean file has no report.
  if (res.status === 204) return;

  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  // Revoked on the next tick: revoking synchronously can cancel the download
  // in some browsers before it has started reading the blob.
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export async function restoreSession(): Promise<boolean> {
  if (accessToken) return true;
  return refresh();
}
