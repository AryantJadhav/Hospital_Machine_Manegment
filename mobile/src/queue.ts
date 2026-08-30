import AsyncStorage from '@react-native-async-storage/async-storage';
import { api, ApiError } from './api';

/**
 * Offline write queue.
 *
 * Deliberately one-way and deliberately small. The build plan scopes offline
 * to "writes queue locally and replay in order if signal drops mid-task" and
 * explicitly rules out bidirectional sync with conflict resolution, which is
 * the single most likely thing to consume two months and still be subtly
 * broken.
 *
 * So: opening a PM needs signal. Submitting one does not. Nothing is merged,
 * nothing is reconciled, and the server is always right.
 */

const QUEUE_KEY = 'hospitalpm.pendingSubmissions';

export type PendingSubmission = {
  /** Client-generated, and the reason a replay cannot double-record a PM. */
  clientSubmissionId: string;
  taskId: number;
  assetTag: string;
  body: unknown;
  queuedAtUtc: string;
  /** Last failure, kept so the user can be told why something is stuck. */
  lastError?: string;
};

async function read(): Promise<PendingSubmission[]> {
  try {
    const raw = await AsyncStorage.getItem(QUEUE_KEY);
    return raw ? (JSON.parse(raw) as PendingSubmission[]) : [];
  } catch {
    // A corrupt queue must not brick the app on launch. Losing the queue is
    // bad; being unable to open the app at all is worse.
    return [];
  }
}

async function write(items: PendingSubmission[]): Promise<void> {
  try {
    await AsyncStorage.setItem(QUEUE_KEY, JSON.stringify(items));
  } catch {
    /* storage full or unavailable */
  }
}

export const queue = {
  all: read,

  count: async () => (await read()).length,

  add: async (item: PendingSubmission) => {
    const items = await read();
    items.push(item);
    await write(items);
  },

  remove: async (clientSubmissionId: string) => {
    const items = await read();
    await write(items.filter((i) => i.clientSubmissionId !== clientSubmissionId));
  },

  /**
   * Replays pending submissions oldest first, stopping at the first network
   * failure.
   *
   * Order matters and the stop matters: if the network is down, every
   * subsequent attempt will fail too, and hammering them only drains a
   * technician's battery mid-round.
   */
  flush: async (): Promise<{ sent: number; failed: number; remaining: number }> => {
    const items = await read();
    let sent = 0;
    let failed = 0;

    for (const item of items) {
      try {
        await api.completeTask(item.taskId, item.body);
        await queue.remove(item.clientSubmissionId);
        sent++;
      } catch (e) {
        if (e instanceof ApiError && e.status === 0) {
          // Still offline. Leave the rest queued.
          break;
        }

        if (e instanceof ApiError && e.status === 409) {
          // The task was closed by someone else. The submission can never
          // succeed, so keeping it would block everything behind it forever.
          await queue.remove(item.clientSubmissionId);
          failed++;
          continue;
        }

        // A validation error means this submission is permanently bad. Record
        // why and move on rather than blocking the queue.
        const items2 = await read();
        const match = items2.find((i) => i.clientSubmissionId === item.clientSubmissionId);
        if (match) {
          match.lastError = e instanceof Error ? e.message : 'Submission rejected.';
          await write(items2);
        }
        failed++;
      }
    }

    return { sent, failed, remaining: (await read()).length };
  },
};
