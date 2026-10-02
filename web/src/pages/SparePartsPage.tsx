import { useCallback, useEffect, useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { StatusPill } from '../StatusPill';
import { formatRupees } from '../money';
import { formatDate, todayAtHospital } from '../time';

/**
 * The biomedical department's own shelf of spares - fuses, tubing sets, sensor
 * probes, filters - counted, not tracked one by one, the way a real store
 * register works.
 *
 * Everyone reads it: an engineer checking whether a replacement is on the shelf
 * before promising a repair date is the point of keeping one. Only an
 * Administrator adds a line or adjusts a count, the same split as the
 * equipment register and equipment types.
 */

type Part = {
  id: number;
  partNumber: string;
  name: string;
  description: string | null;
  equipmentTypeId: number | null;
  equipmentTypeName: string | null;
  unit: string;
  quantityOnHand: number;
  unitCost: number | null;
  supplier: string | null;
  storageLocation: string | null;
  purchaseDate: string | null;
  warrantyMonths: number | null;
  /** Worked out by the server: the purchase date plus the warranty. */
  warrantyExpiryDate: string | null;
  notes: string | null;
  isActive: boolean;
  /** 0 is out, 1 to 5 is low: decided by the server, so every screen agrees. */
  stockStatus: 'ok' | 'low' | 'out';
};

type Paged = { items: Part[]; total: number; page: number; pageSize: number };
type Lookup = { id: number; code: string; name: string };

export function SparePartsPage() {
  const { can } = useAuth();
  const canAuthor = can(ROLES.admin);

  const [parts, setParts] = useState<Part[]>([]);
  const [total, setTotal] = useState(0);
  const [types, setTypes] = useState<Lookup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [editing, setEditing] = useState<Part | 'new' | null>(null);
  const [query, setQuery] = useState('');
  // "" for all stock, "out" for none left, "low" for one to five left.
  const [stock, setStock] = useState<'' | 'out' | 'low'>('');
  const [showInactive, setShowInactive] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const qs = new URLSearchParams();
      if (query.trim()) qs.set('q', query.trim());
      if (stock) qs.set('stock', stock);
      if (showInactive) qs.set('includeInactive', 'true');
      qs.set('pageSize', '200');

      const [p, t] = await Promise.all([
        api.get<Paged>(`/api/spare-parts?${qs.toString()}`),
        types.length > 0 ? Promise.resolve(types) : api.get<Lookup[]>('/api/lookups/equipment-types'),
      ]);
      setParts(p.items);
      setTotal(p.total);
      setTypes(t);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the spares register.');
    } finally {
      setLoading(false);
    }
    // types is intentionally left out: it is fetched once and reused, not refetched on every search.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query, stock, showInactive]);

  useEffect(() => {
    void load();
  }, [load]);

  const outCount = useMemo(() => parts.filter((p) => p.stockStatus === 'out' && p.isActive).length, [parts]);
  const lowCount = useMemo(() => parts.filter((p) => p.stockStatus === 'low' && p.isActive).length, [parts]);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Spare parts</h1>
          <p className="muted">
            {total.toLocaleString('en-IN')} part{total === 1 ? '' : 's'}
            {outCount > 0 && `, ${outCount} out of stock`}
            {lowCount > 0 && `, ${lowCount} low`}
          </p>
        </div>
        {canAuthor && editing === null && (
          <button
            className="btn btn-primary"
            onClick={() => {
              setSaved(null);
              setEditing('new');
            }}
          >
            Add a part
          </button>
        )}
      </header>

      {saved && <p className="alert alert-ok" role="status">{saved}</p>}
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {editing !== null && (
        <PartForm
          key={editing === 'new' ? 'new' : editing.id}
          editing={editing === 'new' ? undefined : editing}
          types={types}
          onCancel={() => setEditing(null)}
          onSaved={async (message) => {
            setEditing(null);
            setSaved(message);
            await load();
          }}
        />
      )}

      <div className="card row" style={{ flexWrap: 'wrap', gap: '0.75rem', alignItems: 'center' }}>
        <input
          className="field"
          style={{ flex: '1 1 16rem' }}
          type="search"
          aria-label="Search spare parts"
          placeholder="Search by part number, name or supplier"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <select
          className="field"
          aria-label="Filter by stock"
          value={stock}
          onChange={(e) => setStock(e.target.value as '' | 'out' | 'low')}
        >
          <option value="">All stock</option>
          <option value="out">Out of stock</option>
          <option value="low">Low stock</option>
        </select>
        <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          <span>Show retired parts</span>
        </label>
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Part</th>
              <th>Used in</th>
              <th className="num">Stock</th>
              <th>Supplier</th>
              <th>Bought and warranty</th>
              <th>Status</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={7} className="empty">Loading…</td></tr>}
            {!loading && parts.length === 0 && (
              <tr>
                <td colSpan={7} className="empty">
                  {total === 0 && !query && !stock
                    ? 'There are no spare parts on the register yet.'
                    : 'No part matches that.'}
                </td>
              </tr>
            )}
            {parts.map((p) => (
              <tr key={p.id}>
                <td>
                  <strong>{p.name}</strong>
                  <div className="mono muted">{p.partNumber}</div>
                  {p.storageLocation && <div className="muted">{p.storageLocation}</div>}
                </td>
                <td>{p.equipmentTypeName ?? <span className="muted">Not specific to one type</span>}</td>
                <td className="num">
                  {p.quantityOnHand} {p.unit}
                </td>
                <td>
                  {p.supplier ?? <span className="muted">—</span>}
                  {p.unitCost != null && <div className="muted">{formatRupees(p.unitCost)} / {p.unit}</div>}
                </td>
                <td>
                  {p.purchaseDate ? (
                    <div>Bought {formatDate(p.purchaseDate)}</div>
                  ) : (
                    <span className="muted">—</span>
                  )}
                  {p.warrantyExpiryDate && p.warrantyMonths !== null && (
                    <div>
                      <StatusPill tone={p.warrantyExpiryDate >= todayAtHospital() ? 'success' : 'neutral'}>
                        {p.warrantyExpiryDate >= todayAtHospital() ? 'In warranty until' : 'Warranty ended'}{' '}
                        {formatDate(p.warrantyExpiryDate)}
                      </StatusPill>
                      <div className="muted">{p.warrantyMonths} {p.warrantyMonths === 1 ? 'month' : 'months'} from purchase</div>
                    </div>
                  )}
                </td>
                <td>
                  {!p.isActive ? (
                    <StatusPill tone="neutral">Retired</StatusPill>
                  ) : p.stockStatus === 'out' ? (
                    <StatusPill tone="danger">Out of stock</StatusPill>
                  ) : p.stockStatus === 'low' ? (
                    <StatusPill tone="warning">Low stock</StatusPill>
                  ) : (
                    <StatusPill tone="success">OK</StatusPill>
                  )}
                </td>
                <td>
                  {canAuthor && (
                    <button className="btn btn-quiet" onClick={() => { setSaved(null); setEditing(p); }}>
                      Edit
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function PartForm({
  editing,
  types,
  onCancel,
  onSaved,
}: {
  editing?: Part;
  types: Lookup[];
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
}) {
  const [partNumber, setPartNumber] = useState(editing?.partNumber ?? '');
  const [name, setName] = useState(editing?.name ?? '');
  const [description, setDescription] = useState(editing?.description ?? '');
  const [equipmentTypeId, setEquipmentTypeId] = useState(editing?.equipmentTypeId?.toString() ?? '');
  const [unit, setUnit] = useState(editing?.unit ?? 'pcs');
  const [quantityOnHand, setQuantityOnHand] = useState(editing?.quantityOnHand.toString() ?? '0');
  const [unitCost, setUnitCost] = useState(editing?.unitCost?.toString() ?? '');
  const [supplier, setSupplier] = useState(editing?.supplier ?? '');
  const [storageLocation, setStorageLocation] = useState(editing?.storageLocation ?? '');
  const [purchaseDate, setPurchaseDate] = useState(editing?.purchaseDate ?? '');
  const [warrantyMonths, setWarrantyMonths] = useState(editing?.warrantyMonths?.toString() ?? '');
  const [notes, setNotes] = useState(editing?.notes ?? '');
  const [isActive, setIsActive] = useState(editing?.isActive ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    // Said here, before the request, so it is the field that is pointed at and not a banner.
    if (warrantyMonths.trim() && !purchaseDate) {
      setError('Give the date of purchase for the warranty to run from.');
      return;
    }

    setBusy(true);
    try {
      const body = {
        partNumber: partNumber.trim(),
        name: name.trim(),
        description: description.trim() || null,
        equipmentTypeId: equipmentTypeId ? Number(equipmentTypeId) : null,
        unit: unit.trim() || 'pcs',
        quantityOnHand: Number(quantityOnHand),
        unitCost: unitCost.trim() ? Number(unitCost) : null,
        supplier: supplier.trim() || null,
        storageLocation: storageLocation.trim() || null,
        purchaseDate: purchaseDate || null,
        warrantyMonths: warrantyMonths.trim() ? Number(warrantyMonths) : null,
        notes: notes.trim() || null,
        isActive,
      };

      if (editing) {
        await api.put(`/api/spare-parts/${editing.id}`, body);
        await onSaved(`Saved ${body.name}.`);
      } else {
        await api.post('/api/spare-parts', body);
        await onSaved(`Added ${body.name} to the register.`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the part.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>{editing ? `Edit ${editing.partNumber}` : 'Add a part'}</h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters">
        <label className="field">
          <span>Part number</span>
          <input
            className="mono"
            value={partNumber}
            onChange={(e) => setPartNumber(e.target.value)}
            placeholder="e.g. FUSE-2A-T"
            maxLength={64}
            disabled={!!editing}
            required
          />
          {editing && <span className="muted">The number on the bin label never changes here.</span>}
        </label>

        <label className="field grow">
          <span>Name</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. 2A slow-blow fuse"
            maxLength={200}
            required
          />
        </label>
      </div>

      <label className="field">
        <span>Description (optional)</span>
        <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
      </label>

      <div className="filters">
        <label className="field grow">
          <span>Used in</span>
          <select value={equipmentTypeId} onChange={(e) => setEquipmentTypeId(e.target.value)}>
            <option value="">Not specific to one type</option>
            {types.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>Unit</span>
          <input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="pcs" maxLength={20} />
        </label>
      </div>

      <div className="filters">
        <label className="field">
          <span>Quantity on hand</span>
          <input
            type="number"
            min={0}
            value={quantityOnHand}
            onChange={(e) => setQuantityOnHand(e.target.value)}
            required
          />
          <span className="muted">0 shows as Out of stock, 1 to 5 as Low stock.</span>
        </label>

        <label className="field">
          <span>Cost per unit, ₹ (optional)</span>
          <input type="number" min={0} step="0.01" value={unitCost} onChange={(e) => setUnitCost(e.target.value)} />
        </label>
      </div>

      <div className="filters">
        <label className="field grow">
          <span>Supplier (optional)</span>
          <input value={supplier} onChange={(e) => setSupplier(e.target.value)} maxLength={200} />
        </label>

        <label className="field grow">
          <span>Where it's kept (optional)</span>
          <input
            value={storageLocation}
            onChange={(e) => setStorageLocation(e.target.value)}
            placeholder="e.g. Store Room A, Rack 3"
            maxLength={200}
          />
        </label>
      </div>

      <div className="filters">
        <label className="field">
          <span>Date of purchase (optional)</span>
          <input
            type="date"
            value={purchaseDate}
            max={todayAtHospital()}
            onChange={(e) => setPurchaseDate(e.target.value)}
          />
          <span className="muted">When this stock was bought.</span>
        </label>

        <label className="field">
          <span>Warranty, months (optional)</span>
          <input
            type="number"
            min={1}
            max={600}
            step={1}
            value={warrantyMonths}
            onChange={(e) => setWarrantyMonths(e.target.value)}
            placeholder="e.g. 12"
          />
          <span className="muted">How long the supplier covers it, from the date of purchase.</span>
        </label>
      </div>

      <label className="field">
        <span>Notes (optional)</span>
        <input value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />
      </label>

      {editing && (
        <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
          <span>Active — uncheck to retire this part without deleting its history</span>
        </label>
      )}

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Saving…' : editing ? 'Save' : 'Add part'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
