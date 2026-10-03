import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { PERMISSIONS } from '../auth/context';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { StatusPill } from '../StatusPill';
import { PRIORITY_LABEL, PRIORITY_LOOK, WORK_ORDER_LABEL, WORK_ORDER_LOOK } from '../statusTones';
import { formatBytes } from '../bytes';
import { formatHours } from '../hours';
import { formatRupees } from '../money';
import { formatDateTime } from '../time';
import { usePageTitle } from '../pageTitle';
import type { WorkOrderDetail, WorkOrderPhoto } from '../workOrderTypes';

type SparePartOption = { id: number; partNumber: string; name: string; quantityOnHand: number; unit: string };

const MAX_PHOTOS = 10;
const MAX_PHOTO_BYTES = 10 * 1024 * 1024;
const PHOTO_ACCEPT = 'image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp';

/** Runs an action on the work order and says whether it worked. */
type Act = (fn: () => Promise<unknown>) => Promise<boolean>;

const RESOLVED = 50;
const CLOSED = 60;
const CANCELLED = 70;

/** What a person is asked before one of the two changes that cannot be taken back. */
const CONFIRM: Record<number, string> = {
  [CLOSED]: 'Close this service request? A closed service request cannot be changed afterwards.',
  [CANCELLED]: 'Cancel this service request? A cancelled service request cannot be reopened.',
};

/**
 * One work order, on its own address: /work-orders/:id.
 *
 * It used to open in a narrow panel beside the list. Everything an engineer does to a
 * ticket - say what was wrong and what was done, draw a part, add a photo, change its
 * state - was squeezed into a third of the screen, and none of it could be linked to,
 * refreshed or reached from a machine's own page.
 */
export function WorkOrderPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const { may } = useAuth();
  const canAssign = may(PERMISSIONS.workOrdersAssign);
  const location = useLocation();
  const navigate = useNavigate();
  // Where they came from, filters and all, so Back is the list as they left it.
  // Kept in state: reading the handoff clears the navigation state, and Back must still know.
  const [from] = useState(() => (location.state as { from?: string } | null)?.from ?? '/work-orders');
  const [handoff] = useHandoff();

  const [order, setOrder] = useState<WorkOrderDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setOrder(await api.get<WorkOrderDetail>(`/api/work-orders/${id}`));
      setMissing(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setMissing(true);
      else setError(e instanceof Error ? e.message : 'Could not open that service request.');
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    if (!valid) return;
    setLoading(true);
    setOrder(null);
    void load();
  }, [valid, load]);

  usePageTitle(order ? order.number : undefined);

  // Runs one action, then shows the ticket as it now is. Errors land in the banner at the top.
  // Says whether it worked, for the one action that leaves the page afterwards.
  const act = useCallback<Act>(
    async (fn) => {
      setError(null);
      try {
        await fn();
        await load();
        return true;
      } catch (e) {
        setError(e instanceof Error ? e.message : 'That action failed.');
        return false;
      }
    },
    [load],
  );

  // A fixed fault is no longer open work, so it has left the list this page was opened from.
  // Going back there is how that is seen, with a line saying where it went.
  const resolved = useCallback(
    (number: string) =>
      navigate(from, {
        replace: true,
        state: {
          handoff: {
            notice: `${number} is resolved and has left the open list. It is under the Resolved filter.`,
            recorded: null,
          },
        },
      }),
    [navigate, from],
  );

  if (!valid) return <Navigate to="/work-orders" replace />;

  const backLabel = from.startsWith('/equipment') ? 'Back to the machine' : 'Back to Request Service';

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  if (missing || !order) {
    return (
      <div className="page stack">
        {error && <p className="alert alert-error" role="alert">{error}</p>}
        {missing && <p className="alert alert-error" role="alert">That service request does not exist.</p>}
        <div><Link className="btn" to={from}>{backLabel}</Link></div>
      </div>
    );
  }

  const isTerminal = order.allowedTransitions.length === 0;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={from}>← {backLabel}</Link></p>
          <h1 className="mono">{order.number}</h1>
          <p className="muted">
            <Link to={`/equipment/${order.equipmentId}`} state={{ from: `/work-orders/${order.id}` }}>{order.assetTag}</Link>
            {' · '}{order.equipmentTypeName} · {order.locationName}
          </p>
        </div>

        {/* A preview in a tab of its own first; downloading is a choice made there. */}
        <a className="btn" href={`/work-orders/${order.id}/report`} target="_blank" rel="noopener">
          View service report
        </a>
      </header>

      <div className="row">
        <StatusPill look={WORK_ORDER_LOOK[order.status]}>{WORK_ORDER_LABEL[order.status]}</StatusPill>
        <StatusPill look={PRIORITY_LOOK[order.priority]}>{PRIORITY_LABEL[order.priority]}</StatusPill>
        {order.downtimeHours !== null && (
          <StatusPill tone={order.stillDown ? 'danger' : 'neutral'}>
            {order.stillDown ? `Down for ${formatHours(order.downtimeHours)} so far` : `Was down ${formatHours(order.downtimeHours)}`}
          </StatusPill>
        )}
      </div>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="wo-page">
        <div className="stack">
          <section className="card stack">
            <h2 className="section-h">Fault</h2>
            <p style={{ margin: 0 }}>{order.faultDescription}</p>
            <p className="muted" style={{ margin: 0 }}>
              Reported {formatDateTime(order.reportedAtUtc)}
              {order.reportedByName && ` by ${order.reportedByName}`}
            </p>
          </section>

          <Solution order={order} />
          <Parts order={order} isTerminal={isTerminal} act={act} />
          <Photos order={order} isTerminal={isTerminal} canRemove={canAssign} act={act} />
          <History order={order} act={act} />
          {/* Last, because it is the last thing done: the job is finished, then it is resolved. */}
          <ResolveForm order={order} act={act} onResolved={() => resolved(order.number)} />
        </div>

        <div className="stack">
          <Actions order={order} canAssign={canAssign} isTerminal={isTerminal} act={act} />
          <Facts order={order} />
        </div>
      </div>
    </div>
  );
}

