import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { EquipmentPicker } from '../EquipmentPicker';
import { StatusPill } from '../StatusPill';
import { PRIORITY_LOOK, WORK_ORDER_LOOK } from '../statusTones';
import { formatHours } from '../hours';

type WorkOrderRow = {
  id: number;
  number: string;
  status: number;
  priority: number;
  faultDescription: string;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  reportedAtUtc: string;
  assignedToUserId: number | null;
  assignedToName: string | null;
  outOfServiceAtUtc: string | null;
  backInServiceAtUtc: string | null;
};

type WorkOrderDetail = WorkOrderRow & {
  reportedByUserId: number;
  assignedAtUtc: string | null;
  startedAtUtc: string | null;
  resolutionNotes: string | null;
  resolvedAtUtc: string | null;
  closedAtUtc: string | null;
  downtimeMinutes: number | null;
  /** Hours down for this report, up to now while the machine still is. */
  downtimeHours: number | null;
  stillDown: boolean;
  allowedTransitions: number[];
  notes: { id: number; body: string; statusAfter: number | null; authorUserId: number; createdAtUtc: string }[];
  partsUsed: {
    id: number;
    sparePartId: number;
    partNumber: string;
    name: string;
    quantityUsed: number;
    unitCostAtUse: number | null;
    usedByUserId: number;
    usedAtUtc: string;
  }[];
  photos: WorkOrderPhoto[];
};

type WorkOrderPhoto = {
  id: number;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedBy: string | null;
  uploadedAtUtc: string;
};

type SparePartOption = { id: number; partNumber: string; name: string; quantityOnHand: number; unit: string };

const MAX_PHOTOS = 10;
const MAX_PHOTO_BYTES = 10 * 1024 * 1024;
const PHOTO_ACCEPT = 'image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp';

function photoSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

const STATUS: Record<number, string> = {
  10: 'Reported',
  20: 'Assigned',
  30: 'In progress',
  40: 'On hold',
  50: 'Resolved',
  60: 'Closed',
  70: 'Cancelled',
};

const PRIORITY: Record<number, string> = { 10: 'Low', 20: 'Medium', 30: 'High', 40: 'Critical' };

const rupees = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });

