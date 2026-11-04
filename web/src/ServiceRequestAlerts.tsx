import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { openStream } from './api/client';
import { PRIORITY_LOOK } from './statusTones';
import { StatusPill } from './StatusPill';
import { formatDateTime } from './time';

/** A new service request, as the server announces it. */
type Alert = {
  id: number;
  number: string;
  equipmentId: number;
  assetTag: string | null;
  machine: string | null;
  location: string | null;
  priority: 'Low' | 'Medium' | 'High' | 'Critical';
  fault: string;
  reportedBy: string;
  reportedAtUtc: string;
};

const PRIORITY_NUMBER: Record<Alert['priority'], number> = { Low: 10, Medium: 20, High: 30, Critical: 40 };

/** Most pop-ups on the screen at once; the oldest go first, and every request is still in the list. */
const MAX_SHOWN = 5;

/** A routine request fades after this long. An urgent one stays until someone has seen it. */
const FADE_AFTER_MS = 30_000;

const MUTE_KEY = 'hospitalpm.alert-sound-muted';

function readMuted(): boolean {
  try {
    return localStorage.getItem(MUTE_KEY) === '1';
  } catch {
    return false;
  }
}

function writeMuted(muted: boolean) {
  try {
    if (muted) localStorage.setItem(MUTE_KEY, '1');
    else localStorage.removeItem(MUTE_KEY);
  } catch {
    /* a private window: the choice lasts until the page is closed */
  }
}

/** Two soft notes. Browsers only allow sound after the person has used the page, which they have by signing in. */
function chime() {
  try {
    const AudioCtx = window.AudioContext;
    const ctx = new AudioCtx();
    const play = (frequency: number, start: number) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = frequency;
      gain.gain.setValueAtTime(0.0001, ctx.currentTime + start);
      gain.gain.exponentialRampToValueAtTime(0.15, ctx.currentTime + start + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + start + 0.28);
      osc.connect(gain).connect(ctx.destination);
      osc.start(ctx.currentTime + start);
      osc.stop(ctx.currentTime + start + 0.3);
    };
    play(880, 0);
    play(1175, 0.16);
    window.setTimeout(() => void ctx.close(), 800);
  } catch {
    /* no sound is no loss: the pop-up is the message */
  }
}

/** Reads server-sent events off a streamed response, one parsed event at a time. */
async function* events(body: ReadableStream<Uint8Array>): AsyncGenerator<{ name: string; data: string }> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });

      let cut: number;
      while ((cut = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, cut);
        buffer = buffer.slice(cut + 2);

        let name = 'message';
        const data: string[] = [];
        for (const line of block.split('\n')) {
          // A line that starts with a colon is a comment: the server saying it is still there.
          if (line.startsWith(':')) continue;
          if (line.startsWith('event:')) name = line.slice(6).trim();
          else if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
        }

        if (data.length > 0) yield { name, data: data.join('\n') };
      }
    }
  } finally {
    reader.releaseLock();
  }
}

/**
 * The pop-up that tells a biomedical engineer or the head, as it happens, that a department has placed a service request.
 *
 * It listens to one open connection to the program (see AlertEndpoints) and shows each new request in the corner of whatever page
 * the person is on: which machine, where, how urgent, what was said, and who said it. Urgent ones stay until dismissed and
 * routine ones fade; a soft chime sounds unless it has been muted. Opening one goes to the request.
 *
 * If the connection drops, because the machine was restarted or the network blinked, it opens again by itself. A request placed
 * while someone was not signed in is not lost: it is in the Request Service list, which is the record. This is only the nudge.
 */