/** What was done, once it has been said. Writing it, and resolving, is the last thing on the page. */
function Solution({ order }: { order: WorkOrderDetail }) {
  const canResolve = order.allowedTransitions.includes(RESOLVED);

  return (
    <section className="card stack">
      <h2 className="section-h">Solution</h2>

      {order.resolutionNotes && (
        <>
          <p style={{ margin: 0 }}>{order.resolutionNotes}</p>
          {order.resolvedAtUtc && (
            <p className="muted" style={{ margin: 0 }}>Resolved {formatDateTime(order.resolvedAtUtc)}</p>
          )}
        </>
      )}

      {!order.resolutionNotes && (
        <p className="muted" style={{ margin: 0 }}>
          {canResolve
            ? 'Not resolved yet. When the work is done, say what was wrong and what was done at the bottom of this page.'
            : order.allowedTransitions.length === 0
              ? 'No solution was recorded.'
              : 'Not resolved yet. Once the work is In progress, you can say what was done and resolve it at the bottom of this page.'}
        </p>
      )}
    </section>
  );
}

/**
 * The last step of the job, so it is the last thing on the page: parts drawn and photos taken
 * first, then what was wrong and what was done. It goes on the service report, so it is not
 * optional to resolve.
 */
function ResolveForm({ order, act, onResolved }: { order: WorkOrderDetail; act: Act; onResolved: () => void }) {
  // A work order that was resolved and then reopened keeps its earlier words to start from.
  const [resolution, setResolution] = useState(order.resolutionNotes ?? '');
  const [problem, setProblem] = useState<string | null>(null);
  const box = useRef<HTMLTextAreaElement>(null);

  if (!order.allowedTransitions.includes(RESOLVED)) return null;

  // The button stays clickable when the box is empty and says what is missing. A greyed-out
  // button explained only by a placeholder reads as broken.
  function resolve() {
    if (!resolution.trim()) {
      setProblem('Say what was wrong and what you did before resolving. It goes on the service report.');
      box.current?.focus();
      return;
    }
    void act(() => api.post(`/api/work-orders/${order.id}/resolve`, { resolutionNotes: resolution })).then((ok) => {
      if (ok) onResolved();
    });
  }

  return (
    <section className="card stack">
      <h2 className="section-h">Resolve</h2>
      <label className="field">
        <span>What was wrong and what you did</span>
        <textarea
          ref={box}
          rows={4}
          aria-invalid={problem !== null}
          placeholder="Required to resolve. It goes on the service report."
          value={resolution}
          onChange={(e) => {
            setResolution(e.target.value);
            if (e.target.value.trim()) setProblem(null);
          }}
        />
      </label>
      {problem && <p className="alert alert-error" role="alert">{problem}</p>}
      <div>
        <button className="btn btn-primary" onClick={resolve}>Resolve and return to service</button>
      </div>
    </section>
  );
}

