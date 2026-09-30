import { useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { formatDate } from '../time';

/**
 * Renewing a machine's insurance.
 *
 * The policy in force is kept in the machine's insurance history and the new one takes its
 * place, so what a machine has cost in insurance adds up over the years. Correcting a mistake in
 * the policy is Edit, which overwrites; a renewal is a second policy that was paid for.
 */
export function RenewInsuranceForm({
  equipmentId,
  currentProvider,
  currentExpiry,
  onCancel,
  onRenewed,
}: {
  equipmentId: number;
  currentProvider: string | null;
  /** The last day the policy being replaced covers, yyyy-mm-dd. */
  currentExpiry: string | null;
  onCancel: () => void;
  onRenewed: (message: string) => void | Promise<void>;
}) {
  const [provider, setProvider] = useState(currentProvider ?? '');
  const [policyNumber, setPolicyNumber] = useState('');
  const [expiry, setExpiry] = useState('');
  const [cost, setCost] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api.post(`/api/equipment/${equipmentId}/insurance/renew`, {
        insuranceProvider: provider.trim(),
        insurancePolicyNumber: policyNumber.trim() || null,
        insuranceExpiryDate: expiry,
        insuranceCost: cost.trim() === '' ? null : Number(cost),
      });
      await onRenewed('Insurance renewed. The earlier policy is kept in the insurance history.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not renew the insurance.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>Renew insurance</h2>
      <p className="muted" style={{ margin: 0 }}>
        The current policy{currentExpiry ? `, which covers the machine until ${formatDate(currentExpiry)},` : ''} is
        kept in the insurance history and its cost stays in what this machine has cost. Enter the new policy.
      </p>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters">
        <label className="field grow">
          <span>Insurer</span>
          <input
            aria-label="Insurer"
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            maxLength={200}
            required
            autoComplete="off"
          />
        </label>

        <label className="field">
          <span>Policy number (optional)</span>
          <input
            aria-label="Policy number"
            value={policyNumber}
            onChange={(e) => setPolicyNumber(e.target.value)}
            maxLength={100}
            autoComplete="off"
          />
        </label>

        <label className="field">
          <span>New policy covers until</span>
          <input
            type="date"
            aria-label="New policy covers until"
            value={expiry}
            min={currentExpiry ?? undefined}
            onChange={(e) => setExpiry(e.target.value)}
            required
          />
        </label>

        <label className="field">
          <span>Cost of the new policy, ₹ (optional)</span>
          <input
            type="number"
            aria-label="Cost of the new policy"
            value={cost}
            min={0}
            step="0.01"
            onChange={(e) => setCost(e.target.value)}
          />
        </label>
      </div>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || !provider.trim() || !expiry}>
          {busy ? 'Renewing…' : 'Renew insurance'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