export function WorkOrdersPage() {
  const { can } = useAuth();
  const canAssign = can(ROLES.admin);

  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const priority = params.get('priority') ?? '';
  // "me" is worked out by the server from who is signed in.
  const mine = params.get('assignee') === 'me';

  const [rows, setRows] = useState<WorkOrderRow[]>([]);
  const [selected, setSelected] = useState<WorkOrderDetail | null>(null);
  const [reporting, setReporting] = useState(false);
  // The machine to report against when we were sent here from its own page, and
  // the page to go back to afterwards. Both are absent when someone simply
  // pressed Report a fault on this list.
  const [reportFor, setReportFor] = useState<{ id: number; label: string } | null>(null);
  const location = useLocation();
  const navigate = useNavigate();
  const from = (location.state as { from?: string } | null)?.from ?? null;
  const reportParam = params.get('report');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const q = new URLSearchParams({ pageSize: '100' });
      if (status) q.set('status', status);
      if (priority) q.set('priority', priority);
      if (mine) q.set('assignee', 'me');
      const data = await api.get<{ items: WorkOrderRow[] }>(`/api/work-orders?${q}`);
      setRows(data.items);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load work orders.');
    } finally {
      setLoading(false);
    }
  }, [status, priority, mine]);

  useEffect(() => {
    void load();
  }, [load]);

  // Arriving from a machine's page with ?report=<id>: open the form with that
  // machine already in it. It used to land on the plain list and leave the
  // technician to press Report a fault and search for the machine they were
  // standing at.
  useEffect(() => {
    if (!reportParam) return;
    let cancelled = false;
    (async () => {
      try {
        const m = await api.get<{ id: number; assetTag: string; equipmentTypeName: string }>(
          `/api/equipment/${reportParam}`,
        );
        if (cancelled) return;
        setReportFor({ id: m.id, label: `${m.assetTag} — ${m.equipmentTypeName}` });
        setReporting(true);
      } catch {
        // Unknown machine: fall back to the plain form rather than a dead end.
        if (!cancelled) setReporting(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [reportParam]);

  function closeReport() {
    setReporting(false);
    setReportFor(null);
    if (reportParam) {
      const next = new URLSearchParams(params);
      next.delete('report');
      setParams(next, { replace: true });
    }
  }

  async function open(id: number) {
    try {
      setSelected(await api.get<WorkOrderDetail>(`/api/work-orders/${id}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not open that work order.');
    }
  }

  async function act(fn: () => Promise<unknown>) {
    setError(null);
    try {
      await fn();
      if (selected) await open(selected.id);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That action failed.');
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>{mine ? 'My work orders' : 'Work orders'}</h1>
          <p className="muted">
            {mine
              ? 'Faults assigned to you that are still to be done.'
              : 'Breakdowns and unscheduled repairs.'}
          </p>
        </div>
        <button className="btn btn-primary" onClick={() => setReporting(true)}>Report a fault</button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {reporting && (
        <ReportForm
          initial={reportFor}
          onCancel={() => (from ? navigate(from) : closeReport())}
          onDone={async () => {
            // Back where they were standing, with the fault said there. Anywhere
            // else, the list, which now shows it.
            if (from) {
              navigate(from, { replace: true, state: { handoff: { notice: 'Fault reported.', recorded: null } } });
              return;
            }
            closeReport();
            await load();
          }}
          onError={setError}
        />
      )}

      <div className="filters card">
        <select
          aria-label="Whose work orders"
          value={mine ? 'me' : ''}
          onChange={(e) => {
            const next = new URLSearchParams(params);
            if (e.target.value) next.set('assignee', e.target.value);
            else next.delete('assignee');
            setParams(next, { replace: true });
          }}
        >
          <option value="">Everyone's</option>
          <option value="me">Assigned to me</option>
        </select>
        <select
          aria-label="Filter by status"
          value={status}
          onChange={(e) => {
            const next = new URLSearchParams(params);
            if (e.target.value) next.set('status', e.target.value);
            else next.delete('status');
            setParams(next, { replace: true });
          }}
        >
          <option value="">{mine ? 'Still to do' : 'Open only'}</option>
          {Object.entries(STATUS).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
        <select
          aria-label="Filter by priority"
          value={priority}
          onChange={(e) => {
            const next = new URLSearchParams(params);
            if (e.target.value) next.set('priority', e.target.value);
            else next.delete('priority');
            setParams(next, { replace: true });
          }}
        >
          <option value="">Any priority</option>
          {Object.entries(PRIORITY).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </div>

      <div className="wo-grid">
        <div className="card table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Number</th>
                <th>Priority</th>
                <th>Asset</th>
                <th>Fault</th>
                <th>Status</th>
                {!mine && <th>Assigned to</th>}
              </tr>
            </thead>
            <tbody>
              {loading && <tr><td colSpan={mine ? 5 : 6} className="empty">Loading…</td></tr>}

              {!loading && rows.length === 0 && (
                <tr>
                  <td colSpan={mine ? 5 : 6} className="empty">
                    {mine && !status ? 'Nothing is assigned to you right now.' : 'No work orders match.'}
                  </td>
                </tr>
              )}

              {!loading && rows.map((w) => (
                <tr
                  key={w.id}
                  onClick={() => void open(w.id)}
                  className={selected?.id === w.id ? 'row-selected' : 'row-clickable'}
                >
                  <td className="mono">{w.number}</td>
                  <td><StatusPill look={PRIORITY_LOOK[w.priority]}>{PRIORITY[w.priority]}</StatusPill></td>
                  <td className="mono">{w.assetTag}</td>
                  <td className="truncate">{w.faultDescription}</td>
                  <td><StatusPill look={WORK_ORDER_LOOK[w.status]}>{STATUS[w.status]}</StatusPill></td>
                  {/* On "my work" every row is the reader's own: a column saying so is noise. */}
                  {!mine && <td>{w.assignedToName ?? <span className="muted">Unassigned</span>}</td>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="card">
          {selected ? (
            <Detail order={selected} canAssign={canAssign} act={act} />
          ) : (
            <p className="muted" style={{ margin: 0 }}>Select a work order to see its history.</p>
          )}
        </div>
      </div>
    </div>
  );
}

function Detail({
  order,
  canAssign,
  act,
}: {
  order: WorkOrderDetail;
  canAssign: boolean;
  act: (fn: () => Promise<unknown>) => Promise<void>;
}) {
  const [note, setNote] = useState('');
  const [resolution, setResolution] = useState('');
  const [resolveProblem, setResolveProblem] = useState<string | null>(null);
  const resolutionBox = useRef<HTMLTextAreaElement>(null);
  const [staff, setStaff] = useState<{ id: number; fullName: string }[]>([]);
  const [spareParts, setSpareParts] = useState<SparePartOption[]>([]);
  const [partId, setPartId] = useState('');
  const [partQty, setPartQty] = useState('1');
  const [partError, setPartError] = useState<string | null>(null);
  const [photoFiles, setPhotoFiles] = useState<File[]>([]);
  const [photoError, setPhotoError] = useState<string | null>(null);
  const [uploadingPhotos, setUploadingPhotos] = useState(false);
  const photoInput = useRef<HTMLInputElement>(null);

  // Only the people who can actually be sent to a machine. Loaded here rather
  // than with the list because it is only needed once a work order is open,
  // and only for someone who may assign - the staff list is an Admin route
  // now, so asking for it as an Employee is a guaranteed 403.
  useEffect(() => {
    if (!canAssign) return;
    void (async () => {
      try {
        setStaff(await api.get<{ id: number; fullName: string }[]>('/api/users'));
      } catch {
        // Assignment degrades to unavailable rather than breaking the page.
      }
    })();
  }, [canAssign]);

  const isTerminal = order.allowedTransitions.length === 0;

  // The shelf to pick from, loaded once a ticket is open rather than with
  // every row in the list - most work orders are looked at without anyone
  // drawing a part against them.
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

  // Local, the same as resolveProblem below: what is missing before the
  // request is even sent. A server-side refusal (not enough stock, a
  // retired part) surfaces through the page's own error banner, like every
  // other action on this ticket.
  function usePart() {
    const id = Number(partId);
    const qty = Number(partQty);
    if (!id || !Number.isInteger(qty) || qty < 1) {
      setPartError('Choose a part and a quantity of at least 1.');
      return;
    }
    setPartError(null);
    void act(() => api.post(`/api/work-orders/${order.id}/parts`, { sparePartId: id, quantityUsed: qty }));
    setPartId('');
    setPartQty('1');
  }

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
        setPhotoError(`A work order can have at most ${MAX_PHOTOS} photos.`);
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
    setUploadingPhotos(true);
    setPhotoError(null);
    await act(async () => {
      const form = new FormData();
      photoFiles.forEach((f) => form.append('files', f, f.name));
      await api.postForm(`/api/work-orders/${order.id}/photos`, form);
      // Only cleared once the upload actually succeeded - a refusal (too many
      // photos, wrong type) leaves the picked files so nothing is silently lost.
      setPhotoFiles([]);
    });
    setUploadingPhotos(false);
  }

  async function removePhoto(photo: WorkOrderPhoto) {
    if (!window.confirm(`Remove "${photo.fileName}"? It is deleted from the system. The removal is recorded in the audit log.`)) {
      return;
    }
    void act(() => api.del(`/api/work-orders/photos/${photo.id}`));
  }

  // The button stays clickable when the box is empty and says what is missing.
  // A greyed-out button explained only by a placeholder reads as broken: the
  // engineer clicks it, nothing happens, and nothing says why.
  function resolve() {
    if (!resolution.trim()) {
      setResolveProblem(
        'Say what was wrong and what you did before resolving. It goes on the service report.',
      );
      resolutionBox.current?.focus();
      return;
    }
    void act(() =>
      api.post(`/api/work-orders/${order.id}/resolve`, {
        resolutionNotes: resolution,
      }),
    );
  }

  // Only what the server will actually accept. Offering a button that
  // returns 409 teaches people to distrust the buttons.
  const transitions = order.allowedTransitions.filter((s) => s !== 50);
  const canResolve = order.allowedTransitions.includes(50);

  return (
    <div className="stack">
      <div>
        <h2 style={{ margin: 0, fontSize: '1.2rem' }}>{order.number}</h2>
        <p className="muted" style={{ margin: '0.2rem 0 0' }}>
          {order.assetTag} · {order.equipmentTypeName} · {order.locationName}
        </p>
      </div>

      <div className="row">
        <StatusPill look={WORK_ORDER_LOOK[order.status]}>{STATUS[order.status]}</StatusPill>
        <StatusPill look={PRIORITY_LOOK[order.priority]}>{PRIORITY[order.priority]}</StatusPill>
        {order.downtimeHours !== null && (
          <span className="pill">
            {order.stillDown ? `Down for ${formatHours(order.downtimeHours)} so far` : `Was down ${formatHours(order.downtimeHours)}`}
          </span>
        )}
      </div>

      <div>
        <div className="muted" style={{ fontSize: '0.8rem' }}>Reported fault</div>
        <p style={{ margin: '0.2rem 0 0' }}>{order.faultDescription}</p>
      </div>

      {order.resolutionNotes && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem' }}>Work carried out</div>
          <p style={{ margin: '0.2rem 0 0' }}>{order.resolutionNotes}</p>
        </div>
      )}

      {(order.partsUsed.length > 0 || !isTerminal) && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Parts used</div>
          {order.partsUsed.length > 0 && (
            <ul className="timeline">
              {order.partsUsed.map((p) => (
                <li key={p.id}>
                  <span className="mono">{p.quantityUsed}× {p.partNumber}</span> — {p.name}
                  {p.unitCostAtUse !== null && (
                    <span className="muted"> ({rupees.format(p.unitCostAtUse * p.quantityUsed)})</span>
                  )}
                  {!isTerminal && (
                    <button
                      className="btn btn-quiet"
                      style={{ marginLeft: '0.5rem' }}
                      onClick={() => void act(() => api.del(`/api/work-orders/${order.id}/parts/${p.id}`))}
                    >
                      Undo
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}

          {!isTerminal && (
            <div className="row" style={{ marginTop: order.partsUsed.length > 0 ? '0.5rem' : 0 }}>
              <select
                aria-label="Part to record"
                className="field"
                style={{ maxWidth: '16rem' }}
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
              <button className="btn" onClick={usePart}>Record</button>
            </div>
          )}
          {partError && <p className="alert alert-error" role="alert">{partError}</p>}
        </div>
      )}

      {(order.photos.length > 0 || !isTerminal) && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Photos</div>

          {order.photos.length > 0 && (
            <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.35rem' }}>
              {order.photos.map((p) => (
                <li key={p.id} className="row" style={{ justifyContent: 'space-between', gap: '0.5rem', flexWrap: 'wrap' }}>
                  <span>
                    {p.fileName}
                    <span className="muted"> · {photoSize(p.sizeBytes)} · {p.uploadedBy ?? 'unknown'}</span>
                  </span>
                  <span className="row" style={{ gap: '0.4rem' }}>
                    <button
                      className="btn btn-quiet"
                      onClick={() =>
                        void api.view(`/api/work-orders/photos/${p.id}`)
                          .catch((err: unknown) => setPhotoError(err instanceof Error ? err.message : 'Could not open the photo.'))
                      }
                    >
                      View
                    </button>
                    {canAssign && (
                      <button className="btn btn-quiet" aria-label={`Remove ${p.fileName}`} onClick={() => void removePhoto(p)}>
                        Remove
                      </button>
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}

          {!isTerminal && order.photos.length + photoFiles.length < MAX_PHOTOS && (
            <div className="stack" style={{ marginTop: order.photos.length > 0 ? '0.5rem' : 0 }}>
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
                    <li key={`${f.name}-${i}`} className="row" style={{ justifyContent: 'space-between', gap: '0.5rem' }}>
                      <span>{f.name} <span className="muted">· {photoSize(f.size)}</span></span>
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
                  <button className="btn" disabled={uploadingPhotos} onClick={() => void uploadPhotos()}>
                    {uploadingPhotos ? 'Saving…' : 'Save photos'}
                  </button>
                </div>
              )}
            </div>
          )}
          {photoError && <p className="alert alert-error" role="alert">{photoError}</p>}
        </div>
      )}

      {order.notes.length > 0 && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Timeline</div>
          <ul className="timeline">
            {order.notes.map((n) => (
              <li key={n.id}>
                <span className="muted mono">{n.createdAtUtc.slice(0, 16).replace('T', ' ')}</span>
                {n.statusAfter !== null && (
                  <span className="pill" style={{ marginLeft: '0.4rem' }}>{STATUS[n.statusAfter]}</span>
                )}
                <div>{n.body}</div>
              </li>
            ))}
          </ul>
        </div>
      )}

      {transitions.length > 0 && (
        <div className="row">
          {transitions.map((s) => (
            <button
              key={s}
              className="btn"
              onClick={() =>
                void act(() => api.post(`/api/work-orders/${order.id}/status`, { status: s, note: note || undefined }))
              }
            >
              {STATUS[s]}
            </button>
          ))}

          {/* Was hardcoded to user id 1 — not "me", just whoever happened to
              be first — because until staff accounts existed there was nobody
              else to assign to. Now it offers the actual engineers. */}
          {canAssign && order.assignedToUserId === null && staff.length > 0 && (
            <select
              aria-label="Assign to"
              className="field"
              style={{ maxWidth: '14rem' }}
              defaultValue=""
              onChange={(e) => {
                const id = Number(e.target.value);
                if (id) void act(() => api.post(`/api/work-orders/${order.id}/assign`, { assignedToUserId: id }));
              }}
            >
              <option value="">Assign to…</option>
              {staff.map((p) => (
                <option key={p.id} value={p.id}>{p.fullName}</option>
              ))}
            </select>
          )}
        </div>
      )}

      {canResolve && (
        <div className="stack">
          <textarea
            ref={resolutionBox}
            className="grow"
            rows={3}
            placeholder="What was wrong and what you did (required to resolve)"
            aria-label="What was wrong and what you did"
            aria-invalid={resolveProblem !== null}
            value={resolution}
            onChange={(e) => {
              setResolution(e.target.value);
              if (e.target.value.trim()) setResolveProblem(null);
            }}
          />
          {resolveProblem && <p className="alert alert-error" role="alert">{resolveProblem}</p>}
          <button className="btn btn-primary" onClick={resolve}>
            Resolve and return to service
          </button>
        </div>
      )}

      <div className="row">
        <input
          className="grow"
          placeholder="Add a note"
          aria-label="Add a note"
          value={note}
          onChange={(e) => setNote(e.target.value)}
        />
        <button
          className="btn"
          disabled={!note.trim()}
          onClick={() =>
            void act(async () => {
              await api.post(`/api/work-orders/${order.id}/notes`, { body: note });
              setNote('');
            })
          }
        >
          Add
        </button>
      </div>

      <button
        className="btn"
        onClick={() => api.download(`/api/reports/work-orders/${order.id}/report.pdf`, `${order.number}.pdf`)}
      >
        Download service report
      </button>
    </div>
  );
}

function ReportForm({
  initial,
  onCancel,
  onDone,
  onError,
}: {
  /** A machine already chosen, when the form was opened from its own page. */
  initial: { id: number; label: string } | null;
  onCancel: () => void;
  onDone: () => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const [equipmentId, setEquipmentId] = useState<number | null>(initial?.id ?? null);
  const [fault, setFault] = useState('');
  const [priority, setPriority] = useState(20);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      await api.post('/api/work-orders', {
        equipmentId,
        faultDescription: fault,
        priority,
      });
      await onDone();
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not report the fault.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Report a fault</h2>

      <div className="filters">
        <EquipmentPicker value={equipmentId} onChange={setEquipmentId} initialLabel={initial?.label} />

        <label className="field">
          <span>Priority</span>
          <select value={priority} onChange={(e) => setPriority(Number(e.target.value))}>
            {Object.entries(PRIORITY).map(([v, label]) => (
              <option key={v} value={v}>{label}</option>
            ))}
          </select>
        </label>
      </div>

      <label className="field">
        <span>What is wrong</span>
        <textarea
          rows={3}
          value={fault}
          onChange={(e) => setFault(e.target.value)}
          placeholder="Describe the fault as the ward reported it"
          required
        />
      </label>

      {/* Reporting a fault is saying the machine is not working: its downtime is counted from this
          moment, not from when an engineer arrives, until it is resolved and back in use. */}
      <p className="muted" style={{ margin: 0 }}>
        The machine is counted as down from the moment you report it, until it is fixed and back in use.
      </p>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || equipmentId === null || !fault.trim()}>
          {busy ? 'Reporting…' : 'Report fault'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