function Parts({
  order,
  isTerminal,
  act,
}: {
  order: WorkOrderDetail;
  isTerminal: boolean;
  act: Act;
}) {
  const [spareParts, setSpareParts] = useState<SparePartOption[]>([]);
  const [partId, setPartId] = useState('');
  const [partQty, setPartQty] = useState('1');
  const [partError, setPartError] = useState<string | null>(null);

  // The shelf to pick from, loaded once a ticket is open rather than with every row in the list.
  useEffect(() => {
    if (isTerminal) return;
    void (async () => {
      try {
        const data = await api.get<{ items: SparePartOption[] }>('/api/spare-parts?pageSize=200');
        setSpareParts(data.items);
      } catch {
        // The rest of the ticket still works without the picker.
      }
    })();
  }, [isTerminal]);

  // Local: what is missing before the request is even sent. A server-side refusal (not enough
  // stock, a retired part) surfaces through the page's own error banner.
  function recordPart() {
    const spareId = Number(partId);
    const qty = Number(partQty);
    if (!spareId || !Number.isInteger(qty) || qty < 1) {
      setPartError('Choose a part and a quantity of at least 1.');
      return;
    }
    setPartError(null);
    void act(() => api.post(`/api/work-orders/${order.id}/parts`, { sparePartId: spareId, quantityUsed: qty }));
    setPartId('');
    setPartQty('1');
  }

  const priced = order.partsUsed.filter((p) => p.unitCostAtUse !== null);
  const total = priced.reduce((sum, p) => sum + (p.unitCostAtUse ?? 0) * p.quantityUsed, 0);

  return (
    <section className="card stack">
      <h2 className="section-h">Parts used</h2>

      {order.partsUsed.length === 0 && (
        <p className="muted" style={{ margin: 0 }}>
          {isTerminal ? 'No spare parts were used.' : 'No spare parts used yet.'}
        </p>
      )}

      {order.partsUsed.length > 0 && (
        <ul className="timeline">
          {order.partsUsed.map((p) => (
            <li key={p.id} className="row" style={{ justifyContent: 'space-between' }}>
              <span>
                <span className="mono">{p.quantityUsed}× {p.partNumber}</span> — {p.name}
                {p.unitCostAtUse !== null && (
                  <span className="muted"> ({formatRupees(p.unitCostAtUse * p.quantityUsed)})</span>
                )}
              </span>
              {!isTerminal && (
                <button
                  className="btn btn-quiet"
                  aria-label={`Undo ${p.partNumber}`}
                  onClick={() => void act(() => api.del(`/api/work-orders/${order.id}/parts/${p.id}`))}
                >
                  Undo
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {priced.length > 0 && (
        <p className="muted" style={{ margin: 0 }}>
          Parts cost {formatRupees(total)}
          {priced.length < order.partsUsed.length && ' (some parts have no cost recorded)'}
        </p>
      )}

      {!isTerminal && (
        <div className="row">
          <select
            aria-label="Part to record"
            className="field grow"
            value={partId}
            onChange={(e) => setPartId(e.target.value)}
          >
            <option value="">Choose a part…</option>
            {spareParts.map((p) => (
              <option key={p.id} value={p.id}>
                {p.partNumber} — {p.name} ({p.quantityOnHand} {p.unit} left)
              </option>
            ))}
          </select>
          <input
            aria-label="Quantity used"
            type="number"
            min={1}
            className="field"
            style={{ maxWidth: '5rem' }}
            value={partQty}
            onChange={(e) => setPartQty(e.target.value)}
          />
          <button className="btn" onClick={recordPart}>Record</button>
        </div>
      )}
      {partError && <p className="alert alert-error" role="alert">{partError}</p>}
    </section>
  );
}

function Photos({
  order,
  isTerminal,
  canRemove,
  act,
}: {
  order: WorkOrderDetail;
  isTerminal: boolean;
  canRemove: boolean;
  act: Act;
}) {
  const [photoFiles, setPhotoFiles] = useState<File[]>([]);
  const [photoError, setPhotoError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const photoInput = useRef<HTMLInputElement>(null);

  function addPhotoFiles(list: FileList | null) {
    if (!list || list.length === 0) return;
    const room = MAX_PHOTOS - order.photos.length - photoFiles.length;
    const next = [...photoFiles];

    for (const f of Array.from(list)) {
      if (f.size === 0) {
        setPhotoError(`"${f.name}" is empty.`);
        continue;
      }
      if (f.size > MAX_PHOTO_BYTES) {
        setPhotoError(`"${f.name}" is larger than 10 MB.`);
        continue;
      }
      if (next.length - photoFiles.length >= room) {
        setPhotoError(`A service request can have at most ${MAX_PHOTOS} photos.`);
        break;
      }
      setPhotoError(null);
      next.push(f);
    }

    setPhotoFiles(next);
    if (photoInput.current) photoInput.current.value = '';
  }

  async function uploadPhotos() {
    if (photoFiles.length === 0) return;
    setUploading(true);
    setPhotoError(null);
    await act(async () => {
      const form = new FormData();
      photoFiles.forEach((f) => form.append('files', f, f.name));
      await api.postForm(`/api/work-orders/${order.id}/photos`, form);
      // Only cleared once the upload actually succeeded - a refusal (too many photos, wrong
      // type) leaves the picked files so nothing is silently lost.
      setPhotoFiles([]);
    });
    setUploading(false);
  }

  function removePhoto(photo: WorkOrderPhoto) {
    if (!window.confirm(`Remove "${photo.fileName}"? It is deleted from the system. The removal is recorded in the audit log.`)) {
      return;
    }
    void act(() => api.del(`/api/work-orders/photos/${photo.id}`));
  }

  return (
    <section className="card stack">
      <h2 className="section-h">Photos</h2>

      {order.photos.length === 0 && (
        <p className="muted" style={{ margin: 0 }}>{isTerminal ? 'No photos were attached.' : 'No photos yet.'}</p>
      )}

      {order.photos.length > 0 && (
        <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.35rem' }}>
          {order.photos.map((p) => (
            <li key={p.id} className="row" style={{ justifyContent: 'space-between' }}>
              <span>
                {p.fileName}
                <span className="muted"> · {formatBytes(p.sizeBytes)} · {p.uploadedBy ?? 'unknown'}</span>
              </span>
              <span className="row" style={{ gap: '0.4rem' }}>
                <button
                  className="btn btn-quiet"
                  aria-label={`View ${p.fileName}`}
                  onClick={() =>
                    void api.view(`/api/work-orders/photos/${p.id}`)
                      .catch((err: unknown) => setPhotoError(err instanceof Error ? err.message : 'Could not open the photo.'))
                  }
                >
                  View
                </button>
                {canRemove && (
                  <button className="btn btn-quiet" aria-label={`Remove ${p.fileName}`} onClick={() => removePhoto(p)}>
                    Remove
                  </button>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}

      {!isTerminal && order.photos.length + photoFiles.length < MAX_PHOTOS && (
        <div className="stack">
          <input
            ref={photoInput}
            type="file"
            className="field"
            style={{ minWidth: 0, width: '100%' }}
            multiple
            accept={PHOTO_ACCEPT}
            aria-label="Choose photos"
            onChange={(e) => addPhotoFiles(e.target.files)}
          />
          {photoFiles.length > 0 && (
            <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.25rem' }}>
              {photoFiles.map((f, i) => (
                <li key={`${f.name}-${i}`} className="row" style={{ justifyContent: 'space-between' }}>
                  <span>{f.name} <span className="muted">· {formatBytes(f.size)}</span></span>
                  <button
                    type="button"
                    className="btn btn-quiet"
                    aria-label={`Remove ${f.name}`}
                    onClick={() => setPhotoFiles(photoFiles.filter((_, j) => j !== i))}
                  >
                    Remove
                  </button>
                </li>
              ))}
            </ul>
          )}
          <p className="muted" style={{ margin: 0 }}>
            JPEG, PNG or WebP, up to 10 MB each. This system holds no patient information, so do
            not upload anything that shows a patient or a patient&apos;s details.
          </p>
          {photoFiles.length > 0 && (
            <div>
              <button className="btn" disabled={uploading} onClick={() => void uploadPhotos()}>
                {uploading ? 'Saving…' : 'Save photos'}
              </button>
            </div>
          )}
        </div>
      )}
      {photoError && <p className="alert alert-error" role="alert">{photoError}</p>}
    </section>
  );
}

function History({ order, act }: { order: WorkOrderDetail; act: Act }) {
  const [note, setNote] = useState('');

  return (
    <section className="card stack">
      <h2 className="section-h">History</h2>

      {order.notes.length === 0 && <p className="muted" style={{ margin: 0 }}>Nothing has been noted yet.</p>}

      {order.notes.length > 0 && (
        <ul className="timeline">
          {order.notes.map((n) => (
            <li key={n.id}>
              <span className="muted">
                {formatDateTime(n.createdAtUtc)}
                {n.authorName && ` · ${n.authorName}`}
              </span>
              {n.statusAfter !== null && (
                <StatusPill look={WORK_ORDER_LOOK[n.statusAfter]} className="hist-flag">
                  {WORK_ORDER_LABEL[n.statusAfter]}
                </StatusPill>
              )}
              <div>{n.body}</div>
            </li>
          ))}
        </ul>
      )}

      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault();
          if (!note.trim()) return;
          void act(async () => {
            await api.post(`/api/work-orders/${order.id}/notes`, { body: note });
            setNote('');
          });
        }}
      >
        <input
          className="grow"
          placeholder="Add a note"
          aria-label="Add a note"
          value={note}
          onChange={(e) => setNote(e.target.value)}
        />
        <button className="btn" type="submit" disabled={!note.trim()}>Add</button>
      </form>
    </section>
  );
}

/** Moving the ticket along, and who it is with. Only what the server will accept is offered. */
function Actions({
  order,
  canAssign,
  isTerminal,
  act,
}: {
  order: WorkOrderDetail;
  canAssign: boolean;
  isTerminal: boolean;
  act: Act;
}) {
  const [staff, setStaff] = useState<{ id: number; fullName: string }[]>([]);
  const [note, setNote] = useState('');

  // Only for someone who may assign: the staff list is an Admin route, so asking as an
  // Employee is a guaranteed 403.
  useEffect(() => {
    if (!canAssign) return;
    void (async () => {
      try {
        setStaff(await api.get<{ id: number; fullName: string }[]>('/api/people'));
      } catch {
        // Assignment degrades to unavailable rather than breaking the page.
      }
    })();
  }, [canAssign]);

  // Resolving has its own place under Solution, because it needs the words that go on the report.
  const transitions = order.allowedTransitions.filter((s) => s !== RESOLVED);

  function move(status: number) {
    const question = CONFIRM[status];
    if (question && !window.confirm(question)) return;
    void act(async () => {
      await api.post(`/api/work-orders/${order.id}/status`, { status, note: note.trim() || undefined });
      setNote('');
    });
  }

  function assign(value: string) {
    if (!value) return;
    const userId = value === 'nobody' ? null : Number(value);
    void act(() => api.post(`/api/work-orders/${order.id}/assign`, { assignedToUserId: userId }));
  }

  return (
    <section className="card stack">
      <h2 className="section-h">Actions</h2>

      {isTerminal && (
        <p className="muted" style={{ margin: 0 }}>
          This service request is {WORK_ORDER_LABEL[order.status].toLowerCase()}, so it can no longer be changed.
        </p>
      )}

      {transitions.length > 0 && (
        <>
          <label className="field">
            <span>Note with the change (optional)</span>
            <input value={note} onChange={(e) => setNote(e.target.value)} />
          </label>
          <div className="row">
            {transitions.map((s) => (
              <button key={s} className={s === CANCELLED ? 'btn btn-quiet' : 'btn'} onClick={() => move(s)}>
                {WORK_ORDER_LABEL[s]}
              </button>
            ))}
          </div>
        </>
      )}

      {canAssign && !isTerminal && staff.length > 0 && (
        <label className="field">
          <span>{order.assignedToUserId === null ? 'Assign to' : 'Reassign to'}</span>
          <select aria-label="Assign to" value="" onChange={(e) => assign(e.target.value)}>
            <option value="">Choose a person…</option>
            {staff
              .filter((p) => p.id !== order.assignedToUserId)
              .map((p) => (
                <option key={p.id} value={p.id}>{p.fullName}</option>
              ))}
            {order.assignedToUserId !== null && <option value="nobody">Nobody — back to the queue</option>}
          </select>
        </label>
      )}
    </section>
  );
}

function Facts({ order }: { order: WorkOrderDetail }) {
  return (
    <section className="card stack">
      <h2 className="section-h">Details</h2>
      <dl className="detail">
        <Fact label="Location" value={order.locationName} />
        <Fact label="Assigned to" value={order.assignedToName} />
        <Fact label="Reported" value={formatDateTime(order.reportedAtUtc)} />
        <Fact label="Assigned" value={order.assignedAtUtc ? formatDateTime(order.assignedAtUtc) : null} />
        <Fact label="Started" value={order.startedAtUtc ? formatDateTime(order.startedAtUtc) : null} />
        <Fact label="Resolved" value={order.resolvedAtUtc ? formatDateTime(order.resolvedAtUtc) : null} />
        <Fact label="Closed" value={order.closedAtUtc ? formatDateTime(order.closedAtUtc) : null} />
        <Fact label="Out of service" value={order.outOfServiceAtUtc ? formatDateTime(order.outOfServiceAtUtc) : null} />
        <Fact label="Back in service" value={order.backInServiceAtUtc ? formatDateTime(order.backInServiceAtUtc) : null} />
      </dl>
    </section>
  );
}

function Fact({ label, value }: { label: string; value: string | null }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{value || <span className="muted">—</span>}</dd>
    </>
  );
}