export function ServiceRequestAlerts() {
  const navigate = useNavigate();
  const [shown, setShown] = useState<(Alert & { key: number })[]>([]);
  const [muted, setMuted] = useState(readMuted);
  const mutedRef = useRef(muted);
  const nextKey = useRef(1);
  const seen = useRef(new Set<number>());

  useEffect(() => {
    mutedRef.current = muted;
  }, [muted]);

  const dismiss = useCallback((key: number) => setShown((all) => all.filter((a) => a.key !== key)), []);

  useEffect(() => {
    const controller = new AbortController();
    const timers = new Set<number>();

    const arrived = (alert: Alert) => {
      // The same request can be announced twice if the connection is opened again at the wrong moment.
      if (seen.current.has(alert.id)) return;
      seen.current.add(alert.id);

      const key = nextKey.current++;
      setShown((all) => [{ ...alert, key }, ...all].slice(0, MAX_SHOWN));

      if (!mutedRef.current) chime();

      // So a list that is open, and the count beside Request Service, catch up without waiting for the next refresh.
      window.dispatchEvent(new Event('work-orders-changed'));

      if (alert.priority !== 'High' && alert.priority !== 'Critical') {
        const timer = window.setTimeout(() => {
          timers.delete(timer);
          dismiss(key);
        }, FADE_AFTER_MS);
        timers.add(timer);
      }
    };

    const pause = (ms: number) =>
      new Promise<void>((resolve) => {
        const timer = window.setTimeout(resolve, ms);
        controller.signal.addEventListener('abort', () => {
          window.clearTimeout(timer);
          resolve();
        });
      });

    (async () => {
      while (!controller.signal.aborted) {
        try {
          const res = await openStream('/api/notifications/stream', controller.signal);

          // Not allowed to listen, or the program is not there: try again later, quietly.
          if (!res.ok || !res.body) {
            await pause(res.status === 403 ? 5 * 60_000 : 10_000);
            continue;
          }

          for await (const event of events(res.body)) {
            if (event.name !== 'service-request') continue;
            try {
              arrived(JSON.parse(event.data) as Alert);
            } catch {
              /* a line that is not a request is skipped */
            }
          }
        } catch {
          // The connection dropped, or the page is closing.
        }

        // The server ends each stream after a few minutes on purpose, and the browser opens it again.
        await pause(3_000);
      }
    })();

    return () => {
      controller.abort();
      timers.forEach((t) => window.clearTimeout(t));
    };
  }, [dismiss]);

  if (shown.length === 0) return null;

  return (
    <section className="request-alerts" aria-label="New service requests">
      {shown.map((a) => {
        const urgent = a.priority === 'High' || a.priority === 'Critical';
        return (
          <article
            key={a.key}
            className={`request-alert${urgent ? ' request-alert-urgent' : ''}`}
            role={urgent ? 'alert' : 'status'}
          >
            <header className="request-alert-head">
              <strong>New service request</strong>
              <StatusPill look={PRIORITY_LOOK[PRIORITY_NUMBER[a.priority]]}>{a.priority}</StatusPill>
              <button className="toast-close" aria-label={`Dismiss ${a.number}`} onClick={() => dismiss(a.key)}>
                ×
              </button>
            </header>

            <p className="request-alert-machine">
              <span className="mono">{a.number}</span>
              {a.assetTag && <> · <span className="mono">{a.assetTag}</span></>}
              {a.machine && <> · {a.machine}</>}
            </p>
            {a.location && <p className="muted request-alert-line">{a.location}</p>}
            <p className="request-alert-fault">{a.fault}</p>
            <p className="muted request-alert-line">
              Placed by {a.reportedBy} · {formatDateTime(a.reportedAtUtc)}
            </p>

            <div className="row">
              <button
                className="btn btn-primary"
                onClick={() => {
                  dismiss(a.key);
                  navigate(`/work-orders/${a.id}`);
                }}
              >
                Open request
              </button>
              <button className="btn btn-quiet" onClick={() => dismiss(a.key)}>Dismiss</button>
            </div>
          </article>
        );
      })}

      <button
        className="btn btn-quiet request-alerts-sound"
        onClick={() => {
          writeMuted(!muted);
          setMuted(!muted);
        }}
      >
        {muted ? 'Turn the sound on' : 'Mute the sound'}
      </button>
    </section>
  );
}
