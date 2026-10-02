import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { EquipmentForm } from './EquipmentForm';
import { StatusPill } from '../StatusPill';
import { CRITICALITY_LABEL, CRITICALITY_LOOK, EQUIPMENT_LABEL, EQUIPMENT_LOOK } from '../statusTones';
import { useFeatures } from '../features';

type Equipment = {
  id: number;
  assetTag: string;
  serialNumber: string | null;
  equipmentTypeId: number;
  equipmentTypeName: string;
  locationId: number;
  locationName: string;
  manufacturer: string | null;
  model: string | null;
  status: number;
  criticality: number | null;
  purchaseDate: string | null;
  warrantyExpiryDate: string | null;
};

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };
type Lookup = { id: number; code: string; name: string };
type LocationLookup = Lookup & { depth: number; level: number };

const PAGE_SIZE = 25;

export function EquipmentListPage() {
  const features = useFeatures();
  const { can } = useAuth();
  const canPrint = can(ROLES.admin);
  const canEdit = can(ROLES.admin);
  const [adding, setAdding] = useState(false);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [printing, setPrinting] = useState(false);
  // The filters live in the address as well, so a dashboard tile can link to "Under repair", the
  // browser's Back button returns to the list as it was left, and a filtered list can be shared.
  const [params, setParams] = useSearchParams();
  const [query, setQuery] = useState(params.get('q') ?? '');
  const [debounced, setDebounced] = useState(params.get('q') ?? '');
  const [locationId, setLocationId] = useState(params.get('location') ?? '');
  const [typeId, setTypeId] = useState(params.get('type') ?? '');
  const [status, setStatus] = useState(params.get('status') ?? '');
  const [page, setPage] = useState(Math.max(1, Number(params.get('page')) || 1));

  const [data, setData] = useState<Paged<Equipment> | null>(null);
  const [types, setTypes] = useState<Lookup[]>([]);
  const [locations, setLocations] = useState<LocationLookup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Debounced so typing an asset tag does not fire a request per keystroke
  // against a hospital PC also running Postgres.
  useEffect(() => {
    // Nothing to wait for when what is typed is what is already applied - which is also what
    // keeps a page number read from the address from being thrown back to 1 on arrival.
    if (query === debounced) return;
    const t = setTimeout(() => {
      setDebounced(query);
      setPage(1);
    }, 300);
    return () => clearTimeout(t);
  }, [query, debounced]);

  // Said in the address whenever a filter or the page changes. Replaced rather than pushed, so
  // typing an asset tag is not one history entry per letter.
  useEffect(() => {
    const next = new URLSearchParams();
    if (debounced.trim()) next.set('q', debounced.trim());
    if (locationId) next.set('location', locationId);
    if (typeId) next.set('type', typeId);
    if (status) next.set('status', status);
    if (page > 1) next.set('page', String(page));
    setParams(next, { replace: true });
  }, [debounced, locationId, typeId, status, page, setParams]);

  useEffect(() => {
    (async () => {
      try {
        const [t, l] = await Promise.all([
          api.get<Lookup[]>('/api/lookups/equipment-types'),
          api.get<LocationLookup[]>('/api/lookups/locations'),
        ]);
        setTypes(t);
        setLocations(l);
      } catch {
        /* filters degrade to text search only */
      }
    })();
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (debounced.trim()) params.set('q', debounced.trim());
      if (locationId) params.set('locationId', locationId);
      if (typeId) params.set('equipmentTypeId', typeId);
      if (status) params.set('status', status);

      setData(await api.get<Paged<Equipment>>(`/api/equipment?${params}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load equipment.');
    } finally {
      setLoading(false);
    }
  }, [debounced, locationId, typeId, status, page]);

  // Fetching from the server is precisely the "synchronising with an
  // external system" case this rule exempts; the setState calls inside load
  // are loading and result transitions, not derived render state.
  useEffect(() => {
    void load();
  }, [load]);

  const totalPages = useMemo(
    () => (data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1),
    [data],
  );

  const hasFilters = Boolean(debounced || locationId || typeId || status);

  function toggle(id: number) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function toggleAll() {
    const ids = data?.items.map((e) => e.id) ?? [];
    const allOn = ids.length > 0 && ids.every((id) => selected.has(id));
    setSelected((prev) => {
      const next = new Set(prev);
      // Only the current page is affected. Clearing a selection the user
      // built across several pages because they clicked the header checkbox
      // would be its own small betrayal.
      for (const id of ids) {
        if (allOn) next.delete(id);
        else next.add(id);
      }
      return next;
    });
  }

  async function print(kind: 'sheet' | 'zpl') {
    if (selected.size === 0) return;
    setPrinting(true);
    setError(null);
    try {
      const ids = [...selected];
      if (kind === 'sheet') {
        await api.downloadPost('/api/labels/sheet', { equipmentIds: ids }, 'asset-tags.pdf');
      } else {
        await api.downloadPost('/api/labels/zpl', { equipmentIds: ids }, 'asset-tags.zpl');
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not generate labels.');
    } finally {
      setPrinting(false);
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Equipment register</h1>
          {data && (
            <p className="muted">
              {data.total.toLocaleString('en-IN')} {data.total === 1 ? 'asset' : 'assets'}
              {hasFilters ? ' matching' : ''}
            </p>
          )}
        </div>
        {canEdit && selected.size === 0 && (
          <button className="btn btn-primary" onClick={() => setAdding(true)}>
            Add a machine
          </button>
        )}
        {canPrint && selected.size > 0 && (
          <div className="row">
            <span className="muted" style={{ alignSelf: 'center' }}>
              {selected.size} selected
            </span>
            <button className="btn" disabled={printing} onClick={() => void print('sheet')}>
              Print label sheet
            </button>
            <button className="btn" disabled={printing} onClick={() => void print('zpl')}>
              Zebra (ZPL)
            </button>
            <button className="btn btn-quiet" onClick={() => setSelected(new Set())}>
              Clear
            </button>
          </div>
        )}
      </header>

      {adding && (
        <EquipmentForm
          onCancel={() => setAdding(false)}
          onSaved={async () => {
            setAdding(false);
            await load();
          }}
        />
      )}

      <div className="filters card">
        <input
          className="grow"
          placeholder="Search asset tag, serial, manufacturer or model…"
          aria-label="Search the register"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />

        <select aria-label="Filter by location" value={locationId} onChange={(e) => { setLocationId(e.target.value); setPage(1); }}>
          <option value="">All locations</option>
          {locations.map((l) => (
            <option key={l.id} value={l.id}>
              {/* Indent by depth so the tree shape survives a flat select. */}
              {' '.repeat(l.depth * 3)}
              {l.name}
            </option>
          ))}
        </select>

        <select aria-label="Filter by type" value={typeId} onChange={(e) => { setTypeId(e.target.value); setPage(1); }}>
          <option value="">All types</option>
          {types.map((t) => (
            <option key={t.id} value={t.id}>{t.name}</option>
          ))}
        </select>

        <select aria-label="Filter by status" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
          <option value="">Any status</option>
          {Object.entries(EQUIPMENT_LABEL).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </div>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              {canPrint && (
                <th style={{ width: '1%' }}>
                  <input
                    type="checkbox"
                    aria-label="Select all on this page"
                    checked={
                      (data?.items.length ?? 0) > 0 &&
                      (data?.items ?? []).every((e) => selected.has(e.id))
                    }
                    onChange={toggleAll}
                  />
                </th>
              )}
              <th>Asset tag</th>
              <th>Type</th>
              <th>Location</th>
              <th>Manufacturer</th>
              <th>Serial</th>
              <th>Status</th>
              <th>Criticality</th>
            </tr>
          </thead>
          <tbody>
            {loading && (
              <tr><td colSpan={canPrint ? 8 : 7} className="empty">Loading…</td></tr>
            )}

            {!loading && data?.items.length === 0 && (
              <tr>
                <td colSpan={canPrint ? 8 : 7} className="empty">
                  {hasFilters
                    ? 'No equipment matches these filters.'
                    : features?.import
                      ? 'The register is empty. Import a spreadsheet to get started.'
                      : canEdit ? 'The register is empty. Use Add a machine to get started.' : 'The register is empty.'}
                </td>
              </tr>
            )}

            {!loading && data?.items.map((e) => (
              <tr key={e.id}>
                {canPrint && (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={`Select ${e.assetTag}`}
                      checked={selected.has(e.id)}
                      onChange={() => toggle(e.id)}
                    />
                  </td>
                )}
                <td className="mono">
                  <Link to={`/equipment/${e.id}`}>{e.assetTag}</Link>
                </td>
                <td>{e.equipmentTypeName}</td>
                <td>{e.locationName}</td>
                <td>{e.manufacturer ?? <span className="muted">—</span>}</td>
                <td className="mono">{e.serialNumber ?? <span className="muted">—</span>}</td>
                <td>
                  <StatusPill look={EQUIPMENT_LOOK[e.status]}>{EQUIPMENT_LABEL[e.status] ?? 'Unknown'}</StatusPill>
                </td>
                <td>
                  {e.criticality != null ? (
                    <StatusPill look={CRITICALITY_LOOK[e.criticality]}>
                      {CRITICALITY_LABEL[e.criticality] ?? 'Unknown'}
                    </StatusPill>
                  ) : (
                    <span className="muted">Not set</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {data && data.total > data.pageSize && (
        <div className="pager">
          <button className="btn" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            Previous
          </button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="btn" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>
            Next
          </button>
        </div>
      )}
    </div>
  );
}
